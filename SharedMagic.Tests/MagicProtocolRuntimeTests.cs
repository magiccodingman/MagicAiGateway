using System.Text.Json;
using MagicAiGateway.Protocol;
using SharedMagic.Execution;

namespace SharedMagic.Tests;

public sealed class MagicProtocolRuntimeTests
{
    [Fact]
    public void ChatRequestSerializesMessagesAndMagicEnvelopeAtDifferentLevels()
    {
        var request = new MagicChatCompletionRequest
        {
            Model = "Qwen36-27B",
            Messages = [MagicChatMessage.User("Hello")],
            MagicAiGateway = new MagicAiGatewayEnvelope
            {
                Application = "GameShow",
                Service = MagicServiceInvocation.Create(
                    MagicServiceNames.ManagedTools,
                    new ManagedToolsOptions { McpProfile = "game-show" })
            }
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, MagicProtocolJson.Options));
        var root = document.RootElement;

        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.True(root.TryGetProperty(MagicAiGatewayProtocol.PropertyName, out var magic));
        Assert.Equal("GameShow", magic.GetProperty("application").GetString());
        Assert.False(root.GetProperty("messages")[0].TryGetProperty(MagicAiGatewayProtocol.PropertyName, out _));
    }

    [Fact]
    public void SamePriorityTranscriptWritersAreRejected()
    {
        var builder = new MagicExecutionPlanBuilder()
            .Add(Step("first", MagicExecutionPhase.Execute, 0, writes: MagicRunResource.Transcript))
            .Add(Step("second", MagicExecutionPhase.Execute, 0, writes: MagicRunResource.Transcript));

        var exception = Assert.Throws<MagicExecutionPlanConflictException>(() => builder.Build());
        Assert.Contains("Transcript", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SamePriorityReadOnlyObserversCanRunTogether()
    {
        var plan = new MagicExecutionPlanBuilder()
            .Add(Step("audit", MagicExecutionPhase.PostProcess, 0, reads: MagicRunResource.ModelResponse))
            .Add(Step("metrics", MagicExecutionPhase.PostProcess, 0, reads: MagicRunResource.ModelResponse))
            .Build();

        var group = Assert.Single(plan.Groups);
        Assert.Equal(2, group.Steps.Count);
    }

    [Fact]
    public async Task ExecutionGroupsRunInPhaseAndPriorityOrder()
    {
        var order = new List<string>();
        var context = CreateRunContext();
        var plan = new MagicExecutionPlanBuilder()
            .Add(Step("final", MagicExecutionPhase.Finalize, 0, execute: () => order.Add("final")))
            .Add(Step("execute-two", MagicExecutionPhase.Execute, 10, execute: () => order.Add("execute-two")))
            .Add(Step("prepare", MagicExecutionPhase.Prepare, 0, execute: () => order.Add("prepare")))
            .Add(Step("execute-one", MagicExecutionPhase.Execute, 0, execute: () => order.Add("execute-one")))
            .Build();

        await new MagicExecutionPlanExecutor().ExecuteAsync(plan, context, CancellationToken.None);

        Assert.Equal(["prepare", "execute-one", "execute-two", "final"], order);
    }

    [Fact]
    public void ContinuationTranscriptDoesNotLeakMagicEnvelopeToBackend()
    {
        var original = new MagicChatCompletionRequest
        {
            Model = "Qwen36-27B",
            Messages = [MagicChatMessage.User("Use a tool")],
            MagicAiGateway = new MagicAiGatewayEnvelope
            {
                Service = MagicServiceInvocation.Create(
                    MagicServiceNames.ManagedTools,
                    new ManagedToolsOptions())
            }
        };
        var transcript = new GatewayConversationTranscript(original.Messages);
        transcript.Append(new MagicChatMessage
        {
            Role = "assistant",
            ToolCalls =
            [
                new MagicToolCall
                {
                    Id = "call_1",
                    Function = new MagicFunctionCall { Name = "weather", Arguments = "{}" }
                }
            ]
        });
        transcript.Append(MagicChatMessage.Tool("call_1", "sunny"));

        var continuation = transcript.CreateContinuationRequest(original);

        Assert.Null(continuation.MagicAiGateway);
        Assert.Equal(3, continuation.Messages.Count);
        Assert.Equal("tool", continuation.Messages[^1].Role);
    }

    [Fact]
    public void UsageAccumulatorReturnsWholeLogicalRunTotals()
    {
        var usage = new MagicUsageAccumulator();
        usage.Add(new MagicTokenUsage { PromptTokens = 1000, CompletionTokens = 150, TotalTokens = 1150 });
        usage.Add(new MagicTokenUsage { PromptTokens = 1450, CompletionTokens = 200, TotalTokens = 1650 });
        usage.Add(new MagicTokenUsage
        {
            PromptTokens = 1750,
            CompletionTokens = 600,
            TotalTokens = 2350,
            CompletionTokenDetails = new MagicCompletionTokenDetails { ReasoningTokens = 700 }
        });

        Assert.Equal(4200, usage.Total.PromptTokens);
        Assert.Equal(950, usage.Total.CompletionTokens);
        Assert.Equal(5150, usage.Total.TotalTokens);
        Assert.Equal(700, usage.Total.CompletionTokenDetails?.ReasoningTokens);
        Assert.Equal(3, usage.Calls.Count);
    }

    [Theory]
    [InlineData(null, 300)]
    [InlineData(30, 30)]
    [InlineData(7200, 3600)]
    public void RunDurationUsesServiceDefaultAndClampsCallerRequest(int? requestedSeconds, int expectedSeconds)
    {
        var limits = new GatewayRunLimits
        {
            DefaultDuration = TimeSpan.FromMinutes(5),
            MaximumDuration = TimeSpan.FromHours(1)
        };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), limits.ResolveDuration(requestedSeconds));
    }

    private static IMagicExecutionStep Step(
        string name,
        MagicExecutionPhase phase,
        int priority,
        MagicRunResource reads = MagicRunResource.None,
        MagicRunResource writes = MagicRunResource.None,
        Action? execute = null) =>
        new DelegateMagicExecutionStep(
            name,
            phase,
            priority,
            new MagicStepAccess(reads, writes),
            (_, _) =>
            {
                execute?.Invoke();
                return ValueTask.CompletedTask;
            });

    private static GatewayRunContext CreateRunContext()
    {
        var request = new MagicChatCompletionRequest
        {
            Model = "Qwen36-27B",
            Messages = [MagicChatMessage.User("Hello")],
            MagicAiGateway = new MagicAiGatewayEnvelope
            {
                Service = MagicServiceInvocation.Create(
                    MagicServiceNames.ManagedTools,
                    new ManagedToolsOptions())
            }
        };
        var limits = new GatewayRunLimits();
        return new GatewayRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            OriginalRequest = request,
            Protocol = request.MagicAiGateway!,
            Service = new MagicServiceDescriptor
            {
                Name = MagicServiceNames.ManagedTools,
                Description = "test",
                SupportedEndpoints = ["/v1/chat/completions"],
                DefaultRunTimeoutSeconds = 300,
                MaximumRunTimeoutSeconds = 1800
            },
            Caller = new GatewayCallerContext("test", new HashSet<string>(), new HashSet<string>(["*"]), false),
            Application = new GatewayApplicationContext(
                MagicAiGatewayProtocol.DefaultApplicationName,
                new HashSet<string>(),
                new HashSet<string>(["*"]),
                new HashSet<string>(["*"])),
            Limits = limits,
            Transcript = new GatewayConversationTranscript(request.Messages),
            Journal = new GatewayRunJournal(limits.MaximumJournalCharacters)
        };
    }
}
