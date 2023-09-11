﻿namespace Redpoint.Uet.Configuration.Project
{
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Text.Json.Serialization;

    public class BuildConfigProject : BuildConfig
    {
        /// <summary>
        /// A list of distributions.
        /// </summary>
        [JsonPropertyName("Distributions")]
        [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "This property is used for JSON serialization.")]
        public List<BuildConfigProjectDistribution> Distributions { get; set; } = new List<BuildConfigProjectDistribution>();
    }
}
