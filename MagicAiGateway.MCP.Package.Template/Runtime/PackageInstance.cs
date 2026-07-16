using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MagicAiGateway.MCP.Package.Template.Runtime;

internal sealed class PackageInstance : IAsyncDisposable
{
    private readonly Pipe _hostToServer = new();
    private readonly Pipe _serverToHost = new();
    private readonly Channel<byte[]> _outgoingMessages = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(256)
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly IHost _host;
    private readonly StreamServerTransport _transport;
    private readonly McpServer _server;
    private readonly Task _serverTask;
    private readonly Task _outputPumpTask;

    private byte[]? _pendingOutgoingMessage;
    private Exception? _terminalFailure;
    private int _disposed;

    private PackageInstance(PackageInstanceContext context, IHost host)
    {
        Context = context;
        _host = host;

        ILoggerFactory? loggerFactory = host.Services.GetService<ILoggerFactory>();
        McpServerOptions options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;

        _transport = new StreamServerTransport(
            _hostToServer.Reader.AsStream(),
            _serverToHost.Writer.AsStream(),
            context.InstanceId,
            loggerFactory);

        _server = McpServer.Create(_transport, options, loggerFactory, host.Services);
        _serverTask = RunServerAsync(_lifetimeCts.Token);
        _outputPumpTask = PumpOutgoingMessagesAsync(_lifetimeCts.Token);
    }

    public PackageInstanceContext Context { get; }

    internal Task Completion => _serverTask;

    public static async Task<PackageInstance> StartAsync(
        Guid instanceId,
        ReadOnlyMemory<byte> configurationJson,
        CancellationToken cancellationToken = default)
    {
        PackageInstanceContext context = new(instanceId, configurationJson);
        IHost host = Program.BuildHost(context);

        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            return new PackageInstance(context, host);
        }
        catch
        {
            try
            {
                await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original startup failure.
            }

            host.Dispose();
            throw;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();

        if (message.IsEmpty)
        {
            throw new ArgumentException("An MCP message cannot be empty.", nameof(message));
        }

        byte[] compactMessage;
        using (JsonDocument document = JsonDocument.Parse(message))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("An MCP JSON-RPC message must be a JSON object.", nameof(message));
            }

            // The public ABI is length-framed and therefore accepts ordinary JSON
            // whitespace. StreamServerTransport is newline-framed internally, so
            // compact only at this private adapter boundary.
            compactMessage = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();

            Memory<byte> destination = _hostToServer.Writer.GetMemory(compactMessage.Length + 1);
            compactMessage.AsSpan().CopyTo(destination.Span);
            destination.Span[compactMessage.Length] = (byte)'\n';
            _hostToServer.Writer.Advance(compactMessage.Length + 1);

            FlushResult flushResult = await _hostToServer.Writer
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);

            if (flushResult.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (flushResult.IsCompleted)
            {
                throw new ObjectDisposedException(nameof(PackageInstance));
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<PackageReceiveResult> ReceiveAsync(
        int outputCapacity,
        int timeoutMilliseconds)
    {
        if (outputCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputCapacity));
        }

        if (timeoutMilliseconds < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds),
                "Use -1 for an infinite wait, 0 for polling, or a positive timeout.");
        }

        ThrowIfStopped();

        await _receiveGate.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
        try
        {
            if (_pendingOutgoingMessage is null)
            {
                byte[] nextMessage;

                if (timeoutMilliseconds == 0)
                {
                    if (!_outgoingMessages.Reader.TryRead(out nextMessage!))
                    {
                        return new PackageReceiveResult(MagicMcpStatus.NoMessage, null, 0);
                    }
                }
                else
                {
                    using CancellationTokenSource? timeoutCts = timeoutMilliseconds > 0
                        ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                        : null;

                    if (timeoutCts is not null)
                    {
                        timeoutCts.CancelAfter(timeoutMilliseconds);
                    }

                    CancellationToken token = timeoutCts?.Token ?? _lifetimeCts.Token;

                    try
                    {
                        nextMessage = await _outgoingMessages.Reader
                            .ReadAsync(token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        timeoutCts is not null &&
                        timeoutCts.IsCancellationRequested &&
                        !_lifetimeCts.IsCancellationRequested)
                    {
                        return new PackageReceiveResult(MagicMcpStatus.NoMessage, null, 0);
                    }
                    catch (ChannelClosedException)
                    {
                        if (Volatile.Read(ref _terminalFailure) is { } terminalFailure)
                        {
                            throw new InvalidOperationException(
                                "The MCP server stopped unexpectedly.",
                                terminalFailure);
                        }

                        return new PackageReceiveResult(MagicMcpStatus.InstanceStopped, null, 0);
                    }
                }

                _pendingOutgoingMessage = nextMessage;
            }

            int requiredLength = _pendingOutgoingMessage.Length;
            if (outputCapacity < requiredLength)
            {
                return new PackageReceiveResult(
                    MagicMcpStatus.BufferTooSmall,
                    null,
                    requiredLength);
            }

            byte[] outgoingMessage = _pendingOutgoingMessage;
            _pendingOutgoingMessage = null;
            return new PackageReceiveResult(MagicMcpStatus.Success, outgoingMessage, outgoingMessage.Length);
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal instance shutdown.
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _terminalFailure, exception, null);
            throw;
        }
        finally
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                _lifetimeCts.Cancel();
            }
        }
    }

    private async Task PumpOutgoingMessagesAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;

        try
        {
            using StreamReader reader = new(
                _serverToHost.Reader.AsStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                await _outgoingMessages.Writer
                    .WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal instance shutdown.
        }
        catch (Exception exception)
        {
            failure = exception;
            Interlocked.CompareExchange(ref _terminalFailure, exception, null);
        }
        finally
        {
            _outgoingMessages.Writer.TryComplete(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;

        _lifetimeCts.Cancel();

        // Serialize pipe completion with any send already in progress. A send that
        // entered first either finishes or observes the completed reader; no writer
        // operation races PipeWriter.CompleteAsync.
        await _sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _hostToServer.Writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            _sendGate.Release();
        }

        try
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal instance shutdown.
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            await _outputPumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal instance shutdown.
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(10));
            await _host.StopAsync(stopTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            _host.Dispose();
            _lifetimeCts.Dispose();
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("The package instance did not stop cleanly.", failure);
        }
    }

    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(PackageInstance));
        }
    }
}
