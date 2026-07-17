using System.ComponentModel;
using System.Text.Json;

namespace MagicAiGateway.MCP.Package.Runtime;

/// <summary>
/// Managed implementation behind the NativeAOT exports generated into the consuming
/// package assembly. The host owns all input and output buffers; no allocator or
/// managed object crosses the boundary.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class MagicMcpExports
{
    public const int InstanceIdSize = 16;

    public static int GetAbiVersion()
    {
        InteropErrorState.Clear();
        return MagicMcpPackageManifest.CurrentAbiVersion;
    }

    public static int GetManifest(nint output, nuint outputCapacity, nint outputLength)
    {
        InteropErrorState.Clear();

        try
        {
            MagicMcpPackageDefinition definition = MagicMcpPackageRegistry.GetRequiredDefinition();
            return CopyToCaller(
                definition.ManifestJsonUtf8,
                (byte*)output,
                outputCapacity,
                (nuint*)outputLength);
        }
        catch (Exception exception)
        {
            return Fail(MagicMcpStatus.InternalError, exception);
        }
    }

    public static int StartInstance(
        nint configurationJson,
        nuint configurationLength,
        nint instanceIdOutput)
    {
        InteropErrorState.Clear();

        try
        {
            byte* instanceIdPointer = (byte*)instanceIdOutput;
            if (instanceIdPointer == null)
            {
                return Fail(MagicMcpStatus.InvalidArgument, "instanceIdOutput must point to 16 writable bytes.");
            }

            byte[] configuration = CopyInput(
                (byte*)configurationJson,
                configurationLength,
                "configurationJson");
            ValidateConfiguration(configuration);

            Guid instanceId = PackageRuntime
                .StartInstanceAsync(configuration)
                .GetAwaiter()
                .GetResult();

            instanceId.TryWriteBytes(new Span<byte>(instanceIdPointer, InstanceIdSize));
            return (int)MagicMcpStatus.Success;
        }
        catch (ArgumentException exception)
        {
            return Fail(MagicMcpStatus.InvalidArgument, exception);
        }
        catch (JsonException exception)
        {
            return Fail(MagicMcpStatus.InvalidArgument, exception);
        }
        catch (Exception exception)
        {
            return Fail(MagicMcpStatus.InternalError, exception);
        }
    }

    public static int Send(
        nint instanceId,
        nint message,
        nuint messageLength)
    {
        InteropErrorState.Clear();

        try
        {
            Guid id = ReadInstanceId((byte*)instanceId);
            if (!PackageRuntime.TryGetInstance(id, out PackageInstance? instance) || instance is null)
            {
                return Fail(MagicMcpStatus.InstanceNotFound, "The package instance does not exist.");
            }

            byte[] messageBytes = CopyInput((byte*)message, messageLength, "message");
            instance.SendAsync(messageBytes).GetAwaiter().GetResult();
            return (int)MagicMcpStatus.Success;
        }
        catch (ArgumentException exception)
        {
            return Fail(MagicMcpStatus.InvalidArgument, exception);
        }
        catch (JsonException exception)
        {
            return Fail(MagicMcpStatus.InvalidArgument, exception);
        }
        catch (ObjectDisposedException exception)
        {
            return Fail(MagicMcpStatus.InstanceStopped, exception);
        }
        catch (OperationCanceledException exception)
        {
            return Fail(MagicMcpStatus.InstanceStopped, exception);
        }
        catch (Exception exception)
        {
            return Fail(MagicMcpStatus.InternalError, exception);
        }
    }

    public static int Receive(
        nint instanceId,
        nint output,
        nuint outputCapacity,
        nint outputLength,
        int timeoutMilliseconds)
    {
        InteropErrorState.Clear();

        try
        {
            nuint* outputLengthPointer = (nuint*)outputLength;
            byte* outputPointer = (byte*)output;

            if (outputLengthPointer == null)
            {
                return Fail(MagicMcpStatus.InvalidArgument, "outputLength cannot be null.");
            }

            *outputLengthPointer = 0;

            if (outputPointer == null && outputCapacity != 0)
            {
                return Fail(MagicMcpStatus.InvalidArgument, "A non-zero output capacity requires an output buffer.");
            }

            if (outputCapacity > int.MaxValue)
            {
                return Fail(MagicMcpStatus.InvalidArgument, "The output buffer is too large for this ABI version.");
            }

            Guid id = ReadInstanceId((byte*)instanceId);
            if (!PackageRuntime.TryGetInstance(id, out PackageInstance? instance) || instance is null)
            {
                return Fail(MagicMcpStatus.InstanceNotFound, "The package instance does not exist.");
            }

            PackageReceiveResult result = instance
                .ReceiveAsync((int)outputCapacity, timeoutMilliseconds)
                .GetAwaiter()
                .GetResult();

            *outputLengthPointer = (nuint)result.RequiredLength;

            if (result.Status == MagicMcpStatus.Success)
            {
                result.Message!.AsSpan().CopyTo(new Span<byte>(outputPointer, result.Message.Length));
            }
            else if (result.Status == MagicMcpStatus.BufferTooSmall)
            {
                InteropErrorState.Set($"The receive buffer requires {result.RequiredLength} bytes.");
            }

            return (int)result.Status;
        }
        catch (ArgumentException exception)
        {
            return Fail(MagicMcpStatus.InvalidArgument, exception);
        }
        catch (ObjectDisposedException exception)
        {
            return Fail(MagicMcpStatus.InstanceStopped, exception);
        }
        catch (OperationCanceledException exception)
        {
            return Fail(MagicMcpStatus.InstanceStopped, exception);
        }
        catch (Exception exception)
        {
            return Fail(MagicMcpStatus.InternalError, exception);
        }
    }

    public static int StopInstance(nint instanceId)
    {
        InteropErrorState.Clear();

        try
        {
            Guid id = ReadInstanceId((byte*)instanceId);
            bool stopped = PackageRuntime.StopInstanceAsync(id).GetAwaiter().GetResult();

            return stopped
                ? (int)MagicMcpStatus.Success
                : Fail(MagicMcpStatus.InstanceNotFound, "The package instance does not exist.");
        }
        catch (ArgumentException exception)
        {
            return Fail(MagicMcpStatus.InvalidArgument, exception);
        }
        catch (Exception exception)
        {
            return Fail(MagicMcpStatus.InternalError, exception);
        }
    }

    public static int ListInstances(nint output, nuint outputCapacity, nint outputLength)
    {
        InteropErrorState.Clear();

        try
        {
            Guid[] ids = PackageRuntime.GetInstanceIds();
            byte[] bytes = new byte[checked(ids.Length * InstanceIdSize)];

            for (int index = 0; index < ids.Length; index++)
            {
                ids[index].TryWriteBytes(bytes.AsSpan(index * InstanceIdSize, InstanceIdSize));
            }

            return CopyToCaller(
                bytes,
                (byte*)output,
                outputCapacity,
                (nuint*)outputLength);
        }
        catch (Exception exception)
        {
            return Fail(MagicMcpStatus.InternalError, exception);
        }
    }

    public static int Shutdown()
    {
        InteropErrorState.Clear();

        try
        {
            PackageRuntime.ShutdownAsync().GetAwaiter().GetResult();
            return (int)MagicMcpStatus.Success;
        }
        catch (Exception exception)
        {
            return Fail(MagicMcpStatus.InternalError, exception);
        }
    }

    public static int GetLastError(nint output, nuint outputCapacity, nint outputLength)
    {
        try
        {
            byte[] error = InteropErrorState.GetUtf8();
            return CopyToCaller(
                error,
                (byte*)output,
                outputCapacity,
                (nuint*)outputLength,
                preserveLastError: true);
        }
        catch (Exception exception)
        {
            return Fail(MagicMcpStatus.InternalError, exception);
        }
    }

    private static void ValidateConfiguration(ReadOnlyMemory<byte> configuration)
    {
        if (configuration.IsEmpty)
        {
            return;
        }

        using JsonDocument document = JsonDocument.Parse(configuration);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "configurationJson must contain one UTF-8 JSON object.",
                nameof(configuration));
        }
    }

    private static Guid ReadInstanceId(byte* instanceId)
    {
        if (instanceId == null)
        {
            throw new ArgumentException("instanceId must point to exactly 16 bytes.", nameof(instanceId));
        }

        return new Guid(new ReadOnlySpan<byte>(instanceId, InstanceIdSize));
    }

    private static byte[] CopyInput(byte* input, nuint length, string parameterName)
    {
        if (input == null && length != 0)
        {
            throw new ArgumentException("A non-zero length requires a non-null input pointer.", parameterName);
        }

        if (length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The input exceeds the ABI v1 size limit.");
        }

        return input == null
            ? []
            : new ReadOnlySpan<byte>(input, checked((int)length)).ToArray();
    }

    private static int CopyToCaller(
        ReadOnlySpan<byte> source,
        byte* output,
        nuint outputCapacity,
        nuint* outputLength,
        bool preserveLastError = false)
    {
        if (outputLength == null)
        {
            if (!preserveLastError)
            {
                InteropErrorState.Set("outputLength cannot be null.");
            }

            return (int)MagicMcpStatus.InvalidArgument;
        }

        *outputLength = (nuint)source.Length;

        if (source.IsEmpty)
        {
            return (int)MagicMcpStatus.Success;
        }

        if (output == null || outputCapacity < (nuint)source.Length)
        {
            if (!preserveLastError)
            {
                InteropErrorState.Set($"The output buffer requires {source.Length} bytes.");
            }

            return (int)MagicMcpStatus.BufferTooSmall;
        }

        source.CopyTo(new Span<byte>(output, source.Length));
        return (int)MagicMcpStatus.Success;
    }

    private static int Fail(MagicMcpStatus status, string message)
    {
        InteropErrorState.Set(message);
        return (int)status;
    }

    private static int Fail(MagicMcpStatus status, Exception exception)
    {
        InteropErrorState.Set(exception);
        return (int)status;
    }
}
