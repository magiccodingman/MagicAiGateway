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

    private static readonly byte[] ManifestJsonUtf8 = """
        {
          "protocol": "magic-ai-gateway-mcp-package",
          "abiVersion": 1,
          "name": "MagicAiGateway MCP Package Template",
          "version": "0.1.0",
          "instanceId": {
            "sizeBytes": 16,
            "encoding": "opaque"
          },
          "transport": {
            "encoding": "utf-8",
            "framing": "one MCP JSON-RPC message per send or receive",
            "duplex": true
          },
          "capabilities": {
            "multipleInstances": true,
            "serverToHostMessages": true
          }
        }
        """u8.ToArray();

    internal static ReadOnlySpan<byte> JsonUtf8 => ManifestJsonUtf8;
}
