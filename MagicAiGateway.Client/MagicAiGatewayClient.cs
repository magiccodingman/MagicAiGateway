using MagicAiGateway.Client.Authentication;
using MagicAiGateway.Client.Chat;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Connection;
using MagicAiGateway.Client.ProtocolServices;
using MagicAiGateway.Client.Security;
using MagicAiGateway.Client.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MagicAiGateway.Client;

public interface IMagicAiGatewayClient : IAsyncDisposable
{
    IGatewayConnection Connection { get; }
    IRawGatewayClient Raw { get; }
    IMagicChatClient Chat { get; }
    IMagicProtocolClient Protocol { get; }
}

public sealed class MagicAiGatewayClient : IMagicAiGatewayClient
{
    private readonly GatewayConnection _connection;
    private readonly bool _ownsConnection;

    internal MagicAiGatewayClient(
        GatewayConnection connection,
        IRawGatewayClient raw,
        IMagicChatClient chat,
        IMagicProtocolClient protocol,
        bool ownsConnection)
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        Connection = connection;
        Raw = raw;
        Chat = chat;
        Protocol = protocol;
    }

    public IGatewayConnection Connection { get; }
    public IRawGatewayClient Raw { get; }
    public IMagicChatClient Chat { get; }
    public IMagicProtocolClient Protocol { get; }

    public static MagicAiGatewayClient Create(
        MagicAiGatewayClientOptions options,
        IGatewayCredentialProvider? credentialProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        credentialProvider ??= GatewayCredentialProviderFactory.Create(options.ApiKey);
        var transportOptions = options.CloneWithoutCredentials();
        var trustStore = new FileGatewayTrustStore(transportOptions);
        var connection = new GatewayConnection(
            transportOptions,
            trustStore,
            NullLoggerFactory.Instance);
        var raw = new RawGatewayClient(connection, credentialProvider, transportOptions);
        var chat = new MagicChatClient(raw, transportOptions);
        var protocol = new MagicProtocolClient(raw, transportOptions);
        return new MagicAiGatewayClient(connection, raw, chat, protocol, ownsConnection: true);
    }

    public ValueTask DisposeAsync() =>
        _ownsConnection ? _connection.DisposeAsync() : ValueTask.CompletedTask;
}
