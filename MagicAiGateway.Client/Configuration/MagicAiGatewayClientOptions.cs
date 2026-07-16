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

    // Convenience compatibility option. The SDK converts this into a
    // StaticApiKeyCredentialProvider so transport code remains credential-agnostic.
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

    internal MagicAiGatewayClientOptions CloneWithoutCredentials()
    {
        var clone = new MagicAiGatewayClientOptions
        {
            ApplicationId = ApplicationId,
            ExpectedGatewayName = ExpectedGatewayName,
            EndpointOverride = EndpointOverride,
            RequestTimeout = RequestTimeout
        };

        clone.Discovery.Mode = Discovery.Mode;
        clone.Discovery.EnableLoopback = Discovery.EnableLoopback;
        clone.Discovery.EnableMdns = Discovery.EnableMdns;
        clone.Discovery.ServiceType = Discovery.ServiceType;
        clone.Discovery.Timeout = Discovery.Timeout;
        foreach (var endpoint in Discovery.FallbackEndpoints)
        {
            clone.Discovery.FallbackEndpoints.Add(endpoint);
        }

        clone.Security.TrustMode = Security.TrustMode;
        clone.Security.PinnedRootCertificateBase64 = Security.PinnedRootCertificateBase64;
        clone.Security.StateDirectory = Security.StateDirectory;
        clone.Security.AllowInsecureLoopbackOnly = Security.AllowInsecureLoopbackOnly;
        return clone;
    }
}
