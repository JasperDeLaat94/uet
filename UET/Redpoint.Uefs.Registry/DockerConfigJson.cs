﻿using System.Text.Json.Serialization;

namespace uefs.Registry
{
    internal class DockerConfigJson
    {
        internal class DockerAuthSetting
        {
            [JsonPropertyName("auth")]
            public string Auth = string.Empty;
        }

        [JsonPropertyName("auths")]
        public Dictionary<string, DockerAuthSetting> Auths = new Dictionary<string, DockerAuthSetting>();

        [JsonPropertyName("credsStore")]
        public string CredsStore = string.Empty;
    }
}