namespace MagicAiGateway.MCP.Package;

/// <summary>
/// Base class for MagicAiGateway MCP tool controllers. A fresh controller object is
/// created for every tool invocation. Long-lived state belongs in injected services,
/// not in the controller itself.
/// </summary>
public abstract class MagicMcpToolController
{
    private MagicMcpPackageInstanceContext? _packageInstance;

    /// <summary>
    /// Gets the package instance handling the current tool invocation.
    /// The framework initializes this property before the tool method runs.
    /// </summary>
    public MagicMcpPackageInstanceContext PackageInstance =>
        _packageInstance ?? throw new InvalidOperationException(
            "The MCP tool controller has not been initialized by the MagicAiGateway package runtime.");

    internal void Initialize(MagicMcpPackageInstanceContext packageInstance)
    {
        ArgumentNullException.ThrowIfNull(packageInstance);

        if (Interlocked.CompareExchange(ref _packageInstance, packageInstance, null) is not null)
        {
            throw new InvalidOperationException("The MCP tool controller was initialized more than once.");
        }
    }
}
