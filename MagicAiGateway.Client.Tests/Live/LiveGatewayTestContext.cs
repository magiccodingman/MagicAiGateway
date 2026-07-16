using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Tests.Infrastructure;

namespace MagicAiGateway.Client.Tests.Live;

internal sealed class LiveGatewayTestContext : IAsyncDisposable
{
    private readonly TemporaryDirectory _stateDirectory;

    private LiveGatewayTestContext(
        LiveGatewayTestSettings settings,
        TemporaryDirectory stateDirectory,
        MagicAiGatewayClient client)
    {
        Settings = settings;
        _stateDirectory = stateDirectory;
        Client = client;
    }

    public LiveGatewayTestSettings Settings { get; }
    public MagicAiGatewayClient Client { get; }

    public static async Task<LiveGatewayTestContext> CreateAsync(
        bool requireInference = false,
        CancellationToken cancellationToken = default)
    {
        var settings = LiveGatewayTestSettings.LoadOrSkip();
        if (requireInference) settings.RequireInference();

        var stateDirectory = new TemporaryDirectory("live-gateway");
        var client = MagicAiGatewayClient.Create(new MagicAiGatewayClientOptions
        {
            ApplicationId = "MagicAiGateway.Client.Tests.Live",
            ExpectedGatewayName = settings.ExpectedGatewayName,
            EndpointOverride = settings.Endpoint,
            ApiKey = settings.ApiKey,
            RequestTimeout = settings.Timeout,
            Security =
            {
                StateDirectory = stateDirectory.Path,
                TrustMode = settings.TrustMode
            }
        });

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout);
            await client.Connection.ConnectAsync(timeout.Token).ConfigureAwait(false);
            return new LiveGatewayTestContext(settings, stateDirectory, client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            stateDirectory.Dispose();
            throw;
        }
    }

    public CancellationTokenSource CreateTimeoutSource()
    {
        var source = new CancellationTokenSource();
        source.CancelAfter(Settings.Timeout);
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        _stateDirectory.Dispose();
    }
}
