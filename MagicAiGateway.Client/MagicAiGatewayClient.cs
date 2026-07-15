using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Connection;
using MagicAiGateway.Client.Security;
using MagicAiGateway.Client.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MagicAiGateway.Client;

public interface IMagicAiGatewayClient : IAsyncDisposable
{
    IGatewayConnection Connection { get; }
    IRawGatewayClient Raw { get; }
}

public sealed class MagicAiGatewayClient : IMagicAiGatewayClient
{
    private readonly GatewayConnection _connection;
    private readonly bool _ownsConnection;

    internal MagicAiGatewayClient(
        GatewayConnection connection,
        IRawGatewayClient raw,
        bool ownsConnection)
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        Connection = connection;
        Raw = raw;
    }

    public IGatewayConnection Connection { get; }
    public IRawGatewayClient Raw { get; }

    public static MagicAiGatewayClient Create(MagicAiGatewayClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var trustStore = new FileGatewayTrustStore(options);
        var connection = new GatewayConnection(options, trustStore, NullLoggerFactory.Instance);
        var raw = new RawGatewayClient(connection);
        return new MagicAiGatewayClient(connection, raw, ownsConnection: true);
    }

    public ValueTask DisposeAsync() =>
        _ownsConnection ? _connection.DisposeAsync() : ValueTask.CompletedTask;
}
