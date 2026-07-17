using System.Text.Json;
using MagicAiGateway.MCP.Package.Serialization;

namespace MagicAiGateway.MCP.Package;

/// <summary>Configures package-level metadata that is available before any instance starts.</summary>
public sealed class MagicMcpPackageOptionsBuilder
{
    private readonly List<Action<MagicMcpPackageManifest>> _manifestSources = [];

    internal MagicMcpPackageOptionsBuilder()
    {
    }

    /// <summary>Adds an inline manifest configuration source.</summary>
    public MagicMcpPackageOptionsBuilder ConfigureManifest(Action<MagicMcpPackageManifest> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _manifestSources.Add(configure);
        return this;
    }

    /// <summary>
    /// Adds a UTF-8 JSON manifest file. Relative paths are resolved against
    /// <see cref="AppContext.BaseDirectory"/> of the embedding host process. Use an
    /// absolute path when deployment keeps package metadata elsewhere. Sources are
    /// applied in registration order, so later inline configuration may override
    /// values loaded from a file.
    /// </summary>
    public MagicMcpPackageOptionsBuilder AddManifestFile(string path, bool optional = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _manifestSources.Add(manifest =>
        {
            string resolvedPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

            if (!File.Exists(resolvedPath))
            {
                if (optional)
                {
                    return;
                }

                throw new MagicMcpPackageConfigurationException(
                    $"The MCP package manifest file '{resolvedPath}' does not exist.");
            }

            try
            {
                byte[] json = File.ReadAllBytes(resolvedPath);
                MagicMcpPackageManifest? loaded = JsonSerializer.Deserialize(
                    json,
                    MagicMcpPackageJsonContext.Default.MagicMcpPackageManifest);

                if (loaded is null)
                {
                    throw new MagicMcpPackageConfigurationException(
                        $"The MCP package manifest file '{resolvedPath}' did not contain a JSON object.");
                }

                manifest.CopyAuthorFieldsFrom(loaded);
            }
            catch (MagicMcpPackageConfigurationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new MagicMcpPackageConfigurationException(
                    $"The MCP package manifest file '{resolvedPath}' could not be loaded.",
                    exception);
            }
        });

        return this;
    }

    internal MagicMcpPackageManifest BuildManifest()
    {
        MagicMcpPackageManifest manifest = new();

        foreach (Action<MagicMcpPackageManifest> source in _manifestSources)
        {
            source(manifest);
        }

        manifest.NormalizeRuntimeFields();
        MagicMcpPackageManifestValidator.Validate(manifest);
        return manifest;
    }
}
