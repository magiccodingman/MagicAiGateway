namespace MagicAiGateway.MCP.Package.Runtime;

internal readonly record struct PackageReceiveResult(
    MagicMcpStatus Status,
    byte[]? Message,
    int RequiredLength);
