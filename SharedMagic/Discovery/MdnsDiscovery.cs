using System.Collections.Concurrent;
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
        logger.LogInformation("Advertising {Instance} as {ServiceType} on port {Port}.", advertisement.InstanceName, advertisement.ServiceType, advertisement.Port);
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

public sealed class MdnsBrowser : IDisposable
{
    private readonly string _serviceType;
    private readonly string _expectedInstancePrefix;
    private readonly ILogger<MdnsBrowser> _logger;
    private readonly MulticastService _multicast = new();
    private readonly ServiceDiscovery _discovery;
    private readonly ConcurrentDictionary<string, DiscoveredFabricService> _services = new(StringComparer.OrdinalIgnoreCase);

    public MdnsBrowser(string serviceType, string expectedInstancePrefix, ILogger<MdnsBrowser> logger)
    {
        _serviceType = serviceType.TrimEnd('.') + ".local";
        _expectedInstancePrefix = expectedInstancePrefix;
        _logger = logger;
        _discovery = new ServiceDiscovery(_multicast);
        _multicast.NetworkInterfaceDiscovered += (_, _) => _multicast.SendQuery(_serviceType, type: DnsType.PTR);
        _discovery.ServiceInstanceDiscovered += (_, args) =>
        {
            var instance = args.ServiceInstanceName.ToString();
            if (!instance.StartsWith(_expectedInstancePrefix, StringComparison.OrdinalIgnoreCase) ||
                !instance.Contains(_serviceType, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _multicast.SendQuery(args.ServiceInstanceName, type: DnsType.SRV);
        };
        _multicast.AnswerReceived += (_, args) =>
        {
            foreach (var server in args.Message.Answers.OfType<SRVRecord>())
            {
                var instance = server.Name.ToString();
                if (!instance.StartsWith(_expectedInstancePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !instance.Contains(_serviceType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var discovered = new DiscoveredFabricService(instance, _serviceType, server.Target.ToString(), server.Port);
                if (_services.TryAdd(instance, discovered))
                {
                    _logger.LogInformation("Discovered fabric service {Instance} at {Host}:{Port}.", instance, discovered.Host, discovered.Port);
                    ServiceDiscovered?.Invoke(this, discovered);
                }
            }
        };
    }

    public event EventHandler<DiscoveredFabricService>? ServiceDiscovered;
    public IReadOnlyCollection<DiscoveredFabricService> Services => _services.Values.ToArray();

    public void Start() => _multicast.Start();

    public void Dispose()
    {
        _discovery.Dispose();
        _multicast.Stop();
        _multicast.Dispose();
    }
}
