﻿namespace BuildRunner.Services
{
    using System;

    internal class DefaultProjectPathProvider : IProjectPathProvider
    {
        public string? ProjectRoot => throw new NotImplementedException();

        public string? ProjectName => throw new NotImplementedException();
    }
}