using System.Text.RegularExpressions;

namespace MagicAiGateway.MCP.Package;

internal static partial class MagicMcpPackageManifestValidator
{
    public static void Validate(MagicMcpPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        ValidateRequiredText(manifest.Name, nameof(manifest.Name), 128);
        ValidateRequiredText(manifest.Version, nameof(manifest.Version), 64);

        if (!SemanticVersionExpression().IsMatch(manifest.Version))
        {
            throw new MagicMcpPackageConfigurationException(
                $"Package version '{manifest.Version}' is not a valid semantic version such as '1.0.0' or '1.0.0-preview.1'.");
        }

        ValidateOptionalText(manifest.Description, nameof(manifest.Description), 2048);
        ValidateOptionalText(manifest.Author, nameof(manifest.Author), 256);
        ValidateOptionalText(manifest.Homepage, nameof(manifest.Homepage), 2048);

        if (manifest.Homepage is not null &&
            !Uri.TryCreate(manifest.Homepage, UriKind.Absolute, out _))
        {
            throw new MagicMcpPackageConfigurationException(
                $"Package homepage '{manifest.Homepage}' must be an absolute URI.");
        }

        if (manifest.Metadata is null)
        {
            return;
        }

        if (manifest.Metadata.Count > 64)
        {
            throw new MagicMcpPackageConfigurationException(
                "Package metadata may contain at most 64 entries.");
        }

        foreach ((string key, string value) in manifest.Metadata)
        {
            ValidateRequiredText(key, "Metadata key", 128);
            ValidateRequiredText(value, $"Metadata value for '{key}'", 2048);
        }
    }

    private static void ValidateRequiredText(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MagicMcpPackageConfigurationException(
                $"Package manifest field '{name}' is required.");
        }

        if (value.Length > maximumLength)
        {
            throw new MagicMcpPackageConfigurationException(
                $"Package manifest field '{name}' may not exceed {maximumLength} characters.");
        }
    }

    private static void ValidateOptionalText(string? value, string name, int maximumLength)
    {
        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MagicMcpPackageConfigurationException(
                $"Package manifest field '{name}' must be omitted or contain non-whitespace text.");
        }

        if (value.Length > maximumLength)
        {
            throw new MagicMcpPackageConfigurationException(
                $"Package manifest field '{name}' may not exceed {maximumLength} characters.");
        }
    }

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionExpression();
}
