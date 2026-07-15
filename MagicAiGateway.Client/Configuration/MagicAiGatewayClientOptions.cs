namespace MagicAiGateway.Client.Configuration;

public enum GatewayDiscoveryMode
{
    LocalFirst,
    RemoteFirst,
    ConfiguredOnly
}

public enum GatewayTrustMode
{
    SystemOrLocalTrustOnFirstUse,
    SystemOnly,
    TrustOnFirstUse,
    PinnedCertificateAuthority,
    InsecureDevelopment
}

public sealed class GatewayDiscoveryOptions
{
    public GatewayDiscoveryMode Mode { get; set; } = GatewayDiscoveryMode.LocalFirst;
    public bool EnableLoopback { get; set; } = true;
    public bool EnableMdns { get; set; } = true;
    public string ServiceType { get; set; } = "_magicaigw._tcp";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
    public IList<Uri> FallbackEndpoints { get; } = new List<Uri>();
}

public sealed class GatewaySecurityOptions
{
    public GatewayTrustMode TrustMode { get; set; } = GatewayTrustMode.SystemOrLocalTrustOnFirstUse;
    public string? PinnedRootCertificateBase64 { get; set; }
    public string? StateDirectory { get; set; }
    public bool AllowInsecureLoopbackOnly { get; set; } = true;
}

public sealed class MagicAiGatewayClientOptions
{
    public string ApplicationId { get; set; } = "MagicAiGateway.Client";
    public string ExpectedGatewayName { get; set; } = "MagicAiGateway";
    public Uri? EndpointOverride { get; set; }
    public string? ApiKey { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public GatewayDiscoveryOptions Discovery { get; } = new();
    public GatewaySecurityOptions Security { get; } = new();

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationId))
        {
            throw new InvalidOperationException("A non-empty client ApplicationId is required.");
        }

        if (string.IsNullOrWhiteSpace(ExpectedGatewayName))
        {
            throw new InvalidOperationException("A non-empty ExpectedGatewayName is required.");
        }

        if (EndpointOverride is not null && !EndpointOverride.IsAbsoluteUri)
        {
            throw new InvalidOperationException("EndpointOverride must be an absolute URI.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("RequestTimeout must be greater than zero.");
        }

        if (Discovery.Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Discovery.Timeout must be greater than zero.");
        }

        if (Security.TrustMode == GatewayTrustMode.PinnedCertificateAuthority &&
            string.IsNullOrWhiteSpace(Security.PinnedRootCertificateBase64))
        {
            throw new InvalidOperationException("PinnedCertificateAuthority trust requires PinnedRootCertificateBase64.");
        }
    }
}
