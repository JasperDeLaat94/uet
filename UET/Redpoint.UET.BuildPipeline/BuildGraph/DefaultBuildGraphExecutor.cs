﻿namespace Redpoint.UET.BuildPipeline.BuildGraph
{
    using Redpoint.ProcessExecution;
    using Redpoint.UET.BuildPipeline.BuildGraph.Export;
    using Redpoint.UET.BuildPipeline.BuildGraph.Patching;
    using Redpoint.UET.Configuration;
    using Redpoint.UET.UAT;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading.Tasks;

    internal class DefaultBuildGraphExecutor : IBuildGraphExecutor
    {
        private readonly IUATExecutor _uatExecutor;
        private readonly IBuildGraphArgumentGenerator _buildGraphArgumentGenerator;
        private readonly IBuildGraphPatcher _buildGraphPatcher;

        public DefaultBuildGraphExecutor(
            IUATExecutor uatExecutor,
            IBuildGraphArgumentGenerator buildGraphArgumentGenerator,
            IBuildGraphPatcher buildGraphPatcher)
        {
            _uatExecutor = uatExecutor;
            _buildGraphArgumentGenerator = buildGraphArgumentGenerator;
            _buildGraphPatcher = buildGraphPatcher;
        }

        public async Task<int> ExecuteGraphNodeAsync(
            string enginePath,
            string buildGraphRepositoryRootPath,
            string uetPath,
            BuildGraphScriptSpecification buildGraphScript,
            string buildGraphTarget,
            string buildGraphNodeName,
            string buildGraphSharedStorageDir,
            Dictionary<string, string> buildGraphArguments,
            Dictionary<string, string> buildGraphArgumentReplacements,
            Dictionary<string, string> globalEnvironmentVariables,
            ICaptureSpecification captureSpecification,
            CancellationToken cancellationToken)
        {
            var environmentVariables = new Dictionary<string, string>
            {
                { "IsBuildMachine", "1" },
                { "uebp_LOCAL_ROOT", enginePath },
                // BuildGraph in Unreal Engine 5.0 causes input files to be unnecessarily modified. Just allow mutation since I'm not sure what the bug is.
                { "BUILD_GRAPH_ALLOW_MUTATION", "true" },
                { "BUILD_GRAPH_PROJECT_ROOT", buildGraphRepositoryRootPath },
            };
            foreach (var kv in globalEnvironmentVariables)
            {
                environmentVariables[kv.Key] = kv.Value;
            }

            return await InternalRunAsync(
                enginePath,
                buildGraphRepositoryRootPath,
                uetPath,
                buildGraphScript,
                buildGraphTarget,
                new[]
                {
                    $"-SingleNode={buildGraphNodeName}",
                    "-WriteToSharedStorage",
                    $"-SharedStorageDir={buildGraphSharedStorageDir}"
                },
                buildGraphArguments,
                buildGraphArgumentReplacements,
                environmentVariables,
                captureSpecification,
                cancellationToken);
        }

        public async Task<BuildGraphExport> GenerateGraphAsync(
            string enginePath,
            string buildGraphRepositoryRootPath,
            string uetPath,
            BuildGraphScriptSpecification buildGraphScript,
            string buildGraphTarget,
            Dictionary<string, string> buildGraphArguments,
            Dictionary<string, string> buildGraphArgumentReplacements,
            ICaptureSpecification captureSpecification,
            CancellationToken cancellationToken)
        {
            var buildGraphOutput = Path.GetTempFileName();
            var deleteBuildGraphOutput = true;
            try
            {
                var exitCode = await InternalRunAsync(
                    enginePath,
                    buildGraphRepositoryRootPath,
                    uetPath,
                    buildGraphScript,
                    buildGraphTarget,
                    new[] { $"-Export={buildGraphOutput}" },
                    buildGraphArguments,
                    buildGraphArgumentReplacements,
                    new Dictionary<string, string>
                    {
                        { "IsBuildMachine", "1" },
                        { "uebp_LOCAL_ROOT", enginePath },
                    },
                    captureSpecification,
                    cancellationToken);
                if (exitCode != 0)
                {
                    throw new BuildGraphExecutionFailure($"Failed to generate build graph; UAT exited with non-zero exit code {exitCode}.");
                }

                using (var reader = new FileStream(buildGraphOutput, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var json = JsonSerializer.Deserialize<BuildGraphExport>(reader, BuildGraphSourceGenerationContext.Default.BuildGraphExport);
                    if (json == null)
                    {
                        deleteBuildGraphOutput = false;
                        throw new BuildGraphExecutionFailure($"Failed to generate build graph; UAT did not produce a valid BuildGraph JSON file. Output file is stored at: {buildGraphOutput}");
                    }
                    return json;
                }
            }
            finally
            {
                if (deleteBuildGraphOutput)
                {
                    File.Delete(buildGraphOutput);
                }
            }
        }

        private async Task<int> InternalRunAsync(
            string enginePath,
            string buildGraphRepositoryRootPath,
            string uetPath,
            BuildGraphScriptSpecification buildGraphScript,
            string buildGraphTarget,
            string[] internalArguments,
            Dictionary<string, string> buildGraphArguments,
            Dictionary<string, string> buildGraphArgumentReplacements,
            Dictionary<string, string> buildGraphEnvironmentVariables,
            ICaptureSpecification captureSpecification,
            CancellationToken cancellationToken)
        {
            string buildGraphScriptPath;
            var deleteBuildGraphScriptPath = false;
            if (buildGraphScript._path != null)
            {
                buildGraphScriptPath = buildGraphScript._path;
            }
            else if (buildGraphScript._forPlugin)
            {
                buildGraphScriptPath = Path.GetTempFileName();
                using (var reader = Assembly.GetExecutingAssembly().GetManifestResourceStream("Redpoint.UET.BuildPipeline.BuildGraph.BuildGraph_Plugin.xml"))
                {
                    using (var writer = new FileStream(buildGraphScriptPath, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        await reader!.CopyToAsync(writer, cancellationToken);
                    }
                }
                deleteBuildGraphScriptPath = true;
            }
            else if (buildGraphScript._forProject)
            {
                buildGraphScriptPath = Path.GetTempFileName();
                using (var reader = Assembly.GetExecutingAssembly().GetManifestResourceStream("Redpoint.UET.BuildPipeline.BuildGraph.BuildGraph_Project.xml"))
                {
                    using (var writer = new FileStream(buildGraphScriptPath, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        await reader!.CopyToAsync(writer, cancellationToken);
                    }
                }
                deleteBuildGraphScriptPath = true;
            }
            else
            {
                throw new NotSupportedException();
            }

            await _buildGraphPatcher.PatchBuildGraphAsync(enginePath);

            try
            {
                return await _uatExecutor.ExecuteAsync(
                    enginePath,
                    new UATSpecification
                    {
                        Command = "BuildGraph",
                        Arguments = new[]
                        {
                            $"-Target={buildGraphTarget}",
                            "-noP4",
                            $"-Script={buildGraphScriptPath}",
                        }
                            .Concat(internalArguments)
                            .Concat(_buildGraphArgumentGenerator.GenerateBuildGraphArguments(
                                buildGraphArguments,
                                buildGraphArgumentReplacements,
                                buildGraphRepositoryRootPath,
                                uetPath)),
                        EnvironmentVariables = buildGraphEnvironmentVariables
                    },
                    captureSpecification,
                    cancellationToken);
            }
            finally
            {
                if (deleteBuildGraphScriptPath)
                {
                    File.Delete(buildGraphScriptPath);
                }
            }
        }
    }
}
