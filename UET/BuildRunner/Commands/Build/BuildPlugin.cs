﻿namespace BuildRunner.Commands.Build
{
    using BuildRunner.Configuration;
    using System;
    using System.Collections.Generic;
    using System.CommandLine.Invocation;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    internal class BuildPlugin : IBuild<BuildConfig>
    {
        public Task<int> ExecuteAsync(InvocationContext context, BuildConfig config)
        {
            throw new NotImplementedException();
        }
    }
}