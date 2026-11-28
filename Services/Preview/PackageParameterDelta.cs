using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Preview;

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
        out byte[]? payload,
        bool supportsCurveDelta = false)
    {
        payload = null;
        try
        {
            previous = GroupExposedItems.Materialize(previous);
            current = GroupExposedItems.Materialize(current);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        var changes = new List<PreviewParameterChange>();
        var curves = new List<PreviewCurveChange>();
        if (sequence <= 0 || !PreviewProtocol.Collect(previous, current, [], changes, curves) ||
            (changes.Count == 0 && curves.Count == 0) || (curves.Count != 0 && !supportsCurveDelta))
        {
            return false;
        }

        var updates = new List<object>();
        var matrices = new HashSet<Guid>();
        foreach (var change in changes)
        {
            var node = FindNode(current, change.GraphPath, change.NodeId, out var containerId);
            if (node is null)
            {
                return false;
            }
            var definition = GraphNodeDefinition.Find(node.Kind);
            if (definition is null)
            {
                return false;
            }
            definition = EffectiveDefinition(definition, node.Parameters);
            var oldNode = FindNode(previous, change.GraphPath, change.NodeId, out _);
            if (oldNode is null || !SameInputLayout(definition, EffectiveDefinition(
                GraphNodeDefinition.Find(oldNode.Kind)!, oldNode.Parameters)))
            {
                return false;
            }
            if (node.Kind.Value == "MatrixCompose" && change.ParameterId is "row0" or "row1" or "row2" or "row3")
            {
                if (matrices.Add(node.Id))
                {
                    var scope = FindGraph(current, change.GraphPath);
                    if (scope is null || !TryCreateMatrixUpdate(scope, node, definition, containerId, out var matrix))
                    {
                        return false;
                    }
                    updates.Add(matrix);
                }
                continue;
            }
            var sourceValue = change.Value;
            if (change.Remove)
            {
                sourceValue = definition.Parameters.FirstOrDefault(parameter =>
                    parameter.Id == change.ParameterId && parameter.Type == change.Type)?.DefaultValue;
            }
            if (sourceValue is not { } value ||
                !TryMapParameter(definition, node, change.ParameterId, change.Type, value,
                    out var inputSlot, out var isInt32, out var intValues, out var floatValues))
            {
                return false;
            }

            var targetId = node.Id;
            if (GraphMatrixLowering.IsMatrixNode(node.Kind))
            {
                var scope = FindGraph(current, change.GraphPath);
                if (scope is null || !TryResolveMatrixInput(scope, node, change.ParameterId, out targetId))
                {
                    return false;
                }
                inputSlot = 0;
            }
            var nodeId = targetId.ToString("D");
            if (isInt32)
            {
                updates.Add(new PackageParameterUpdate(containerId, nodeId, inputSlot, 0, true, intValues[0], 0));
            }
            else
            {
                for (var c = 0; c < floatValues.Length; c++)
                    updates.Add(new PackageParameterUpdate(containerId, nodeId, inputSlot, (uint)c, false, 0, floatValues[c]));
            }
        }

        foreach (var change in curves)
        {
            var node = FindNode(current, change.GraphPath, change.NodeId, out var containerId);
            if (node is null || node.Kind != GraphNodeKind.FloatFCurve)
            {
                return false;
            }
            try
            {
                FCurve.Validate(change.Curve);
            }
            catch (InvalidDataException)
            {
                return false;
            }
            updates.Add(new
            {
                kind = "fcurve",
                containerId,
                nodeId = node.Id.ToString("D"),
                keys = FCurve.ExpandWeightedCurves(change.Curve.Keys).Select(key => new
                {
                    time = key.TimeSeconds,
                    value = key.Value,
                    inSlope = key.InSlope,
                    outSlope = key.OutSlope,
                    interpolation = (int)key.Interpolation
                }).ToArray()
            });
        }
        if (updates.Count == 0) return false;
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "parameter_delta",
            sequence,
            updates
        }, JsonOptions);
        if (json.Length > CompiledPreviewProtocol.MaxSceneBytes) return false;
        payload = CompiledPreviewProtocol.CreateScene(json, checked((ulong)sequence));
        return true;
    }

    private static GraphNodeDefinition EffectiveDefinition(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? parameters) =>
        DynamicPinSchema.Effective(SequenceSchema.Effective(
            GameIoSchema.Effective(RenderNodeSchema.Effective(definition, parameters), parameters), parameters), parameters);

    private static bool SameInputLayout(GraphNodeDefinition left, GraphNodeDefinition right) =>
        left.PortType == right.PortType && left.PortCategory == right.PortCategory &&
        (left.InputPorts ?? []).Select(port => (port.Id, port.PortType, port.Category))
            .SequenceEqual((right.InputPorts ?? []).Select(port => (port.Id, port.PortType, port.Category))) &&
        (left.OutputPorts ?? []).Select(port => (port.Id, port.PortType, port.Category))
            .SequenceEqual((right.OutputPorts ?? []).Select(port => (port.Id, port.PortType, port.Category)));

    private static GraphSnapshot? FindGraph(GraphSnapshot graph, IReadOnlyList<Guid> path)
    {
        var current = graph;
        foreach (var id in path)
        {
            var child = current.Nodes.FirstOrDefault(node => node.Id == id)?.ChildGraph;
            if (child is null)
            {
                return null;
            }
            current = child;
        }
        return current;
    }

    private static bool TryResolveMatrixInput(GraphSnapshot scope, GraphNodeSnapshot node,
        string parameterId, out Guid runtimeId)
    {
        runtimeId = Guid.Empty;
        if (scope.Connections.Any(edge => edge.TargetNodeId == node.Id && edge.TargetPortId == parameterId))
        {
            return false;
        }
        try
        {
            var lowered = GraphMatrixLowering.Lower(scope);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.matrix:{node.Id:D}:default-{parameterId}"));
            var candidate = new Guid(digest.AsSpan(0, 16));
            if (!lowered.Graph.Nodes.Any(item => item.Id == candidate &&
                item.Parameters?.Any(parameter => parameter.Id == "value") == true))
            {
                return false;
            }
            runtimeId = candidate;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryCreateMatrixUpdate(GraphSnapshot scope, GraphNodeSnapshot node,
        GraphNodeDefinition definition, string containerId, out object update)
    {
        update = null!;
        if (scope.Connections.Any(edge => edge.TargetNodeId == node.Id &&
            edge.TargetPortId is "row0" or "row1" or "row2" or "row3"))
        {
            return false;
        }
        try
        {
            var lowered = GraphMatrixLowering.Lower(scope);
            if (!lowered.Outputs.TryGetValue((node.Id, "value"), out var rows) || rows.Count != 4 ||
                rows.Select(row => row.NodeId).Distinct().Count() != 4)
            {
                return false;
            }
            var values = new List<double>(16);
            for (var row = 0; row < 4; row++)
            {
                var id = $"row{row}";
                var parameter = node.Parameters?.FirstOrDefault(item => item.Id == id);
                var value = parameter?.Value ?? definition.Parameters.Single(item => item.Id == id).DefaultValue;
                if (parameter is not null && parameter.Type != "vector4" ||
                    !NodeParameter.IsValidValue("vector4", value))
                {
                    return false;
                }
                values.AddRange(value.EnumerateArray().Select(item => item.GetDouble()));
            }
            update = new { containerId, kind = "matrix", rowNodeIds = rows.Select(row => row.NodeId.ToString("D")).ToArray(), values };
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static GraphNodeSnapshot? FindNode(GraphSnapshot graph, IReadOnlyList<Guid> path, Guid nodeId, out string containerId)
    {
        var owner = Guid.Empty;
        var current = graph;
        foreach (var scopeId in path)
        {
            var container = current.Nodes.FirstOrDefault(n => n.Id == scopeId);
            if (container?.ChildGraph is null)
            {
                containerId = string.Empty;
                return null;
            }
            if (container.Kind == GraphNodeKind.SpawnableGroup)
            {
                owner = container.Id;
            }
            else if (container.Kind != GraphNodeKind.GraphGroup)
            {
                containerId = string.Empty;
                return null;
            }
            current = container.ChildGraph;
        }
        containerId = owner.ToString("D");
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
            .Where(p => definition.GetInputPortCategory(p.Id) != GraphPortCategory.Signal)
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

        if (type.StartsWith("enum:", StringComparison.Ordinal))
        {
            if (!GraphEnumBranchSchema.TryGetValueDefinition(node.Kind, out _) ||
                !GraphEnumDefinition.TryGetTypeId(type, out var enumId) ||
                !GraphEnumDefinition.All.TryGetValue(enumId, out var enumeration) ||
                value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var enumValue) ||
                !enumeration.Values.Any(item => item.Value == enumValue))
            {
                return false;
            }
            isInt32 = true;
            intValues = [enumValue];
            return true;
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
            case "double":
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
                if (list.Count != (type switch { "vector2" => 2, "vector3" => 3, _ => 4 })) return false;
                floatValues = [.. list];
                return true;
            default:
                return false;
        }
    }
}
