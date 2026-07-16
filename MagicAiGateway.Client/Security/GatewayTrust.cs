using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Protocol;

namespace MagicAiGateway.Client.Security;

public sealed record GatewayTrustRecord(
    string GatewayName,
    Guid GatewayId,
    Guid ClusterId,
    string RootCertificateBase64,
    string LastKnownBaseUri,
    DateTimeOffset TrustedAt);

public interface IGatewayTrustStore
{
    Task<GatewayTrustRecord?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(GatewayTrustRecord record, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class FileGatewayTrustStore : IGatewayTrustStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _path;

    public FileGatewayTrustStore(MagicAiGatewayClientOptions options)
    {
        var directory = options.Security.StateDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            directory = Path.Combine(root, "MagicAiGateway", "clients", Sanitize(options.ApplicationId));
        }

        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);
        _path = Path.Combine(directory, "gateway-trust.json");
    }

    public async Task<GatewayTrustRecord?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<GatewayTrustRecord>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveAsync(GatewayTrustRecord record, CancellationToken cancellationToken = default)
    {
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, record, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
        RestrictFile(_path);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }
}

public static class GatewayCertificateValidator
{
    public static bool Validate(
        X509Certificate2 certificate,
        X509Certificate2 root,
        Guid expectedGatewayId,
        out string? error)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        if (!chain.Build(certificate))
        {
            error = string.Join("; ", chain.ChainStatus.Select(status => status.StatusInformation.Trim()));
            return false;
        }

        var simpleName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!Guid.TryParse(simpleName, out var gatewayId) || gatewayId != expectedGatewayId)
        {
            error = "The gateway certificate identity does not match the expected gateway ID.";
            return false;
        }

        error = null;
        return true;
    }

    public static X509Certificate2 LoadRoot(string base64) =>
        X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));

    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public static GatewayTrustRecord CreateRecord(GatewayInfo info, Uri endpoint) =>
        new(
            info.Name,
            info.GatewayId,
            info.ClusterId,
            info.RootCertificateBase64,
            endpoint.ToString().TrimEnd('/'),
            DateTimeOffset.UtcNow);
}
