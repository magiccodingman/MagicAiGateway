using System.Security.Cryptography;
using System.Text;
using MagicAiGateway.DB.API.Configuration;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Security;

namespace MagicAiGateway.DB.API.Security;

public sealed class ApiKeyPepperProvider
{
    public ApiKeyPepperProvider(
        IOptions<ApplicationSecurityOptions> options,
        IOptions<FabricSecurityOptions> fabricOptions,
        IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.ApiKeyPepper))
        {
            Pepper = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.ApiKeyPepper));
            return;
        }

        var directory = FabricStateFiles.ResolveDirectory(fabricOptions.Value.StateDirectory, environment.ContentRootPath);
        var path = Path.Combine(directory, "api-key.pepper");
        if (File.Exists(path))
        {
            Pepper = Convert.FromBase64String(File.ReadAllText(path));
            return;
        }

        Pepper = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(Pepper));
        FabricStateFiles.TryRestrictFile(path);
    }

    public byte[] Pepper { get; }
}

public sealed record GeneratedApiKey(string Secret, string Prefix, string Hash);

public sealed class ApiKeySecretService(ApiKeyPepperProvider pepperProvider)
{
    public GeneratedApiKey Generate()
    {
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var random = Base64Url(RandomNumberGenerator.GetBytes(32));
        var secret = $"magk_{prefix}_{random}";
        return new GeneratedApiKey(secret, prefix, Hash(secret));
    }

    public bool Verify(string candidate, string expectedHash)
    {
        byte[] supplied;
        byte[] expected;
        try
        {
            supplied = Convert.FromHexString(Hash(candidate));
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    public static bool TryReadPrefix(string candidate, out string prefix)
    {
        prefix = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var parts = candidate.Split('_', 3, StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != "magk" || parts[1].Length != 12) return false;
        prefix = parts[1].ToLowerInvariant();
        return true;
    }

    private string Hash(string secret)
    {
        using var hmac = new HMACSHA256(pepperProvider.Pepper);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(secret)));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
