using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;

namespace SharedMagic.Security;

public static class FabricAuthenticationDefaults
{
    public const string Scheme = "MagicFabricCertificate";
    public const string Policy = "MagicFabric.TrustedPeer";
    public const string PeerIdClaim = "magic_peer_id";
    public const string PeerRoleClaim = "magic_peer_role";
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class MagicFabricAuthorizeAttribute : AuthorizeAttribute
{
    public MagicFabricAuthorizeAttribute() => Policy = FabricAuthenticationDefaults.Policy;
}

public sealed class FabricAuthenticationOptions : AuthenticationSchemeOptions;

public interface IFabricPeerTrustProvider
{
    X509Certificate2? RootCertificate { get; }
    ValueTask<bool> IsPeerAllowedAsync(Guid peerId, CancellationToken cancellationToken);
    string ExpectedPeerRole { get; }
    string? LoopbackToken { get; }
    bool AllowInsecureLoopback { get; }
}

public sealed class FabricAuthenticationHandler(
    IOptionsMonitor<FabricAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IFabricPeerTrustProvider trustProvider)
    : AuthenticationHandler<FabricAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var certificate = await Context.Connection.GetClientCertificateAsync(Context.RequestAborted).ConfigureAwait(false);
        if (certificate is not null && trustProvider.RootCertificate is not null)
        {
            if (!FabricCertificateValidation.Validate(certificate, trustProvider.RootCertificate, out var error))
            {
                return AuthenticateResult.Fail(error ?? "The peer certificate is not trusted.");
            }

            var simpleName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (!Guid.TryParse(simpleName, out var peerId))
            {
                return AuthenticateResult.Fail("The peer certificate subject is not a valid fabric identity.");
            }

            if (!await trustProvider.IsPeerAllowedAsync(peerId, Context.RequestAborted).ConfigureAwait(false))
            {
                return AuthenticateResult.Fail("The peer identity is not approved for this fabric.");
            }

            return Success(peerId, trustProvider.ExpectedPeerRole, "certificate");
        }

        if (trustProvider.AllowInsecureLoopback && IsLoopback(Context.Connection.RemoteIpAddress) &&
            !string.IsNullOrWhiteSpace(trustProvider.LoopbackToken) &&
            Context.Request.Headers.TryGetValue("X-Magic-Loopback-Token", out var token) &&
            CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(token.ToString()),
                System.Text.Encoding.UTF8.GetBytes(trustProvider.LoopbackToken)))
        {
            return Success(Guid.Empty, trustProvider.ExpectedPeerRole, "loopback-token");
        }

        return AuthenticateResult.NoResult();
    }

    private AuthenticateResult Success(Guid peerId, string role, string method)
    {
        var claims = new[]
        {
            new Claim(FabricAuthenticationDefaults.PeerIdClaim, peerId.ToString()),
            new Claim(FabricAuthenticationDefaults.PeerRoleClaim, role),
            new Claim(ClaimTypes.Role, role),
            new Claim("authentication_method", method)
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);
}

public static class FabricSecurityServiceCollectionExtensions
{
    public static IServiceCollection AddMagicFabricAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(FabricAuthenticationDefaults.Scheme)
            .AddScheme<FabricAuthenticationOptions, FabricAuthenticationHandler>(FabricAuthenticationDefaults.Scheme, _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(FabricAuthenticationDefaults.Policy, policy =>
            {
                policy.AddAuthenticationSchemes(FabricAuthenticationDefaults.Scheme);
                policy.RequireAuthenticatedUser();
            });
        });
        return services;
    }
}

public static class FabricCertificateValidation
{
    public static bool Validate(X509Certificate2 certificate, X509Certificate2 root, out string? error)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        var valid = chain.Build(certificate);
        error = valid ? null : string.Join("; ", chain.ChainStatus.Select(static x => x.StatusInformation.Trim()));
        return valid;
    }
}

public sealed record FabricIdentityState(Guid InstanceId, Guid ClusterId, string Name, string Role);

public static class FabricStateFiles
{
    public static string ResolveDirectory(string configuredDirectory, string contentRoot)
    {
        var path = Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(contentRoot, configuredDirectory);
        Directory.CreateDirectory(path);
        TryRestrictDirectory(path);
        return path;
    }

    public static FabricIdentityState LoadOrCreateIdentity(string directory, string name, string role, Guid? clusterId = null)
    {
        var path = Path.Combine(directory, "identity.json");
        if (File.Exists(path))
        {
            return JsonSerializer.Deserialize<FabricIdentityState>(File.ReadAllText(path))
                   ?? throw new InvalidOperationException($"Unable to deserialize {path}.");
        }

        var state = new FabricIdentityState(Guid.NewGuid(), clusterId ?? Guid.NewGuid(), name, role);
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        TryRestrictFile(path);
        return state;
    }

    public static void TryRestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    private static void TryRestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
    }
}

public sealed class GatewayCertificateAuthority
{
    private readonly string _directory;
    private readonly FabricSecurityOptions _options;
    private readonly object _sync = new();

    public GatewayCertificateAuthority(string directory, FabricIdentityState identity, FabricSecurityOptions options)
    {
        _directory = directory;
        Identity = identity;
        _options = options;
        RootCertificate = LoadOrCreateRoot();
        ServerCertificate = LoadOrCreateServerCertificate();
    }

    public FabricIdentityState Identity { get; }
    public X509Certificate2 RootCertificate { get; }
    public X509Certificate2 ServerCertificate { get; private set; }

    public byte[] IssueCertificate(Guid nodeId, string nodeName, byte[] csr)
    {
        var request = CertificateRequest.LoadSigningRequest(
            csr,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default,
            RSASignaturePadding.Pkcs1);

        var simpleName = request.SubjectName.Name?.Replace("CN=", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!Guid.TryParse(simpleName, out var csrNodeId) || csrNodeId != nodeId)
        {
            throw new CryptographicException("The CSR subject does not match the requested node identity.");
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        var certificate = request.Create(
            RootCertificate,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(_options.CertificateLifetimeDays),
            serial);
        return certificate.Export(X509ContentType.Cert);
    }

    private X509Certificate2 LoadOrCreateRoot()
    {
        var pfxPath = Path.Combine(_directory, "cluster-ca.pfx");
        var passwordPath = Path.Combine(_directory, "cluster-ca.password");
        if (File.Exists(pfxPath) && File.Exists(passwordPath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, File.ReadAllText(passwordPath));
        }

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36));
        using var rsa = RSA.Create(4096);
        var request = new CertificateRequest(
            $"CN=MagicAiGateway Cluster {Identity.ClusterId}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        var export = created.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(pfxPath, export);
        File.WriteAllText(passwordPath, password);
        FabricStateFiles.TryRestrictFile(pfxPath);
        FabricStateFiles.TryRestrictFile(passwordPath);
        return X509CertificateLoader.LoadPkcs12(export, password);
    }

    private X509Certificate2 LoadOrCreateServerCertificate()
    {
        var pfxPath = Path.Combine(_directory, "gateway.pfx");
        var passwordPath = Path.Combine(_directory, "gateway.password");
        if (File.Exists(pfxPath) && File.Exists(passwordPath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, File.ReadAllText(passwordPath));
        }

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36));
        using var rsa = RSA.Create(3072);
        var request = CreatePeerRequest(Identity.InstanceId, rsa, includeServerNames: true);
        using var issued = request.Create(
            RootCertificate,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(_options.CertificateLifetimeDays),
            RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(rsa);
        var export = withKey.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(pfxPath, export);
        File.WriteAllText(passwordPath, password);
        FabricStateFiles.TryRestrictFile(pfxPath);
        FabricStateFiles.TryRestrictFile(passwordPath);
        return X509CertificateLoader.LoadPkcs12(export, password);
    }

    internal static CertificateRequest CreatePeerRequest(Guid id, RSA key, bool includeServerNames)
    {
        var request = new CertificateRequest($"CN={id}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var eku = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"), // Server authentication
            new("1.3.6.1.5.5.7.3.2")  // Client authentication
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        if (includeServerNames)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddDnsName(Environment.MachineName);
            san.AddIpAddress(IPAddress.Loopback);
            san.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(san.Build());
        }
        return request;
    }
}

public sealed class NodeCertificateStore
{
    private readonly string _directory;
    private readonly object _sync = new();
    private readonly string _keyPath;
    private readonly string _certificatePath;
    private readonly string _rootPath;

    public NodeCertificateStore(string directory, FabricIdentityState identity)
    {
        _directory = directory;
        Identity = identity;
        _keyPath = Path.Combine(directory, "node.key");
        _certificatePath = Path.Combine(directory, "node.pfx");
        _rootPath = Path.Combine(directory, "cluster-ca.cer");
        EnsureKey();
    }

    public FabricIdentityState Identity { get; private set; }
    public bool IsPaired => File.Exists(_certificatePath) && File.Exists(_rootPath);

    public X509Certificate2 CurrentServerCertificate => IsPaired ? LoadIssuedCertificate() : LoadBootstrapCertificate();
    public X509Certificate2? RootCertificate => File.Exists(_rootPath) ? X509CertificateLoader.LoadCertificateFromFile(_rootPath) : null;

    public string CreateCsrBase64()
    {
        using var rsa = LoadKey();
        var request = GatewayCertificateAuthority.CreatePeerRequest(Identity.InstanceId, rsa, includeServerNames: true);
        return Convert.ToBase64String(request.CreateSigningRequest());
    }

    public void Install(Guid clusterId, byte[] certificate, byte[] rootCertificate)
    {
        lock (_sync)
        {
            using var rsa = LoadKey();
            using var publicCertificate = X509CertificateLoader.LoadCertificate(certificate);
            using var withKey = publicCertificate.CopyWithPrivateKey(rsa);
            File.WriteAllBytes(_certificatePath, withKey.Export(X509ContentType.Pfx));
            File.WriteAllBytes(_rootPath, rootCertificate);
            FabricStateFiles.TryRestrictFile(_certificatePath);
            FabricStateFiles.TryRestrictFile(_rootPath);
            Identity = Identity with { ClusterId = clusterId };
            File.WriteAllText(Path.Combine(_directory, "identity.json"), JsonSerializer.Serialize(Identity, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void EnsureKey()
    {
        if (File.Exists(_keyPath)) return;
        using var rsa = RSA.Create(3072);
        File.WriteAllBytes(_keyPath, rsa.ExportPkcs8PrivateKey());
        FabricStateFiles.TryRestrictFile(_keyPath);
    }

    private RSA LoadKey()
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(File.ReadAllBytes(_keyPath), out _);
        return rsa;
    }

    private X509Certificate2 LoadIssuedCertificate() => X509CertificateLoader.LoadPkcs12FromFile(_certificatePath, null);

    private X509Certificate2 LoadBootstrapCertificate()
    {
        var path = Path.Combine(_directory, "bootstrap.pfx");
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        using var rsa = LoadKey();
        var request = GatewayCertificateAuthority.CreatePeerRequest(Identity.InstanceId, rsa, includeServerNames: true);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        FabricStateFiles.TryRestrictFile(path);
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }
}
