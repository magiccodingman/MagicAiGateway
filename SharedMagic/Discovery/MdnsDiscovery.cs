using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SharedMagic.Discovery;

public sealed record MdnsAdvertisement(
    string InstanceName,
    string ServiceType,
    ushort Port,
    IReadOnlyDictionary<string, string> Properties);

public sealed record DiscoveredFabricService(string InstanceName, string ServiceType, string Host, ushort Port)
{
    public Uri ToHttpsUri() => new($"https://{Host.TrimEnd('.')}:{Port}");
}

public sealed class MdnsAdvertiserHostedService(
    MdnsAdvertisement advertisement,
    ILogger<MdnsAdvertiserHostedService> logger) : IHostedService, IDisposable
{
    private MulticastService? _multicast;
    private ServiceDiscovery? _discovery;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _multicast = new MulticastService();
        _discovery = new ServiceDiscovery(_multicast);
        var profile = new ServiceProfile(advertisement.InstanceName, advertisement.ServiceType, advertisement.Port);
        foreach (var property in advertisement.Properties)
        {
            profile.AddProperty(property.Key, property.Value);
        }

        _discovery.Advertise(profile);
        _multicast.Start();
        logger.LogInformation(
            "Advertising {Instance} as {ServiceType} on port {Port}.",
            advertisement.InstanceName,
            advertisement.ServiceType,
            advertisement.Port);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _multicast?.Stop();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _discovery?.Dispose();
        _multicast?.Dispose();
    }
}

// Compatibility adapter for existing server code. Gateway browsing itself now lives
// in MagicAiGateway.Client so applications and nodes share one discovery implementation.
public sealed class MdnsBrowser : IDisposable
{
    private readonly MagicAiGateway.Client.Discovery.MdnsBrowser _inner;

    public MdnsBrowser(string serviceType, string expectedInstancePrefix, ILogger<MdnsBrowser> logger)
    {
        _inner = new MagicAiGateway.Client.Discovery.MdnsBrowser(serviceType, expectedInstancePrefix, logger);
        _inner.ServiceDiscovered += (_, service) =>
        {
            var discovered = new DiscoveredFabricService(
                service.InstanceName,
                service.ServiceType,
                service.Host,
                service.Port);
            ServiceDiscovered?.Invoke(this, discovered);
        };
    }

    public event EventHandler<DiscoveredFabricService>? ServiceDiscovered;

    public IReadOnlyCollection<DiscoveredFabricService> Services => _inner.Services
        .Select(service => new DiscoveredFabricService(
            service.InstanceName,
            service.ServiceType,
            service.Host,
            service.Port))
        .ToArray();

    public void Start() => _inner.Start();
    public void Dispose() => _inner.Dispose();
}
