namespace NativeBot.Core;

public delegate Task<BlockResult> BlockHandler(FlowNode node, VariableContext variables, CancellationToken cancellationToken);

public sealed class FlowEngine(IReadOnlyDictionary<string, BlockHandler> handlers, int maxSteps = 1000)
{
    public async Task<NodeOutcome> ExecuteAsync(
        FlowDefinition flow,
        VariableContext variables,
        CancellationToken cancellationToken = default)
    {
        var nodes = flow.Nodes.ToDictionary(x => x.Id);
        var current = flow.EntryNodeId;

        for (var step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!nodes.TryGetValue(current, out var node))
                return NodeOutcome.Error;
            if (node.Kind is NodeKind.End)
                return NodeOutcome.Success;
            if (!handlers.TryGetValue(node.Handler, out var handler))
                return NodeOutcome.Error;

            var result = await handler(node, variables, cancellationToken);
            if (node.Next is null || !node.Next.TryGetValue(result.Outcome, out current))
                return result.Outcome;
        }

        return NodeOutcome.Error;
    }
}
