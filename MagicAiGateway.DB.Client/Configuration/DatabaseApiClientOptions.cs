using MagicAiGateway.DB.Contracts;

namespace MagicAiGateway.DB.Client.Configuration;

public sealed class DatabaseApiClientOptions
{
    public MagicApplication Application { get; set; } = MagicApplication.Unknown;
    public string? ApiKey { get; set; }
    public Uri? EndpointOverride { get; set; }
    public Guid? ExpectedPeerId { get; set; }
    public string? PinnedRootCertificateBase64 { get; set; }
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public void Validate()
    {
        if (Application == MagicApplication.Unknown)
        {
            throw new InvalidOperationException("Database API client Application must be configured.");
        }
        if (EndpointOverride is not null && !EndpointOverride.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Database API EndpointOverride must be absolute.");
        }
        if (RefreshInterval <= TimeSpan.Zero) throw new InvalidOperationException("RefreshInterval must be positive.");
        if (RequestTimeout <= TimeSpan.Zero && RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new InvalidOperationException("RequestTimeout must be positive or infinite.");
        }
    }
}
