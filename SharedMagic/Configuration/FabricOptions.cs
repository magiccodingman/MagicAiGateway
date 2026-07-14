namespace SharedMagic.Configuration;

public enum AiBackendKind
{
    Vllm,
    LlamaCpp
}

public enum PairingMode
{
    ApprovalRequired,
    EnrollmentToken,
    AutomaticTrustOnFirstUse
}

public enum PairingServerCertificateMode
{
    TrustOnFirstUse,
    SystemTrust
}

public sealed class FabricSecurityOptions
{
    public const string SectionName = "Fabric:Security";
    public string StateDirectory { get; set; } = ".magic-ai";
    public PairingMode PairingMode { get; set; } = PairingMode.ApprovalRequired;
    public PairingServerCertificateMode PairingServerCertificateMode { get; set; } = PairingServerCertificateMode.TrustOnFirstUse;
    public string? EnrollmentToken { get; set; }
    public string? AdminToken { get; set; }
    public bool AllowInsecureLoopback { get; set; } = true;
    public string? LoopbackToken { get; set; }
    public int CertificateLifetimeDays { get; set; } = 730;
}

public sealed class DiscoveryOptions
{
    public const string SectionName = "Fabric:Discovery";
    public bool Enabled { get; set; } = true;
    public string GatewayServiceType { get; set; } = "_magicaigw._tcp";
    public string NodeServiceType { get; set; } = "_magicainode._tcp";
    public int AdvertisedHttpsPort { get; set; } = 7443;
}

public sealed class QueueOptions
{
    public const string SectionName = "Fabric:Queue";
    public int MaxConcurrentRequestsPerModel { get; set; } = 64;
    public int MaxQueuedRequestsPerModel { get; set; } = 256;
    public int MaximumQueueWaitSeconds { get; set; } = 120;
}

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";
    public string Name { get; set; } = "MagicAiGateway";
    public string? AdvertisedBaseUri { get; set; } = "https://localhost:7443";
    public List<StaticNodeOptions> StaticNodes { get; set; } = [];
    public int HeartbeatLeaseSeconds { get; set; } = 20;
}

public sealed class NodeOptions
{
    public const string SectionName = "Node";
    public string Name { get; set; } = Environment.MachineName;
    public string GatewayName { get; set; } = "MagicAiGateway";
    public string? AdvertisedBaseUri { get; set; } = "https://localhost:7553";
    public List<string> StaticGateways { get; set; } = [];
    public List<BackendOptions> Backends { get; set; } = [];
    public int HealthyPollSeconds { get; set; } = 10;
    public int OfflinePollSeconds { get; set; } = 3;
    public int HeartbeatSeconds { get; set; } = 5;
}

public sealed class BackendOptions
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "local-ai";
    public AiBackendKind Kind { get; set; }
    public string BaseUri { get; set; } = "http://localhost:8000";
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AllowInvalidServerCertificate { get; set; }
}

public sealed class StaticNodeOptions
{
    public Guid NodeId { get; set; }
    public string Name { get; set; } = "static-node";
    public string BaseUri { get; set; } = "https://localhost:7553";
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 10;
}
