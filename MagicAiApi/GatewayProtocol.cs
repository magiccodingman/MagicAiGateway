using System.Text.Json;
using System.Text.Json.Nodes;
using MagicAiApi.Protocol;
using MagicAiGateway.Protocol;
using SharedMagic.Execution;

namespace MagicAiApi;

/// <summary>
/// Hosts Magic protocol requests. The envelope selects one public service; the server resolves
/// security/application context, compiles the internal execution plan, and owns the entire HTTP run lifecycle.
/// </summary>
public sealed class MagicProtocolHost(
    IMagicProtocolServiceRegistry services,
    IEnumerable<IMagicExecutionPlanContributor> contributors,
    IGatewayCallerContextResolver callerResolver,
    IGatewayApplicationResolver applicationResolver,
    IGatewayAgentResolver agentResolver,
    IGatewayServiceAuthorizationService authorization,
    IGatewayRunManager runManager,
    IMagicExecutionPlanExecutor executor,
    IHostApplicationLifetime lifetime,
    ILogger<MagicProtocolHost> logger)
{
    public async Task ExecuteAsync(
        HttpContext httpContext,
        JsonElement envelopeElement,
        CancellationToken cancellationToken)
    {
        MagicAiGatewayEnvelope envelope;
        MagicChatCompletionRequest request;
        try
        {
            envelope = envelopeElement.Deserialize<MagicAiGatewayEnvelope>(MagicProtocolJson.Options)
                       ?? throw new JsonException("The magic_ai_gateway envelope is empty.");
            ValidateEnvelope(envelope);
            request = await ReadChatRequestAsync(httpContext.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            await WriteProtocolErrorAsync(
                httpContext,
                StatusCodes.Status400BadRequest,
                "invalid_magic_request",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!services.TryGet(envelope.Service.Name, envelope.Service.Version, out var service))
        {
            await WriteProtocolErrorAsync(
                httpContext,
                StatusCodes.Status404NotFound,
                "service_not_found",
                $"Magic service '{envelope.Service.Name}' version {envelope.Service.Version} is not installed.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!service!.Descriptor.SupportedEndpoints.Any(path =>
                string.Equals(path, httpContext.Request.Path.Value?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            await WriteProtocolErrorAsync(
                httpContext,
                StatusCodes.Status400BadRequest,
                "service_endpoint_not_supported",
                $"Magic service '{service.Descriptor.Name}' does not support endpoint '{httpContext.Request.Path}'.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var caller = await callerResolver.ResolveAsync(httpContext.User, cancellationToken).ConfigureAwait(false);
        var application = await applicationResolver.ResolveAsync(
            caller,
            envelope.Application,
            cancellationToken).ConfigureAwait(false);
        var agent = await agentResolver.ResolveAsync(
            application,
            envelope.Agent,
            cancellationToken).ConfigureAwait(false);
        if (!await authorization.IsAllowedAsync(
                caller,
                application,
                agent,
                service.Descriptor,
                cancellationToken).ConfigureAwait(false))
        {
            await WriteProtocolErrorAsync(
                httpContext,
                StatusCodes.Status403Forbidden,
                "service_forbidden",
                $"The caller is not permitted to use service '{service.Descriptor.Name}' for application '{application.Name}'.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var limits = new GatewayRunLimits
        {
            DefaultDuration = TimeSpan.FromSeconds(service.Descriptor.DefaultRunTimeoutSeconds),
            MaximumDuration = TimeSpan.FromSeconds(service.Descriptor.MaximumRunTimeoutSeconds)
        };
        TimeSpan duration;
        object serviceOptions;
        try
        {
            duration = limits.ResolveDuration(envelope.RequestedRunTimeoutSeconds);
            serviceOptions = service.DeserializeOptions(envelope.Service.Options);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            await WriteProtocolErrorAsync(
                httpContext,
                StatusCodes.Status400BadRequest,
                "invalid_service_options",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var run = new GatewayRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            OriginalRequest = request,
            Protocol = envelope,
            Service = service.Descriptor,
            Caller = caller,
            Application = application,
            Agent = agent,
            Limits = limits,
            Transcript = new GatewayConversationTranscript(request.Messages),
            Journal = new GatewayRunJournal(limits.MaximumJournalCharacters)
        };
        var output = new HttpGatewayRunOutput(httpContext, request.Stream, envelope.ResponseMode);
        await using var lease = runManager.Start(
            run,
            duration,
            httpContext.RequestAborted,
            lifetime.ApplicationStopping);

        try
        {
            var planning = new MagicServicePlanningContext(run, output);
            var builder = new MagicExecutionPlanBuilder();
            await service.ContributePlanAsync(builder, planning, serviceOptions, lease.CancellationToken).ConfigureAwait(false);
            foreach (var contributor in contributors)
            {
                await contributor.ContributeAsync(builder, planning, lease.CancellationToken).ConfigureAwait(false);
            }

            var plan = builder.Build();
            await executor.ExecuteAsync(plan, run, lease.CancellationToken).ConfigureAwait(false);
            if (!output.IsCompleted)
            {
                run.Status = MagicRunStatuses.Failed;
                await output.FailAsync(
                    new MagicRunError
                    {
                        Code = "service_did_not_complete",
                        Message = $"Magic service '{service.Descriptor.Name}' finished its execution plan without completing the response.",
                        Retryable = false
                    },
                    CreateMetadata(run, MagicRunStatuses.Failed, "error"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (ManagedToolServiceNotReadyException exception)
        {
            run.Status = MagicRunStatuses.Failed;
            await output.FailAsync(
                new MagicRunError
                {
                    Code = "service_not_ready",
                    Message = exception.Message,
                    Retryable = false
                },
                CreateMetadata(run, MagicRunStatuses.Failed, "error"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            var disconnected = httpContext.RequestAborted.IsCancellationRequested;
            run.Status = disconnected ? MagicRunStatuses.Cancelled : MagicRunStatuses.TimedOut;
            if (!disconnected)
            {
                await output.FailAsync(
                    new MagicRunError
                    {
                        Code = "run_timed_out",
                        Message = $"Magic run exceeded its {duration.TotalSeconds:0}-second deadline.",
                        Retryable = true
                    },
                    CreateMetadata(run, run.Status, "timeout"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (MagicExecutionPlanConflictException exception)
        {
            run.Status = MagicRunStatuses.Failed;
            logger.LogError(exception, "Magic run {RunId} produced an invalid execution plan.", run.RunId);
            await output.FailAsync(
                new MagicRunError
                {
                    Code = "execution_plan_conflict",
                    Message = exception.Message,
                    Retryable = false
                },
                CreateMetadata(run, run.Status, "error"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            run.Status = MagicRunStatuses.Failed;
            logger.LogError(exception, "Magic run {RunId} failed.", run.RunId);
            await output.FailAsync(
                new MagicRunError
                {
                    Code = "magic_run_failed",
                    Message = "The Magic protocol service failed while processing the request.",
                    Retryable = false
                },
                CreateMetadata(run, run.Status, "error"),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static MagicRunMetadata CreateMetadata(
        GatewayRunContext run,
        string status,
        string? finishReason) => new()
    {
        RunId = run.RunId.ToString("N"),
        Service = run.Service.Name,
        Status = status,
        FinishReason = finishReason,
        ModelCalls = run.Usage.Calls.Count,
        ToolCalls = run.ToolCalls,
        Usage = run.Usage.Total,
        UsageAccuracy = run.Usage.Accuracy,
        ModelCallUsage = run.Usage.Calls
    };

    private static async Task<MagicChatCompletionRequest> ReadChatRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Body.CanSeek)
        {
            throw new InvalidOperationException("The request body must be buffered before Magic protocol execution.");
        }

        var originalPosition = request.Body.Position;
        try
        {
            request.Body.Position = 0;
            var parsed = await JsonSerializer.DeserializeAsync<MagicChatCompletionRequest>(
                request.Body,
                MagicProtocolJson.Options,
                cancellationToken).ConfigureAwait(false);
            if (parsed is null) throw new JsonException("The chat-completions request body is empty.");
            if (string.IsNullOrWhiteSpace(parsed.Model)) throw new JsonException("A non-empty model is required.");
            if (parsed.Messages.Count == 0) throw new JsonException("At least one chat message is required.");
            return parsed;
        }
        finally
        {
            request.Body.Position = originalPosition;
        }
    }

    private static void ValidateEnvelope(MagicAiGatewayEnvelope envelope)
    {
        if (envelope.Version < MagicAiGatewayProtocol.MinimumSupportedVersion ||
            envelope.Version > MagicAiGatewayProtocol.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Magic protocol version {envelope.Version} is not supported. " +
                $"Supported versions are {MagicAiGatewayProtocol.MinimumSupportedVersion}-{MagicAiGatewayProtocol.CurrentVersion}.");
        }
        if (string.IsNullOrWhiteSpace(envelope.Service.Name))
        {
            throw new InvalidOperationException("A Magic service name is required.");
        }
        if (envelope.Service.Version <= 0)
        {
            throw new InvalidOperationException("A positive Magic service version is required.");
        }
        if (envelope.ResponseMode is not MagicResponseModes.Compatible and not MagicResponseModes.Enriched)
        {
            throw new InvalidOperationException(
                $"Response mode must be '{MagicResponseModes.Compatible}' or '{MagicResponseModes.Enriched}'.");
        }
    }

    private static async Task WriteProtocolErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = "invalid_request_error",
                ["code"] = code
            }
        };
        await context.Response.WriteAsJsonAsync(body, cancellationToken).ConfigureAwait(false);
    }
}
