using System.Text.Json.Serialization;

namespace MagicAiGateway.MCP.Package.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MagicMcpPackageManifest))]
internal partial class MagicMcpPackageJsonContext : JsonSerializerContext;
