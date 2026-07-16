using MagicAiGateway.Protocol;
using SharedMagic.Execution;

namespace MagicAiApi.Protocol;

public sealed record GatewayModelInvocation(
    MagicChatCompletionRequest Request,
    GatewayRunContext Run,
    IReadOnlyList<MagicToolDefinition> Tools);

public interface IGatewayModelInvoker
{
    ValueTask<MagicModelTurn> InvokeAsync(
        GatewayModelInvocation invocation,
        CancellationToken cancellationToken);
}

public sealed record ToolCatalogContext(
    GatewayRunContext Run,
    ManagedToolsOptions Options);

public interface IToolCatalogProvider
{
    ValueTask<IReadOnlyList<MagicToolDefinition>> GetToolsAsync(
        ToolCatalogContext context,
        CancellationToken cancellationToken);
}

public sealed record ToolExecutionContext(
    GatewayRunContext Run,
    ManagedToolsOptions Options);

public sealed record MagicToolExecutionResult(
    bool Success,
    string Content,
    string? Error = null);

public interface IToolCallExecutor
{
    ValueTask<MagicToolExecutionResult> ExecuteAsync(
        MagicToolCall call,
        ToolExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IManagedToolRunService
{
    ValueTask RunAsync(
        GatewayRunContext context,
        ManagedToolsOptions options,
        IGatewayRunOutput output,
        CancellationToken cancellationToken);
}

public sealed class ManagedToolServiceNotReadyException(string message) : InvalidOperationException(message);

/// <summary>
/// Placeholder that preserves the complete managed-tool seam without pretending MCP execution exists yet.
/// Replace this registration with the real model/MCP continuation service.
/// </summary>
public sealed class UnavailableManagedToolRunService : IManagedToolRunService
{
    public ValueTask RunAsync(
        GatewayRunContext context,
        ManagedToolsOptions options,
        IGatewayRunOutput output,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new ManagedToolServiceNotReadyException(
            "Managed tool continuation is registered and discoverable, but the MCP model/tool loop has not been installed yet."));
}

public sealed class ManagedToolsProtocolService(IManagedToolRunService runner)
    : MagicProtocolService<ManagedToolsOptions>(new MagicServiceDescriptor
    {
        Name = MagicServiceNames.ManagedTools,
        Version = 1,
        Description = "Discovers tools through the configured MCP provider, executes tool calls, and continues the model until a final assistant response is produced.",
        Availability = "scaffolded",
        SupportedEndpoints = ["/v1/chat/completions"],
        DefaultRunTimeoutSeconds = 30 * 60,
        MaximumRunTimeoutSeconds = 60 * 60,
        ResponseSchema = MagicServiceSchemaBuilder.Build(typeof(MagicChatCompletionResponse)),
        InvocationExample = MagicServiceDescriptorExamples.CreateInvocation(
            MagicServiceNames.ManagedTools,
            serviceVersion: 1,
            new ManagedToolsOptions
            {
                McpProfile = "default",
                MaximumRounds = 16,
                MaximumToolCalls = 64
            }),
        ResponseExample = MagicServiceDescriptorExamples.CreateResponse(MagicServiceNames.ManagedTools),
        StreamingEvents =
        [
            MagicStreamEventTypes.RunStarted,
            MagicStreamEventTypes.ToolStarted,
            MagicStreamEventTypes.ToolCompleted,
            MagicStreamEventTypes.ReasoningDelta,
            MagicStreamEventTypes.RunCompleted,
            MagicStreamEventTypes.RunFailed
        ]
    })
{
    protected override ValueTask ContributePlanAsync(
        MagicExecutionPlanBuilder builder,
        MagicServicePlanningContext context,
        ManagedToolsOptions options,
        CancellationToken cancellationToken)
    {
        builder.Add(new DelegateMagicExecutionStep(
            "managed-tools.validate",
            MagicExecutionPhase.Validate,
            priority: 0,
            MagicStepAccess.ReadOnly(MagicRunResource.Transcript),
            (_, _) =>
            {
                if (options.MaximumRounds <= 0)
                {
                    throw new InvalidOperationException("managed_tools.maximum_rounds must be greater than zero.");
                }
                if (options.MaximumToolCalls <= 0)
                {
                    throw new InvalidOperationException("managed_tools.maximum_tool_calls must be greater than zero.");
                }
                return ValueTask.CompletedTask;
            }));

        builder.Add(new DelegateMagicExecutionStep(
            "managed-tools.run",
            MagicExecutionPhase.Execute,
            priority: 0,
            MagicStepAccess.Exclusive(
                MagicRunResource.Transcript | MagicRunResource.ModelRequest,
                MagicRunResource.Transcript |
                MagicRunResource.ModelRequest |
                MagicRunResource.ModelResponse |
                MagicRunResource.ClientResponse |
                MagicRunResource.Journal |
                MagicRunResource.Usage),
            (run, token) => runner.RunAsync(run, options, context.Output, token)));

        return ValueTask.CompletedTask;
    }
}
