namespace MagicAiGateway.MCP.Package.Runtime;

internal static class MagicMcpPackageRegistry
{
    private static readonly object Gate = new();

    private static MagicMcpPackageDefinition? _definition;
    private static Exception? _configurationFailure;

    public static void Register(MagicMcpPackageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        lock (Gate)
        {
            if (_definition is not null || _configurationFailure is not null)
            {
                throw new MagicMcpPackageConfigurationException(
                    "A MagicAiGateway MCP package definition has already been registered in this library.");
            }

            _definition = definition;
        }
    }

    public static void RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (Gate)
        {
            if (_definition is null && _configurationFailure is null)
            {
                _configurationFailure = exception;
            }
        }
    }

    public static MagicMcpPackageDefinition GetRequiredDefinition()
    {
        lock (Gate)
        {
            if (_definition is not null)
            {
                return _definition;
            }

            if (_configurationFailure is not null)
            {
                throw new MagicMcpPackageConfigurationException(
                    "The MagicAiGateway MCP package could not be configured.",
                    _configurationFailure);
            }

            throw new MagicMcpPackageConfigurationException(
                "No [MagicMcpPackage] configuration method registered this library. " +
                "Ensure the generator/analyzer is referenced and exactly one valid configuration method exists.");
        }
    }
}
