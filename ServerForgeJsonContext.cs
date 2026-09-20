using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ServerForge
{
    // NativeAOT cannot generate JSON converters at run time. Keep every JSON
    // contract used by the application in this compile-time source-generated
    // context so vault, monitoring and backup paths remain available.
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(List<Server>))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    [JsonSerializable(typeof(ServerResourceSnapshot))]
    [JsonSerializable(typeof(RemoteDumpArtifact))]
    [JsonSerializable(typeof(MongoRemoteArtifact))]
    [JsonSerializable(typeof(RedisRemoteArtifact))]
    [JsonSerializable(typeof(TerminalPageMessage))]
    internal sealed partial class ServerForgeJsonContext : JsonSerializerContext
    {
    }

    internal sealed class TerminalPageMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Data { get; set; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Value { get; set; }
    }
}
