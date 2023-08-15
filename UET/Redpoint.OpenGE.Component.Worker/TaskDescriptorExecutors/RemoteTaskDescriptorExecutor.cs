﻿namespace Redpoint.OpenGE.Component.Worker.TaskDescriptorExecutors
{
    using Redpoint.OpenGE.Core;
    using Redpoint.OpenGE.Protocol;
    using Redpoint.ProcessExecution.Enumerable;
    using Redpoint.ProcessExecution;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Runtime.CompilerServices;
    using Microsoft.Extensions.Logging;
    using Redpoint.Reservation;
    using Redpoint.OpenGE.Component.Worker.DriveMapping;
    using System.Diagnostics;
    using Redpoint.OpenGE.Component.Worker.PchPortability;

    internal class RemoteTaskDescriptorExecutor : ITaskDescriptorExecutor<RemoteTaskDescriptor>
    {
        private readonly ILogger<RemoteTaskDescriptorExecutor> _logger;
        private readonly IReservationManagerForOpenGE _reservationManagerForOpenGE;
        private readonly IToolManager _toolManager;
        private readonly IBlobManager _blobManager;
        private readonly IProcessExecutor _processExecutor;
        private readonly IDirectoryDriveMapping _directoryDriveMapping;

        public RemoteTaskDescriptorExecutor(
            ILogger<RemoteTaskDescriptorExecutor> logger,
            IReservationManagerForOpenGE reservationManagerForOpenGE,
            IToolManager toolManager,
            IBlobManager blobManager,
            IProcessExecutor processExecutor,
            IDirectoryDriveMapping directoryDriveMapping)
        {
            _logger = logger;
            _reservationManagerForOpenGE = reservationManagerForOpenGE;
            _toolManager = toolManager;
            _blobManager = blobManager;
            _processExecutor = processExecutor;
            _directoryDriveMapping = directoryDriveMapping;
        }

        private void RecreateDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                // Try and remove "Read Only" flags on files and directories.
                foreach (var entry in Directory.GetFileSystemEntries(
                    path,
                    "*",
                    new EnumerationOptions
                    {
                        AttributesToSkip = FileAttributes.System,
                        RecurseSubdirectories = true
                    }))
                {
                    var attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        attrs ^= FileAttributes.ReadOnly;
                        File.SetAttributes(entry, attrs);
                    }
                }

                // Now try to delete again.
                Directory.Delete(path, true);
            }
            Directory.CreateDirectory(path);
        }

        public async IAsyncEnumerable<ExecuteTaskResponse> ExecuteAsync(
            RemoteTaskDescriptor descriptor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Get a workspace to work in.
            await using (var reservation = await _reservationManagerForOpenGE.ReservationManager.ReserveAsync(
                "OpenGEBuild", 
                descriptor.ToolExecutionInfo.ToolExecutableName))
            {
                // If this tool requires a clean workspace, delete any files that might be in
                // this reservation already.
                if (descriptor.RequireCleanWorkspace)
                {
                    RecreateDirectory(reservation.ReservedPath);
                }

                // Ask the tool manager where our tool is located.
                var st = Stopwatch.StartNew();
                var toolPath = await _toolManager.GetToolPathAsync(
                    descriptor.ToolExecutionInfo.ToolXxHash64,
                    descriptor.ToolExecutionInfo.ToolExecutableName,
                    cancellationToken);
                _logger.LogInformation($"Tool path obtained in: {st.Elapsed}");
                st.Restart();

                // Shorten the directory path if needed.
                var rootPath = _reservationManagerForOpenGE.RootDirectory;
                var shortenedRootPath = _directoryDriveMapping.ShortenPath(rootPath);
                var shortenedReservationPath = reservation.ReservedPath;
                if (rootPath != shortenedRootPath)
                {
                    shortenedReservationPath = shortenedRootPath.TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar
                        + shortenedReservationPath.Substring(rootPath.Length).TrimStart(Path.DirectorySeparatorChar);
                }
                _logger.LogInformation($"Path shortening set up in: {st.Elapsed}");
                st.Restart();

                // Ask the blob manager to lay out all of the files in the reservation
                // based on the input files.
                await _blobManager.LayoutBuildDirectoryAsync(
                    reservation.ReservedPath,
                    shortenedReservationPath,
                    descriptor.InputsByBlobXxHash64,
                    cancellationToken);
                _logger.LogInformation($"Laid out build directory in: {st.Elapsed}");
                st.Restart();

                // Replace {__OPENGE_VIRTUAL_ROOT__} in the arguments as well.
                var arguments = descriptor
                    .Arguments
                    .Select(x => x.Replace(
                        "{__OPENGE_VIRTUAL_ROOT__}",
                        shortenedReservationPath))
                    .ToArray();

                // Replace {__OPENGE_VIRTUAL_ROOT__} in the environment variables.
                Dictionary<string, string>? environmentVariables = null;
                if (descriptor.EnvironmentVariables.Count > 0)
                {
                    environmentVariables = new Dictionary<string, string>();
                    foreach (var kv in descriptor.EnvironmentVariables)
                    {
                        environmentVariables[kv.Key] = kv.Value.Replace(
                            "{__OPENGE_VIRTUAL_ROOT__}",
                            shortenedReservationPath);
                    }
                }

                // Convert the working directory.
                var workingDirectory = _blobManager.ConvertAbsolutePathToBuildDirectoryPath(
                    shortenedReservationPath,
                    descriptor.WorkingDirectoryAbsolutePath);

                // Execute the process in the virtual root.
                _logger.LogInformation($"File path: {toolPath}");
                _logger.LogInformation($"Arguments: {string.Join(" ", arguments)}");
                _logger.LogInformation($"Working directory: {workingDirectory}");
                await foreach (var response in _processExecutor.ExecuteAsync(new ProcessSpecification
                    {
                        FilePath = toolPath,
                        Arguments = arguments,
                        EnvironmentVariables = environmentVariables,
                        WorkingDirectory = workingDirectory,
                    },
                    cancellationToken))
                {
                    switch (response)
                    {
                        case ExitCodeResponse exitCode:
                            var outputBlobs = await _blobManager.CaptureOutputBlobsFromBuildDirectoryAsync(
                                reservation.ReservedPath,
                                shortenedReservationPath,
                                descriptor.OutputAbsolutePaths,
                                cancellationToken);
                            yield return new ExecuteTaskResponse
                            {
                                Response = new Protocol.ProcessResponse
                                {
                                    ExitCode = exitCode.ExitCode,
                                },
                                OutputAbsolutePathsToBlobXxHash64 = outputBlobs,
                            };
                            break;
                        case StandardOutputResponse standardOutput:
                            yield return new ExecuteTaskResponse
                            {
                                Response = new Protocol.ProcessResponse
                                {
                                    StandardOutputLine = standardOutput.Data,
                                },
                            };
                            break;
                        case StandardErrorResponse standardError:
                            yield return new ExecuteTaskResponse
                            {
                                Response = new Protocol.ProcessResponse
                                {
                                    StandardOutputLine = standardError.Data,
                                },
                            };
                            break;
                        default:
                            throw new InvalidOperationException("Received unexpected ProcessResponse type from IProcessExecutor!");
                    };
                }
            }
        }
    }
}
