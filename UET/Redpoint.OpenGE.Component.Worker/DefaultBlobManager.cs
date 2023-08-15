﻿namespace Redpoint.OpenGE.Component.Worker
{
    using Grpc.Core;
    using Microsoft.AspNetCore.Components.Forms;
    using Redpoint.OpenGE.Component.Worker.PchPortability;
    using Redpoint.OpenGE.Core;
    using Redpoint.OpenGE.Core.ReadableStream;
    using Redpoint.OpenGE.Core.WritableStream;
    using Redpoint.OpenGE.Protocol;
    using Redpoint.Reservation;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO.Compression;
    using System.IO.Hashing;
    using System.Threading.Tasks;

    internal class DefaultBlobManager : IBlobManager, IAsyncDisposable
    {
        private readonly IReservationManagerForOpenGE _reservationManagerForOpenGE;
        private readonly IPchPortability _pchPortability;
        private readonly ConcurrentDictionary<string, ServerCallContext> _remoteHostLocks;
        private readonly SemaphoreSlim _blobsReservationSemaphore;
        private IReservation? _blobsReservation;
        private bool _disposed;

        public DefaultBlobManager(
            IReservationManagerForOpenGE reservationManagerForOpenGE,
            IPchPortability pchPortability)
        {
            _reservationManagerForOpenGE = reservationManagerForOpenGE;
            _pchPortability = pchPortability;
            _remoteHostLocks = new ConcurrentDictionary<string, ServerCallContext>();
            _blobsReservationSemaphore = new SemaphoreSlim(1);
            _blobsReservation = null;
            _disposed = false;
        }

        private string ParsePeer(string peer)
        {
            return peer.Substring(0, peer.LastIndexOf(':'));
        }

        public void NotifyServerCallEnded(ServerCallContext context)
        {
            var peerHost = ParsePeer(context.Peer);
            if (_remoteHostLocks.TryGetValue(peerHost, out var compareContext))
            {
                if (context == compareContext)
                {
                    _remoteHostLocks.TryRemove(peerHost, out _);
                }
            }
        }

        public string ConvertAbsolutePathToBuildDirectoryPath(string targetDirectory, string absolutePath)
        {
            var remotifiedPath = absolutePath.RemotifyPath(false);
            if (remotifiedPath == null)
            {
                throw new InvalidOperationException($"Expected path '{absolutePath}' to be rooted and not a UNC path.");
            }

            return Path.Combine(targetDirectory, remotifiedPath.TrimStart(Path.DirectorySeparatorChar));
        }

        public async Task LayoutBuildDirectoryAsync(
            string targetDirectory,
            string shortenedTargetDirectory,
            InputFilesByBlobXxHash64 inputFiles,
            CancellationToken cancellationToken)
        {
            var blobsPath = await GetBlobsPath();

            var virtualised = new HashSet<string>(inputFiles.AbsolutePathsToVirtualContent);
            await Parallel.ForEachAsync(
                inputFiles.AbsolutePathsToBlobs.ToAsyncEnumerable(),
                cancellationToken,
                async (kv, cancellationToken) =>
                {
                    var targetPath = ConvertAbsolutePathToBuildDirectoryPath(
                        targetDirectory,
                        kv.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    if (virtualised.Contains(kv.Key))
                    {
                        // Grab the blob content, replace {__OPENGE_VIRTUAL_ROOT__} and then emit it.
                        using var sourceStream = new FileStream(
                            Path.Combine(blobsPath, kv.Value.HexString()),
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read);
                        using var reader = new StreamReader(sourceStream, leaveOpen: true);
                        var content = await reader.ReadToEndAsync(cancellationToken);
                        content = content.Replace("{__OPENGE_VIRTUAL_ROOT__}", shortenedTargetDirectory);
                        using var targetStream = new FileStream(
                            targetPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None);
                        using var writer = new StreamWriter(targetStream, leaveOpen: true);
                        await writer.WriteAsync(content);
                    }
                    else
                    {
                        File.Copy(
                            Path.Combine(blobsPath, kv.Value.HexString()),
                            targetPath,
                            true);
                        if (targetPath.EndsWith(".pch", StringComparison.InvariantCultureIgnoreCase))
                        {
                            try
                            {
                                await _pchPortability.ConvertPotentialPortablePchToPch(
                                    targetPath,
                                    shortenedTargetDirectory,
                                    cancellationToken);
                            }
                            catch
                            {
                                // @note: If we don't succeed in undoing portabilization,
                                // make sure we don't leave a broken PCH file.
                                File.Delete(targetPath);
                                throw;
                            }
                        }
                    }
                });
        }

        public async Task QueryMissingBlobsAsync(
            ServerCallContext context,
            QueryMissingBlobsRequest request, 
            IServerStreamWriter<ExecutionResponse> responseStream,
            CancellationToken cancellationToken)
        {
            // @note: We lock around the peer host, because we don't want
            // multiple tasks on the same host trying to send the same blobs.
            // We release this lock either:
            // - When QueryMissingBlobsAsync detects that there are no blobs
            //   for the dispatcher to send us.
            // - When SendCompressedBlobsAsync receives the final write for
            //   the compressed blobs.
            // - When the client disconnects for any reason, which is tracked
            //   on the ServerCallContext object and notified via
            //   NotifyServerCallEndedAsync.
            var peerHost = ParsePeer(context.Peer);
            while (!_remoteHostLocks.TryAdd(peerHost, context))
            {
                await Task.Delay(200 * Random.Shared.Next(1, 5), cancellationToken);
            }
            var retainLock = false;
            try
            {
                var blobsPath = await GetBlobsPath();

                var requested = new HashSet<long>(request.BlobXxHash64);
                var exists = new HashSet<long>();
                foreach (var hash in request.BlobXxHash64)
                {
                    var targetPath = Path.Combine(blobsPath, hash.HexString());
                    if (File.Exists(targetPath))
                    {
                        exists.Add(hash);
                    }
                }
                var notExists = new HashSet<long>(requested);
                notExists.ExceptWith(exists);

                var response = new QueryMissingBlobsResponse();
                response.MissingBlobXxHash64.AddRange(notExists);
                await responseStream.WriteAsync(new ExecutionResponse
                {
                    QueryMissingBlobs = response,
                });

                // @note: If we have no missing blobs, then the client will never call
                // SendCompressedBlobsAsync and thus we should not retain the lock or
                // it won't be released until the RPC is closed.
                retainLock = response.MissingBlobXxHash64.Count > 0;
            }
            finally
            {
                if (!retainLock)
                {
                    _remoteHostLocks.TryRemove(peerHost, out _);
                }
            }
        }

        public async Task SendCompressedBlobsAsync(
            ServerCallContext context,
            ExecutionRequest initialRequest,
            IAsyncStreamReader<ExecutionRequest> requestStream,
            IServerStreamWriter<ExecutionResponse> responseStream,
            CancellationToken cancellationToken)
        {
            var peerHost = ParsePeer(context.Peer);
            if (!_remoteHostLocks.TryGetValue(peerHost, out var currentContext) ||
                currentContext != context)
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    "You must not send SendCompressedBlobsRequest until QueryMissingBlobsResponse has arrived."));
            }

            var blobsPath = await GetBlobsPath();

            // At this point, we're in the lock for this peer, so we can safely
            // receive the data and stream it out.
            IDisposable? lockFile = null;
            FileStream? currentStream = null;
            long currentBytesRemaining = 0;
            XxHash64? hash = null;
            try
            {
                using (var destination = new SequentialVersion1Decoder(
                    (blobHash, blobLength) =>
                    {
                        if (lockFile != null)
                        {
                            throw new InvalidOperationException();
                        }
                        lockFile = LockFile.TryObtainLock(
                            Path.Combine(blobsPath, blobHash.HexString() + ".lock"));
                        while (lockFile == null)
                        {
                            // @hack: This is super questionable, but we know that
                            // the blob writing (and consequently the lock file
                            // duration) in other request threads is *not* asynchronous,
                            // The lock will never be held over an async yield even
                            // across RPCs, so sleeping the current thread while another
                            // RPC's thread completes writing to the file is probably
                            // safe.
                            Thread.Sleep(200);
                        }
                        hash = new XxHash64();
                        currentStream = new FileStream(
                            Path.Combine(blobsPath, blobHash.HexString() + ".tmp"),
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None);
                        currentStream.SetLength(blobLength);
                        currentBytesRemaining = blobLength;
                    },
                    (blobHash, blobOffset, buffer, bufferOffset, bufferCount) =>
                    {
                        // @note: We don't seek the file streams because we know we're writing sequentially.
                        currentStream!.Write(buffer, bufferOffset, bufferCount);
                        hash!.Append(new Span<byte>(buffer, bufferOffset, bufferCount));
                        currentBytesRemaining -= bufferCount;
                        if (currentBytesRemaining < 0)
                        {
                            throw new InvalidOperationException();
                        }
                        else if (currentBytesRemaining == 0)
                        {
                            currentStream.Flush();
                            currentStream.Dispose();
                            currentStream = null;
                            var invalid = false;
                            if (BitConverter.ToInt64(hash.GetCurrentHash()) == blobHash)
                            {
                                File.Move(
                                    Path.Combine(blobsPath, blobHash.HexString() + ".tmp"),
                                    Path.Combine(blobsPath, blobHash.HexString()),
                                    true);
                            }
                            else
                            {
                                try
                                {
                                    File.Delete(
                                        Path.Combine(blobsPath, blobHash.HexString() + ".tmp"));
                                }
                                catch
                                {
                                }
                                invalid = true;
                            }
                            if (lockFile != null)
                            {
                                lockFile.Dispose();
                                lockFile = null;
                            }
                            if (invalid)
                            {
                                throw new RpcException(new Status(
                                    StatusCode.InvalidArgument,
                                    "The received blob did not hash to the hash it was associated with."));
                            }
                        }
                    }))
                {
                    using (var source = new SendCompressedBlobsReadableBinaryChunkStream(
                        initialRequest,
                        requestStream))
                    {
                        using (var decompressor = new BrotliStream(source, CompressionMode.Decompress))
                        {
                            await decompressor.CopyToAsync(destination);
                        }
                    }
                }

                await responseStream.WriteAsync(new ExecutionResponse
                {
                    SendCompressedBlobs = new SendCompressedBlobsResponse(),
                }, cancellationToken);
            }
            finally
            {
                if (currentStream != null)
                {
                    currentStream.Flush();
                    currentStream.Dispose();
                    currentStream = null;
                }
                if (lockFile != null)
                {
                    lockFile.Dispose();
                    lockFile = null;
                }
                if (hash != null)
                {
                    hash = null;
                }

                // Release the lock to allow other requests from the same host
                // to come through.
                _remoteHostLocks.TryRemove(peerHost, out _);
            }
        }

        public async Task<OutputFilesByBlobXxHash64> CaptureOutputBlobsFromBuildDirectoryAsync(
            string targetDirectory,
            string shortenedTargetDirectory,
            IEnumerable<string> outputAbsolutePaths,
            CancellationToken cancellationToken)
        {
            var blobsPath = await GetBlobsPath();

            var results = new ConcurrentDictionary<string, long>();

            var virtualised = new HashSet<string>(outputAbsolutePaths);
            await Parallel.ForEachAsync(
                outputAbsolutePaths.ToAsyncEnumerable(),
                cancellationToken,
                async (path, cancellationToken) =>
                {
                    var targetPath = ConvertAbsolutePathToBuildDirectoryPath(
                        targetDirectory,
                        path);
                    if (File.Exists(targetPath))
                    {
                        if (targetPath.EndsWith(".pch", StringComparison.InvariantCultureIgnoreCase))
                        {
                            try
                            {
                                await _pchPortability.ConvertPchToPortablePch(
                                    targetPath,
                                    shortenedTargetDirectory,
                                    cancellationToken);
                            }
                            catch
                            {
                                // @note: If we don't succeed in doing portabilization,
                                // make sure we don't leave a broken PCH file.
                                File.Delete(targetPath);
                                throw;
                            }
                        }

                        var fileHash = (await XxHash64Helpers.HashFile(targetPath, cancellationToken)).hash;
                        var blobPath = Path.Combine(blobsPath, fileHash.HexString());
                        results[path] = fileHash;
                        if (!File.Exists(blobPath))
                        {
                            var lockFile = LockFile.TryObtainLock(
                                Path.Combine(blobsPath, fileHash.HexString() + ".lock"));
                            while (lockFile == null)
                            {
                                // @hack: This is super questionable, but we know that
                                // the file copy below is *not* asynchronous, nor is blob
                                // writing in other request threads.
                                //
                                // The lock will never be held over an async yield even
                                // across RPCs, so sleeping the current thread while another
                                // RPC's thread completes writing to the file is probably
                                // safe.
                                Thread.Sleep(200);
                            }
                            try
                            {
                                if (!File.Exists(blobPath))
                                {
                                    File.Copy(
                                        targetPath,
                                        blobPath,
                                        true);
                                }
                            }
                            finally
                            {
                                lockFile.Dispose();
                            }
                        }
                    }
                });

            return new OutputFilesByBlobXxHash64
            {
                AbsolutePathsToBlobs = { results }
            };
        }

        public async Task ReceiveOutputBlobsAsync(
            ServerCallContext context,
            ExecutionRequest request,
            IServerStreamWriter<ExecutionResponse> responseStream,
            CancellationToken cancellationToken)
        {
            var blobsPath = await GetBlobsPath();

            var allEntriesByBlobHash = new ConcurrentDictionary<long, BlobInfo>();
            var requestedBlobHashes = request.ReceiveOutputBlobs.BlobXxHash64;

            await Parallel.ForEachAsync(
                requestedBlobHashes.ToAsyncEnumerable(),
                cancellationToken,
                (blobHash, cancellationToken) =>
                {
                    var blobPath = Path.Combine(blobsPath, blobHash.HexString());
                    var blobFileInfo = new FileInfo(blobPath);
                    if (blobFileInfo.Exists)
                    {
                        allEntriesByBlobHash[blobHash] = new BlobInfo
                        {
                            ByteLength = blobFileInfo.Length,
                            Content = null,
                            Path = blobPath,
                        };
                    }
                    return ValueTask.CompletedTask;
                });

            await using (var destination = new ReceiveOutputBlobsWritableBinaryChunkStream(responseStream))
            {
                await using (var compressor = new BrotliStream(destination, CompressionMode.Compress))
                {
                    using (var source = new SequentialVersion1Encoder(
                        allEntriesByBlobHash,
                        requestedBlobHashes))
                    {
                        await source.CopyToAsync(compressor, cancellationToken);
                    }
                }
            }
        }

        private class PeerLockDisposable : IDisposable
        {
            private ConcurrentDictionary<string, bool> _remoteHostLocks;
            private string _peerHost;

            public PeerLockDisposable(
                ConcurrentDictionary<string, bool> remoteHostLocks, 
                string peerHost)
            {
                _remoteHostLocks = remoteHostLocks;
                _peerHost = peerHost;
            }

            public void Dispose()
            {
                _remoteHostLocks.TryRemove(_peerHost, out _);
            }
        }

        private async Task<string> GetBlobsPath()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DefaultToolManager));
            }
            if (_blobsReservation != null)
            {
                return _blobsReservation.ReservedPath;
            }
            await _blobsReservationSemaphore.WaitAsync();
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(DefaultToolManager));
                }
                if (_blobsReservation != null)
                {
                    return _blobsReservation.ReservedPath;
                }
                _blobsReservation = await _reservationManagerForOpenGE.ReservationManager.ReserveAsync("Blobs");
                return _blobsReservation.ReservedPath;
            }
            finally
            {
                _blobsReservationSemaphore.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _blobsReservationSemaphore.WaitAsync();
            try
            {
                if (_blobsReservation != null)
                {
                    await _blobsReservation.DisposeAsync();
                }
                _disposed = true;
            }
            finally
            {
                _blobsReservationSemaphore.Release();
            }
        }
    }
}
