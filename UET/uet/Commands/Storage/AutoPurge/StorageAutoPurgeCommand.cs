﻿namespace UET.Commands.Storage.Purge
{
    using Redpoint.Uet.Workspace.Storage;
    using System.CommandLine;
    using System.CommandLine.Invocation;
    using System.Threading.Tasks;

    internal sealed class StorageAutoPurgeCommand
    {
        internal sealed class Options
        {
        }

        public static Command CreateAutoPurgeCommand()
        {
            var command = new Command("autopurge", "Automatically purge storage consumed by UET if low on disk space.");
            command.AddServicedOptionsHandler<StorageAutoPurgeCommandInstance, Options>();
            return command;
        }

        private sealed class StorageAutoPurgeCommandInstance : ICommandInstance
        {
            private readonly IStorageManagement _storageManagement;

            public StorageAutoPurgeCommandInstance(IStorageManagement storageManagement)
            {
                _storageManagement = storageManagement;
            }

            public async Task<int> ExecuteAsync(InvocationContext context)
            {
                await _storageManagement.AutoPurgeStorageAsync(
                    context.GetCancellationToken()).ConfigureAwait(false);

                return 0;
            }
        }
    }
}
