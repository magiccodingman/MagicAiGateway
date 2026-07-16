namespace MagicAiGateway.MCP.Package.Template.Runtime;

internal readonly record struct PackageReceiveResult(
    MagicMcpStatus Status,
    byte[]? Message,
    int RequiredLength);
