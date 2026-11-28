using LibraGpu.Core;

namespace LiberaFx.Services.Gpu;

public static class GpuGraphLayout
{
    public static double NodeHeight(GpuNode node) =>
        80 + 28 * GpuOperationRegistry.Operations[node.Op].MaxInputs;

    private static bool IsStageInputNode(GpuNode node) =>
        (node.Op == GpuOp.ReadAttribute && node.Attribute is not null) ||
        (node.Op == GpuOp.ReadUniform && node.Uniform is "deltaTime" or "time" or "emitterPosition");

    public static Dictionary<string, GpuPosition> CreatePositions(KernelGraph kernel)
    {
        var visible = kernel.Nodes.Where(n => !IsStageInputNode(n)).ToArray();
        var nodes = visible.ToDictionary(n => n.Id);
        var order = visible.Select((n, i) => (n.Id, Index: i)).ToDictionary(n => n.Id, n => n.Index);
        var inputs = visible.ToDictionary(n => n.Id,
            n => n.Inputs.Where(nodes.ContainsKey).Distinct().ToArray());
        var consumers = visible.ToDictionary(n => n.Id, _ => new List<string>());
        foreach (var node in visible)
            foreach (var source in inputs[node.Id])
                consumers[source].Add(node.Id);
        var remaining = inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Length);
        var levels = visible.ToDictionary(n => n.Id, _ => 0);
        var ready = new Queue<string>(visible.Where(n => remaining[n.Id] == 0).Select(n => n.Id));
        while (ready.TryDequeue(out var source))
            foreach (var target in consumers[source])
            {
                levels[target] = Math.Max(levels[target], levels[source] + 1);
                if (--remaining[target] == 0) ready.Enqueue(target);
            }

        // Invalid cyclic drafts remain editable; layout must not recurse or change the IR.
        foreach (var node in visible.Where(n => remaining[n.Id] > 0))
            levels[node.Id] = 0;
        var positions = new Dictionary<string, GpuPosition>();
        var visited = new HashSet<string>();
        var bandTop = 30d;
        foreach (var root in visible)
        {
            if (!visited.Add(root.Id)) continue;
            var component = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root.Id);
            while (pending.TryPop(out var id))
            {
                component.Add(id);
                foreach (var adjacent in inputs[id].Concat(consumers[id]))
                    if (visited.Add(adjacent)) pending.Push(adjacent);
            }

            double DesiredTop(string id) => inputs[id].Where(positions.ContainsKey)
                .Select(source => positions[source].Y + NodeHeight(nodes[source]) / 2)
                .DefaultIfEmpty(bandTop + NodeHeight(nodes[id]) / 2).Average() - NodeHeight(nodes[id]) / 2;
            var bottom = bandTop;
            foreach (var layer in component.GroupBy(id => levels[id]).OrderBy(group => group.Key))
            {
                var cursor = bandTop;
                foreach (var id in layer.OrderBy(DesiredTop).ThenBy(id => order[id]).ToArray())
                {
                    var y = Math.Max(cursor, DesiredTop(id));
                    positions[id] = new(30 + layer.Key * 320, y);
                    cursor = y + NodeHeight(nodes[id]) + 48;
                    bottom = Math.Max(bottom, cursor - 48);
                }
            }
            bandTop = bottom + 100;
        }
        return positions.ToDictionary(pair => GpuParticleEditorService.Key(kernel.Id, pair.Key), pair => pair.Value);
    }
}
