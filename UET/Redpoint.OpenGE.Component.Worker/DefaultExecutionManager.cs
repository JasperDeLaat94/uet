﻿namespace Redpoint.OpenGE.Component.Worker
{
    using Grpc.Core;
    using Microsoft.Extensions.Logging;
    using Redpoint.OpenGE.Component.Worker.TaskDescriptorExecutors;
    using Redpoint.OpenGE.Protocol;
    using System.Threading.Tasks;

    internal class DefaultExecutionManager : IExecutionManager
    {
        private readonly ILogger<DefaultExecutionManager> _logger;
        private readonly ITaskDescriptorExecutor<LocalTaskDescriptor> _localTaskExecutor;
        private readonly ITaskDescriptorExecutor<CopyTaskDescriptor> _copyTaskExecutor;
        private readonly ITaskDescriptorExecutor<RemoteTaskDescriptor> _remoteTaskExecutor;

        public DefaultExecutionManager(
            ILogger<DefaultExecutionManager> logger,
            ITaskDescriptorExecutor<LocalTaskDescriptor> localTaskExecutor,
            ITaskDescriptorExecutor<CopyTaskDescriptor> copyTaskExecutor,
            ITaskDescriptorExecutor<RemoteTaskDescriptor> remoteTaskExecutor)
        {
            _logger = logger;
            _localTaskExecutor = localTaskExecutor;
            _copyTaskExecutor = copyTaskExecutor;
            _remoteTaskExecutor = remoteTaskExecutor;
        }

        public async Task ExecuteTaskAsync(
            ExecuteTaskRequest request, 
            IServerStreamWriter<ExecutionResponse> responseStream,
            CancellationToken cancellationToken)
        {
            try
            {
                var shouldRestart = false;
                var restartingCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                var autoRecover = new List<string>(request.AutoRecover);
                // @hack: Do this in a better place.
                if (request.Descriptor_.DescriptorCase == TaskDescriptor.DescriptorOneofCase.Local)
                {
                    if (Path.GetFileName(request.Descriptor_.Local.Path) == "cl.exe")
                    {
                        // "c1xx : fatal error C1356: unable to find mspdbcore.dll"
                        // which can happen under high loads.
                        autoRecover.Add("C1356");
                        // "cl : Command line error D8037: cannot create temporary il file; clean temp directory of old il files"
                        // which can happen under high loads.
                        autoRecover.Add("D8037");
                    }
                    else if (Path.GetFileName(request.Descriptor_.Local.Path) == "link.exe")
                    {
                        // "LINK : fatal error LNK1171: unable to load mspdbcore.dll (error code: 1455)"
                        // which can happen under high loads.
                        autoRecover.Add("LNK1171");
                    }
                }
                do
                {
                    shouldRestart = false;
                    var processResponseStream = GetProcessResponseStreamFromRequest(
                        request, 
                        restartingCancellationTokenSource.Token);
                    var didGetExitCode = false;
                    await foreach (var response in processResponseStream)
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Worker process response stream: " + response.ToString());
                        }
                        if (didGetExitCode)
                        {
                            throw new RpcException(new Status(StatusCode.Internal, "Task executor sent response after sending ExitCode."));
                        }
                        var shouldAutoRecover = false;
                        if (autoRecover.Count > 0)
                        {
                            switch (response.Response.DataCase)
                            {
                                case ProcessResponse.DataOneofCase.StandardOutputLine:
                                    if (autoRecover.Any(x => response.Response.StandardOutputLine.Contains(x)))
                                    {
                                        shouldAutoRecover = true;
                                    }
                                    break;
                                case ProcessResponse.DataOneofCase.StandardErrorLine:
                                    if (autoRecover.Any(x => response.Response.StandardErrorLine.Contains(x)))
                                    {
                                        shouldAutoRecover = true;
                                    }
                                    break;
                            }
                        }
                        if (shouldAutoRecover)
                        {
                            // We must auto-recover and restart the process.
                            restartingCancellationTokenSource.Cancel();
                            restartingCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken);
                            shouldRestart = true;
                            break;
                        }
                        var ignoreThisOutputLine = false;
                        switch (response.Response.DataCase)
                        {
                            case ProcessResponse.DataOneofCase.StandardOutputLine:
                                if (request.IgnoreLines.Any(x => response.Response.StandardOutputLine.Contains(x)))
                                {
                                    ignoreThisOutputLine = true;
                                }
                                break;
                            case ProcessResponse.DataOneofCase.StandardErrorLine:
                                if (request.IgnoreLines.Any(x => response.Response.StandardErrorLine.Contains(x)))
                                {
                                    ignoreThisOutputLine = true;
                                }
                                break;
                        }
                        if (!ignoreThisOutputLine)
                        {
                            await responseStream.WriteAsync(new ExecutionResponse
                            {
                                ExecuteTask = response,
                            });
                        }
                        if (response.Response.DataCase == ProcessResponse.DataOneofCase.ExitCode)
                        {
                            if (response.Response.ExitCode == -1073741502)
                            {
                                // @note: This is a weird transient exit code we get on Windows sometimes.
                                // Just retry in this case.
                                restartingCancellationTokenSource.Cancel();
                                restartingCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                                    cancellationToken);
                                shouldRestart = true;
                            }
                            else
                            {
                                didGetExitCode = true;
                            }
                        }
                    }
                    if (!didGetExitCode && !shouldRestart)
                    {
                        throw new RpcException(new Status(StatusCode.Internal, "All task executors must emit an ExitCode as their final response."));
                    }
                } while (shouldRestart);
            }
            catch (RpcException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.ToString()));
            }
        }

        private IAsyncEnumerable<ExecuteTaskResponse> GetProcessResponseStreamFromRequest(ExecuteTaskRequest request, CancellationToken cancellationToken)
        {
            IAsyncEnumerable<ExecuteTaskResponse> processResponseStream;
            switch (request.Descriptor_.DescriptorCase)
            {
                case TaskDescriptor.DescriptorOneofCase.Local:
                    processResponseStream = _localTaskExecutor.ExecuteAsync(
                        request.Descriptor_.Local,
                        cancellationToken);
                    break;
                case TaskDescriptor.DescriptorOneofCase.Copy:
                    processResponseStream = _copyTaskExecutor.ExecuteAsync(
                        request.Descriptor_.Copy,
                        cancellationToken);
                    break;
                case TaskDescriptor.DescriptorOneofCase.Remote:
                    processResponseStream = _remoteTaskExecutor.ExecuteAsync(
                        request.Descriptor_.Remote,
                        cancellationToken);
                    break;
                default:
                    throw new RpcException(new Status(StatusCode.Unimplemented, "No executor for this descriptor type."));
            }

            return processResponseStream;
        }
    }
}
