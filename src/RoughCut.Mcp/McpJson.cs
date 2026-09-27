using System.Text.Json.Serialization;

namespace RoughCut.Mcp;

/// Where a speech model sits and whether it passed its published size and hash.
public sealed record SpeechModelStatus(string ModelPath, long Bytes, bool Verified);

/// Contracts the host answers with that belong to no other layer. Reflection-based serialization is off
/// across this repository, so every shape a tool returns is generated here rather than discovered at run time.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SpeechModelStatus))]
public partial class McpJson : JsonSerializerContext;
