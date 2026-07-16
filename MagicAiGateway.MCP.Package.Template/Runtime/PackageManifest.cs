using System.Text.Json;

namespace MagicAiGateway.MCP.Package.Template.Runtime;

/// <summary>
/// Identifies this package to MagicAiGateway. Update the name and version when
/// creating a real package from the template.
/// </summary>
public static class PackageManifest
{
    public const int AbiVersion = 1;
    public const string Name = "MagicAiGateway MCP Package Template";
    public const string Version = "0.1.0";

    private static readonly byte[] ManifestJsonUtf8 = CreateManifestJson();

    internal static ReadOnlySpan<byte> JsonUtf8 => ManifestJsonUtf8;

    private static byte[] CreateManifestJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", "magic-ai-gateway-mcp-package");
            writer.WriteNumber("abiVersion", AbiVersion);
            writer.WriteString("name", Name);
            writer.WriteString("version", Version);

            writer.WritePropertyName("instanceId");
            writer.WriteStartObject();
            writer.WriteNumber("sizeBytes", MagicMcpExports.InstanceIdSize);
            writer.WriteString("encoding", "opaque");
            writer.WriteEndObject();

            writer.WritePropertyName("transport");
            writer.WriteStartObject();
            writer.WriteString("encoding", "utf-8");
            writer.WriteString("framing", "one MCP JSON-RPC message per send or receive");
            writer.WriteBoolean("duplex", true);
            writer.WriteEndObject();

            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WriteBoolean("multipleInstances", true);
            writer.WriteBoolean("serverToHostMessages", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
