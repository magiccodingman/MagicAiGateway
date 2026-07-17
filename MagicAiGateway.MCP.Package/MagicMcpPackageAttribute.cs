namespace MagicAiGateway.MCP.Package;

/// <summary>
/// Marks the single static method that configures a compiled MagicAiGateway MCP package.
/// A source generator creates the NativeAOT-safe package bootstrap around this method.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class MagicMcpPackageAttribute : Attribute
{
}
