﻿namespace Redpoint.UET.Configuration
{
    using Redpoint.UET.Configuration.Engine;
    using Redpoint.UET.Configuration.Plugin;
    using Redpoint.UET.Configuration.Project;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(BuildConfig))]
    [JsonSerializable(typeof(BuildConfigEngine))]
    [JsonSerializable(typeof(BuildConfigPlugin))]
    [JsonSerializable(typeof(BuildConfigProject))]
    [JsonSerializable(typeof(BuildConfigEngineIncludeFragment))]
    [JsonSerializable(typeof(BuildConfigPluginIncludeFragment))]
    [JsonSerializable(typeof(BuildConfigProjectIncludeFragment))]
    public partial class BuildConfigSourceGenerationContext : JsonSerializerContext
    {
        public static BuildConfigSourceGenerationContext WithDynamicBuildConfig(string basePathForIncludes)
        {
            return new BuildConfigSourceGenerationContext(new JsonSerializerOptions
            {
                Converters =
                {
                    new BuildConfigConverter(basePathForIncludes),
                    new JsonStringEnumConverter(),
                }
            });
        }
    }
}
