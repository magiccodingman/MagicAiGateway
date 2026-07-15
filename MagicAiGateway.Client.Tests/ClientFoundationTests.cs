using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Discovery;
using MagicAiGateway.Client.Protocol;
using MagicAiGateway.Client.Security;

namespace MagicAiGateway.Client.Tests;

public sealed class ClientFoundationTests
{
    [Fact]
    public void AttachAddsCanonicalGatewayEnvelopeWithoutMutatingSource()
    {
        var source = new JsonObject
        {
            ["model"] = "Qwen36-27B",
            ["stream"] = true
        };

        var result = MagicAiGatewayJson.Attach(source, new MagicAiGatewayEnvelope
        {
            Operation = "tool-loop",
            Options = new JsonObject { ["maximum_rounds"] = 8 }
        });

        Assert.False(source.ContainsKey(MagicAiGatewayProtocol.PropertyName));
        Assert.Equal("tool-loop", result[MagicAiGatewayProtocol.PropertyName]?["operation"]?.GetValue<string>());
        Assert.Equal(8, result[MagicAiGatewayProtocol.PropertyName]?["options"]?["maximum_rounds"]?.GetValue<int>());
    }

    [Theory]
    [InlineData("https://localhost:7443")]
    [InlineData("https://gateway.local:7443")]
    [InlineData("https://10.0.0.5:7443")]
    [InlineData("https://172.20.1.5:7443")]
    [InlineData("https://192.168.1.5:7443")]
    public void PrivateAndLocalConfiguredEndpointsAreLocalCandidates(string endpoint)
    {
        var candidate = new GatewayCandidate(new Uri(endpoint), GatewayCandidateKind.Configured, "test");
        Assert.True(candidate.IsLocal);
    }

    [Fact]
    public void PublicConfiguredEndpointIsNotLocalCandidate()
    {
        var candidate = new GatewayCandidate(
            new Uri("https://ai.example.com"),
            GatewayCandidateKind.Configured,
            "test");

        Assert.False(candidate.IsLocal);
    }

    [Fact]
    public async Task FileTrustStoreRoundTripsGatewayIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "magic-ai-client-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new MagicAiGatewayClientOptions { ApplicationId = "tests" };
            options.Security.StateDirectory = directory;
            var store = new FileGatewayTrustStore(options);
            var expected = new GatewayTrustRecord(
                "MagicAiGateway",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Convert.ToBase64String([1, 2, 3]),
                "https://localhost:7443",
                DateTimeOffset.UtcNow);

            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RawClientRejectsAbsoluteExternalUris()
    {
        var directory = Path.Combine(Path.GetTempPath(), "magic-ai-client-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var client = MagicAiGatewayClient.Create(new MagicAiGatewayClientOptions
            {
                ApplicationId = "absolute-uri-test",
                Security = { StateDirectory = directory }
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.Raw.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.com/v1/models")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitLoopbackEndpointResolvesAndSendsRawRequests()
    {
        await using var server = new LoopbackGatewayServer();
        var directory = Path.Combine(Path.GetTempPath(), "magic-ai-client-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var client = MagicAiGatewayClient.Create(new MagicAiGatewayClientOptions
            {
                ApplicationId = "transport-test",
                EndpointOverride = server.Endpoint,
                Security = { StateDirectory = directory, TrustMode = GatewayTrustMode.SystemOnly }
            });

            var resolved = await client.Connection.ConnectAsync();
            Assert.Equal(server.GatewayId, resolved.Gateway.GatewayId);
            Assert.Equal(server.Endpoint, resolved.BaseUri);

            using var response = await client.Raw.GetAsync("/v1/models");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            Assert.Contains("Qwen36-27B", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class LoopbackGatewayServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serverTask;

        public LoopbackGatewayServer()
        {
            GatewayId = Guid.NewGuid();
            ClusterId = Guid.NewGuid();
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/");
            _serverTask = RunAsync(_stopping.Token);
        }

        public Guid GatewayId { get; }
        public Guid ClusterId { get; }
        public Uri Endpoint { get; }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    await HandleAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
            string? header;
            do
            {
                header = await reader.ReadLineAsync(cancellationToken);
            }
            while (!string.IsNullOrEmpty(header));

            var path = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            var body = path switch
            {
                MagicAiGatewayProtocol.GatewayInfoPath => JsonSerializer.Serialize(new GatewayInfo(
                    "MagicAiGateway",
                    GatewayId,
                    ClusterId,
                    MagicAiGatewayProtocol.CurrentVersion,
                    1,
                    string.Empty,
                    ["openai-proxy", "streaming"])),
                "/v1/models" => "{\"object\":\"list\",\"data\":[{\"id\":\"Qwen36-27B\"}]}",
                _ => "{\"error\":\"not found\"}"
            };
            var status = path is MagicAiGatewayProtocol.GatewayInfoPath or "/v1/models"
                ? "200 OK"
                : "404 Not Found";
            var bytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try { await _serverTask; } catch (SocketException) { }
            _stopping.Dispose();
        }
    }
}
