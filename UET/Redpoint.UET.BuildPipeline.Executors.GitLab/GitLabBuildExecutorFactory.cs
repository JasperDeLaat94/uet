﻿namespace Redpoint.UET.BuildPipeline.Executors.GitLab
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Redpoint.UET.BuildPipeline.BuildGraph;
    using Redpoint.UET.BuildPipeline.Executors.BuildServer;
    using Redpoint.UET.BuildPipeline.Executors.Engine;
    using Redpoint.UET.Core;
    using Redpoint.UET.Workspace;
    using System;

    public class GitLabBuildExecutorFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public GitLabBuildExecutorFactory(
            IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IBuildExecutor CreateExecutor(string buildServerOutputFilePath)
        {
            return new GitLabBuildExecutor(
                _serviceProvider.GetRequiredService<ILogger<GitLabBuildExecutor>>(),
                _serviceProvider.GetRequiredService<ILogger<BuildServerBuildExecutor>>(),
                _serviceProvider.GetRequiredService<IBuildGraphExecutor>(),
                _serviceProvider.GetRequiredService<IEngineWorkspaceProvider>(),
                _serviceProvider.GetRequiredService<IWorkspaceProvider>(),
                _serviceProvider.GetRequiredService<IStringUtilities>(),
                buildServerOutputFilePath);
        }
    }
}