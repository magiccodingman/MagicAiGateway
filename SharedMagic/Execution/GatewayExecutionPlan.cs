namespace SharedMagic.Execution;

public enum MagicExecutionPhase
{
    Validate = 0,
    Prepare = 100,
    Execute = 200,
    PostProcess = 300,
    Finalize = 400
}

[Flags]
public enum MagicRunResource
{
    None = 0,
    Transcript = 1 << 0,
    ModelRequest = 1 << 1,
    ModelResponse = 1 << 2,
    ClientResponse = 1 << 3,
    ServiceState = 1 << 4,
    Journal = 1 << 5,
    Usage = 1 << 6
}

public sealed record MagicStepAccess(
    MagicRunResource Reads = MagicRunResource.None,
    MagicRunResource Writes = MagicRunResource.None,
    bool ParallelSafe = true)
{
    public static MagicStepAccess ReadOnly(MagicRunResource reads) => new(reads, MagicRunResource.None, true);
    public static MagicStepAccess Exclusive(MagicRunResource reads, MagicRunResource writes) => new(reads, writes, false);
}

public interface IMagicExecutionStep
{
    string Name { get; }
    MagicExecutionPhase Phase { get; }
    int Priority { get; }
    MagicStepAccess Access { get; }
    ValueTask ExecuteAsync(GatewayRunContext context, CancellationToken cancellationToken);
}

public sealed class DelegateMagicExecutionStep(
    string name,
    MagicExecutionPhase phase,
    int priority,
    MagicStepAccess access,
    Func<GatewayRunContext, CancellationToken, ValueTask> execute) : IMagicExecutionStep
{
    public string Name { get; } = name;
    public MagicExecutionPhase Phase { get; } = phase;
    public int Priority { get; } = priority;
    public MagicStepAccess Access { get; } = access;

    public ValueTask ExecuteAsync(GatewayRunContext context, CancellationToken cancellationToken) =>
        execute(context, cancellationToken);
}

public sealed record MagicExecutionGroup(
    MagicExecutionPhase Phase,
    int Priority,
    IReadOnlyList<IMagicExecutionStep> Steps);

public sealed class MagicExecutionPlan
{
    internal MagicExecutionPlan(IReadOnlyList<MagicExecutionGroup> groups) => Groups = groups;
    public IReadOnlyList<MagicExecutionGroup> Groups { get; }
}

public sealed class MagicExecutionPlanConflictException(string message) : InvalidOperationException(message);

public sealed class MagicExecutionPlanBuilder
{
    private readonly List<IMagicExecutionStep> _steps = [];

    public MagicExecutionPlanBuilder Add(IMagicExecutionStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (string.IsNullOrWhiteSpace(step.Name)) throw new InvalidOperationException("Execution steps require a name.");
        _steps.Add(step);
        return this;
    }

    public MagicExecutionPlan Build()
    {
        var duplicate = _steps
            .GroupBy(static step => step.Name, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new MagicExecutionPlanConflictException($"Execution step name '{duplicate.Key}' is registered more than once.");
        }

        var groups = _steps
            .GroupBy(static step => (step.Phase, step.Priority))
            .OrderBy(static group => group.Key.Phase)
            .ThenBy(static group => group.Key.Priority)
            .Select(group =>
            {
                var steps = group.OrderBy(static step => step.Name, StringComparer.Ordinal).ToArray();
                ValidateGroup(group.Key.Phase, group.Key.Priority, steps);
                return new MagicExecutionGroup(group.Key.Phase, group.Key.Priority, steps);
            })
            .ToArray();

        return new MagicExecutionPlan(groups);
    }

    private static void ValidateGroup(
        MagicExecutionPhase phase,
        int priority,
        IReadOnlyList<IMagicExecutionStep> steps)
    {
        if (steps.Count <= 1) return;

        var nonParallel = steps.FirstOrDefault(static step => !step.Access.ParallelSafe);
        if (nonParallel is not null)
        {
            throw new MagicExecutionPlanConflictException(
                $"Step '{nonParallel.Name}' is exclusive but shares phase {phase} priority {priority} with another step.");
        }

        for (var leftIndex = 0; leftIndex < steps.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < steps.Count; rightIndex++)
            {
                var left = steps[leftIndex];
                var right = steps[rightIndex];
                var leftConflict = left.Access.Writes & (right.Access.Reads | right.Access.Writes);
                var rightConflict = right.Access.Writes & (left.Access.Reads | left.Access.Writes);
                if (leftConflict != MagicRunResource.None || rightConflict != MagicRunResource.None)
                {
                    throw new MagicExecutionPlanConflictException(
                        $"Steps '{left.Name}' and '{right.Name}' conflict in phase {phase} priority {priority}. " +
                        $"Conflicting resources: {leftConflict | rightConflict}.");
                }
            }
        }
    }
}

public interface IMagicExecutionPlanExecutor
{
    ValueTask ExecuteAsync(
        MagicExecutionPlan plan,
        GatewayRunContext context,
        CancellationToken cancellationToken);
}

public sealed class MagicExecutionPlanExecutor : IMagicExecutionPlanExecutor
{
    public async ValueTask ExecuteAsync(
        MagicExecutionPlan plan,
        GatewayRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var group in plan.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Journal.Record("execution.group.started", new { phase = group.Phase.ToString(), group.Priority });
            var tasks = group.Steps
                .Select(step => ExecuteStepAsync(step, context, cancellationToken))
                .ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            context.Journal.Record("execution.group.completed", new { phase = group.Phase.ToString(), group.Priority });
        }
    }

    private static async Task ExecuteStepAsync(
        IMagicExecutionStep step,
        GatewayRunContext context,
        CancellationToken cancellationToken)
    {
        context.Journal.Record("execution.step.started", step.Name);
        await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        context.Journal.Record("execution.step.completed", step.Name);
    }
}
