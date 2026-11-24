using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

internal sealed record PackageParameterUpdate(
    string ContainerId,
    string NodeId,
    uint InputSlot,
    uint Component,
    bool IsInt32,
    int IntValue,
    double FloatValue);

internal static class PackageParameterDelta
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryCreatePayload(
        GraphSnapshot previous,
        GraphSnapshot current,
        long sequence,
        out byte[]? payload)
    {
        payload = null;
        var changes = new List<PreviewParameterChange>();
        if (!PreviewProtocol.Collect(previous, current, [], changes) || changes.Count == 0)
            return false;

        var updates = new List<PackageParameterUpdate>();
        foreach (var change in changes)
        {
            if (change.Remove || change.Value is not { } value)
                return false;
            var node = FindNode(current, change.GraphPath, change.NodeId);
            if (node is null) return false;
            var definition = GraphNodeDefinition.Find(node.Kind);
            if (definition is null) return false;

            if (!TryMapParameter(definition, node, change.ParameterId, change.Type, value, out var inputSlot, out var isInt32, out var intValues, out var floatValues))
                return false;

            var containerId = change.GraphPath.Count == 0
                ? Guid.Empty.ToString("D")
                : change.GraphPath[^1].ToString("D");
            var nodeId = node.Id.ToString("D");

            if (isInt32)
            {
                updates.Add(new(containerId, nodeId, inputSlot, 0, true, intValues[0], 0));
            }
            else
            {
                for (var c = 0; c < floatValues.Length; c++)
                    updates.Add(new(containerId, nodeId, inputSlot, (uint)c, false, 0, floatValues[c]));
            }
        }

        if (updates.Count == 0) return false;
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "parameter_delta",
            sequence,
            updates
        }, JsonOptions);
        if (json.Length > CompiledPreviewProtocol.MaxSceneBytes) return false;
        payload = CompiledPreviewProtocol.CreateScene(json);
        return true;
    }

    private static GraphNodeSnapshot? FindNode(GraphSnapshot graph, IReadOnlyList<Guid> path, Guid nodeId)
    {
        var current = graph;
        foreach (var scopeId in path)
        {
            var container = current.Nodes.FirstOrDefault(n => n.Id == scopeId);
            if (container?.ChildGraph is null) return null;
            current = container.ChildGraph;
        }
        return current.Nodes.FirstOrDefault(n => n.Id == nodeId);
    }

    private static bool TryMapParameter(
        GraphNodeDefinition definition,
        GraphNodeSnapshot node,
        string parameterId,
        string type,
        JsonElement value,
        out uint inputSlot,
        out bool isInt32,
        out int[] intValues,
        out double[] floatValues)
    {
        inputSlot = 0;
        isInt32 = false;
        intValues = [];
        floatValues = [];

        if (parameterId.StartsWith("editor.", StringComparison.Ordinal) ||
            type is "gpuState" or "proceduralState" or "spriteData" or "string" ||
            definition.Kind.Value is "CpuParticle" or "GpuParticle" or "ModelDraw" or "SpriteDraw" or
                "FlexiblePlaneDraw" or "CpuParticleSpriteDraw" or "ProceduralMaterial" or "ProceduralTexture" or
                "ProceduralModel")
            return false;

        var parameterPorts = (definition.InputPorts ?? [])
            .Where(p => p.Category != GraphPortCategory.Signal)
            .ToArray();
        var portIndex = Array.FindIndex(parameterPorts, p => p.Id == parameterId);
        if (portIndex >= 0)
        {
            inputSlot = (uint)portIndex;
        }
        else if (definition.Parameters.Count == 1 && parameterId == "value")
        {
            inputSlot = 0;
        }
        else
        {
            return false;
        }

        switch (type)
        {
            case "int":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var i)) return false;
                isInt32 = true;
                intValues = [i];
                return true;
            case "bool":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                isInt32 = true;
                intValues = [value.GetBoolean() ? 1 : 0];
                return true;
            case "float":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var f) || !double.IsFinite(f)) return false;
                floatValues = [f];
                return true;
            case "vector2":
            case "vector3":
            case "vector4":
            case "color":
                if (value.ValueKind != JsonValueKind.Array) return false;
                var list = new List<double>(4);
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var d) || !double.IsFinite(d)) return false;
                    list.Add(d);
                }
                if (list.Count is 0 or > 4) return false;
                floatValues = [.. list];
                return true;
            default:
                return false;
        }
    }
}
