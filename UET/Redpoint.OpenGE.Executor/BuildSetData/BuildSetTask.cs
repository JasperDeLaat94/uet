﻿namespace Redpoint.OpenGE.Executor.BuildSetData
{
    internal record class BuildSetTask
    {
        // public required string SourceFile { get; init; }

        public required string Caption { get; init; }

        public required string Name { get; init; }

        public required string Tool { get; init; }

        public required string? WorkingDir { get; init; }

        public required bool SkipIfProjectFailed { get; init; }

        public required string? DependsOn { get; init; }
    }
}
