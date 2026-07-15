using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace MagicAiGateway.Client.Discovery;

public enum GatewayCandidateKind
{
    Loopback,
    LastKnown,
    Mdns,
    Configured
}

public sealed record GatewayCandidate(Uri BaseUri, GatewayCandidateKind Kind, string Source)
{
    public bool IsLocal =>
        Kind is GatewayCandidateKind.Loopback or GatewayCandidateKind.Mdns ||
        BaseUri.IsLoopback ||
        BaseUri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
        IsPrivateAddress(BaseUri.Host);

    private static bool IsPrivateAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }
}

public interface IGatewayEndpointSource
{
    int Order { get; }

    IAsyncEnumerable<GatewayCandidate> FindAsync(CancellationToken cancellationToken = default);
}

public sealed record DiscoveredFabricService(string InstanceName, string ServiceType, string Host, ushort Port)
{
    public Uri ToHttpsUri() => new($"https://{Host.TrimEnd('.')}:{Port}");
}

public sealed class MdnsBrowser : IDisposable
{
    private readonly string _serviceType;
    private readonly string _expectedInstancePrefix;
    private readonly ILogger _logger;
    private readonly MulticastService _multicast = new();
    private readonly ServiceDiscovery _discovery;
    private readonly ConcurrentDictionary<string, DiscoveredFabricService> _services = new(StringComparer.OrdinalIgnoreCase);

    public MdnsBrowser(string serviceType, string expectedInstancePrefix, ILogger logger)
    {
        _serviceType = serviceType.TrimEnd('.') + ".local";
        _expectedInstancePrefix = expectedInstancePrefix;
        _logger = logger;
        _discovery = new ServiceDiscovery(_multicast);

        _multicast.NetworkInterfaceDiscovered += (_, _) => _multicast.SendQuery(_serviceType, type: DnsType.PTR);
        _discovery.ServiceInstanceDiscovered += (_, args) =>
        {
            var instance = args.ServiceInstanceName.ToString();
            if (!Matches(instance)) return;
            _multicast.SendQuery(args.ServiceInstanceName, type: DnsType.SRV);
        };
        _multicast.AnswerReceived += (_, args) =>
        {
            foreach (var server in args.Message.Answers.OfType<SRVRecord>())
            {
                var instance = server.Name.ToString();
                if (!Matches(instance)) continue;

                var discovered = new DiscoveredFabricService(instance, _serviceType, server.Target.ToString(), server.Port);
                if (_services.TryAdd(instance, discovered))
                {
                    _logger.LogInformation(
                        "Discovered Magic AI Gateway {Instance} at {Host}:{Port}.",
                        instance,
                        discovered.Host,
                        discovered.Port);
                    ServiceDiscovered?.Invoke(this, discovered);
                }
            }
        };
    }

    public event EventHandler<DiscoveredFabricService>? ServiceDiscovered;
    public IReadOnlyCollection<DiscoveredFabricService> Services => _services.Values.ToArray();

    public void Start() => _multicast.Start();

    private bool Matches(string instance) =>
        instance.StartsWith(_expectedInstancePrefix, StringComparison.OrdinalIgnoreCase) &&
        instance.Contains(_serviceType, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _discovery.Dispose();
        _multicast.Stop();
        _multicast.Dispose();
    }
}

public sealed class LoopbackGatewayEndpointSource(int order = 0) : IGatewayEndpointSource
{
    public int Order { get; } = order;

    public async IAsyncEnumerable<GatewayCandidate> FindAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new GatewayCandidate(
            new Uri("https://localhost:7443"),
            GatewayCandidateKind.Loopback,
            "loopback");
        await Task.CompletedTask;
    }
}

public sealed class ConfiguredGatewayEndpointSource(
    IEnumerable<Uri> endpoints,
    int order = 100) : IGatewayEndpointSource
{
    private readonly Uri[] _endpoints = endpoints.Distinct().ToArray();
    public int Order { get; } = order;

    public async IAsyncEnumerable<GatewayCandidate> FindAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var endpoint in _endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new GatewayCandidate(
                Normalize(endpoint),
                GatewayCandidateKind.Configured,
                "configured");
        }

        await Task.CompletedTask;
    }

    private static Uri Normalize(Uri endpoint) => new(endpoint.ToString().TrimEnd('/') + "/");
}

public sealed class MdnsGatewayEndpointSource(
    string serviceType,
    string expectedGatewayName,
    TimeSpan timeout,
    ILogger logger,
    int order = 50) : IGatewayEndpointSource
{
    public int Order { get; } = order;

    public async IAsyncEnumerable<GatewayCandidate> FindAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<GatewayCandidate>();
        using var browser = new MdnsBrowser(serviceType, expectedGatewayName, logger);
        browser.ServiceDiscovered += (_, service) =>
            channel.Writer.TryWrite(new GatewayCandidate(
                service.ToHttpsUri(),
                GatewayCandidateKind.Mdns,
                service.InstanceName));
        browser.Start();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        while (true)
        {
            bool available;
            try
            {
                available = await channel.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (!available) yield break;
            while (channel.Reader.TryRead(out var candidate))
            {
                yield return candidate;
            }
        }
    }
}
