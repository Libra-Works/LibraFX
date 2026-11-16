using System.Text.Json;

namespace LiberaFx.Models.Graph;

public sealed record GroupExposedTarget(Guid NodeId, string? ParameterId);

public sealed record GroupExposedItem(
    Guid Id,
    string Name,
    string Description,
    string Category,
    int Order,
    string Editor,
    IReadOnlyList<GroupExposedTarget> Targets,
    double? Minimum = null,
    double? Maximum = null,
    double? Step = null,
    string? Presentation = null);

public sealed record GroupInstanceComponent(JsonElement? Value = null, FCurveSnapshot? Curve = null);
public sealed record GroupInstanceValue(Guid ItemId, IReadOnlyList<GroupInstanceComponent> Components);

public static class GroupExposedItems
{
    public static bool SupportsParameter(GraphNodeDefinition definition, NodeParameterDefinition parameter) =>
        parameter.EnabledWhen is null &&
        !(parameter.Id == "materialSchema" && RenderNodeSchema.IsRenderNode(definition)) &&
        (parameter.Type is "float" or "int" or "bool" or "string" or "vector2" or "vector3" or "vector4" or "color" ||
         GraphEnumDefinition.TryGetTypeId(parameter.Type, out _));

    public static bool ContainsMetadata(GraphSnapshot graph) =>
        graph.ExposedItems is { Count: > 0 } ||
        graph.Nodes.Any(node => node.GroupOverrides is { Count: > 0 } ||
            node.ChildGraph is { } child && ContainsMetadata(child));

    public static void ValidateOverrides(IReadOnlyList<GroupInstanceValue>? values)
    {
        if (values is null) return;
        var ids = new HashSet<Guid>();
        if (values.Count > 256) throw new InvalidDataException("A group has too many instance values.");
        foreach (var value in values)
        {
            if (value is null || value.ItemId == Guid.Empty || !ids.Add(value.ItemId) ||
                value.Components is null || value.Components.Count is < 1 or > 3)
                throw new InvalidDataException("The group contains an invalid instance value.");
            foreach (var component in value.Components)
            {
                if (component is null || (component.Value is null) == (component.Curve is null) ||
                    component.Value is { } json &&
                        (json.ValueKind == JsonValueKind.Undefined || json.GetRawText().Length > 16384))
                    throw new InvalidDataException("The group contains an invalid instance value.");
                if (component.Curve is { } curve) FCurve.Validate(curve);
            }
        }
    }

    public static bool OverridesEqual(IReadOnlyList<GroupInstanceValue>? left, IReadOnlyList<GroupInstanceValue>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        return left.Zip(right).All(pair => pair.First.ItemId == pair.Second.ItemId &&
            pair.First.Components.Count == pair.Second.Components.Count &&
            pair.First.Components.Zip(pair.Second.Components).All(component =>
                (component.First.Value is null && component.Second.Value is null ||
                 component.First.Value is { } a && component.Second.Value is { } b && JsonElement.DeepEquals(a, b)) &&
                (component.First.Curve is null && component.Second.Curve is null ||
                 component.First.Curve is { } c && component.Second.Curve is { } d && c.Keys.SequenceEqual(d.Keys))));
    }

    public static GraphSnapshot Materialize(GraphSnapshot graph)
    {
        var nodes = graph.Nodes.Select(node =>
        {
            if (node.ChildGraph is not { } child) return node;
            Validate(child.ExposedItems);
            ValidateOverrides(node.GroupOverrides);
            foreach (var value in node.GroupOverrides ?? [])
            {
                var item = child.ExposedItems?.SingleOrDefault(candidate => candidate.Id == value.ItemId)
                    ?? throw new InvalidDataException($"Group '{node.Name}' has an instance value for a missing exposed item {value.ItemId}.");
                if (item.Targets.Count != value.Components.Count)
                    throw new InvalidDataException($"Group exposed item '{item.Name}' has incompatible instance values.");
                var children = child.Nodes.ToArray();
                for (var index = 0; index < item.Targets.Count; index++)
                {
                    var target = item.Targets[index];
                    var component = value.Components[index];
                    var childIndex = Array.FindIndex(children, candidate => candidate.Id == target.NodeId);
                    if (childIndex < 0)
                        throw new InvalidDataException($"Group exposed item '{item.Name}' references a missing node.");
                    var internalNode = children[childIndex];
                    if (item.Editor == "parameter" && component.Value is { } json)
                    {
                        var baseDefinition = GraphNodeDefinition.All.Single(definition => definition.Kind == internalNode.Kind);
                        var definition = GameIoSchema.Effective(RenderNodeSchema.Effective(baseDefinition, internalNode.Parameters),
                            internalNode.Parameters).Parameters.FirstOrDefault(parameter => parameter.Id == target.ParameterId)
                            ?? throw new InvalidDataException($"Group exposed item '{item.Name}' references a missing parameter.");
                        if (!SupportsParameter(baseDefinition, definition) || !NodeParameter.IsValidValue(definition.Type, json) ||
                            definition.Type is "float" or "int" &&
                            (item.Minimum is { } minimum && json.GetDouble() < minimum ||
                             item.Maximum is { } maximum && json.GetDouble() > maximum))
                            throw new InvalidDataException($"Group exposed item '{item.Name}' has an invalid value.");
                        var parameters = internalNode.Parameters?.ToList() ?? [];
                        var parameterIndex = parameters.FindIndex(parameter => parameter.Id == definition.Id);
                        var updated = new NodeParameterSnapshot(definition.Id, definition.Type, json.Clone());
                        if (parameterIndex < 0) parameters.Add(updated);
                        else parameters[parameterIndex] = updated;
                        RenderNodeSchema.ValidateNodeParameters(baseDefinition, parameters);
                        GameIoSchema.ValidateNodeParameters(baseDefinition, parameters);
                        children[childIndex] = internalNode with { Parameters = parameters.AsReadOnly() };
                    }
                    else if (item.Editor is "curve" or "vector3Curve" &&
                             component.Curve is { } curve && internalNode.Kind == GraphNodeKind.FloatFCurve)
                        children[childIndex] = internalNode with { Curve = curve };
                    else throw new InvalidDataException($"Group exposed item '{item.Name}' has an incompatible target.");
                }
                child = child with { Nodes = children };
            }
            return node with { ChildGraph = Materialize(child), GroupOverrides = null };
        }).ToArray();
        return graph with { Nodes = nodes };
    }

    public static void Validate(IReadOnlyList<GroupExposedItem>? items)
    {
        if (items is null) return;
        var ids = new HashSet<Guid>();
        if (items.Count > 256)
            throw new InvalidDataException("A group has too many exposed items.");
        foreach (var item in items)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) ||
                string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 200 ||
                item.Description is null || item.Description.Length > 4096 ||
                item.Category is null || item.Category.Length > 200 ||
                item.Editor is not ("parameter" or "curve" or "vector3Curve") ||
                item.Targets is null || item.Targets.Count != (item.Editor == "vector3Curve" ? 3 : 1) ||
                item.Targets.Any(target => target is null || target.NodeId == Guid.Empty ||
                    (item.Editor == "parameter"
                        ? string.IsNullOrWhiteSpace(target.ParameterId) || target.ParameterId.Length > 128
                        : target.ParameterId is not null)) ||
                item.Targets.Distinct().Count() != item.Targets.Count ||
                item.Minimum is { } min && !double.IsFinite(min) ||
                item.Maximum is { } max && !double.IsFinite(max) ||
                item.Step is { } step && (!double.IsFinite(step) || step <= 0) ||
                item.Minimum is { } minimum && item.Maximum is { } maximum && minimum > maximum)
                throw new InvalidDataException("The group contains an invalid exposed item.");
            if (item.Presentation is not (null or "text" or "slider") ||
                item.Presentation == "slider" &&
                (item.Editor != "parameter" || item.Minimum is null || item.Maximum is null ||
                 item.Minimum >= item.Maximum))
                throw new InvalidDataException("The group contains an invalid exposed item presentation.");
        }
    }

    public static IReadOnlyList<GroupExposedItem>? Remap(
        IReadOnlyList<GroupExposedItem>? items, IReadOnlyDictionary<Guid, Guid> ids) =>
        items?.Select(item => item with
        {
            Targets = item.Targets.Select(target => target with
            {
                NodeId = ids.TryGetValue(target.NodeId, out var mapped) ? mapped : target.NodeId
            }).ToArray()
        }).ToArray();

    public static bool Equal(IReadOnlyList<GroupExposedItem>? left, IReadOnlyList<GroupExposedItem>? right,
        IReadOnlyDictionary<Guid, Guid>? ids = null)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        return left.Zip(right).All(pair =>
            pair.First with { Targets = Array.Empty<GroupExposedTarget>() } ==
                pair.Second with { Targets = Array.Empty<GroupExposedTarget>() } &&
            pair.First.Targets.Count == pair.Second.Targets.Count &&
            pair.First.Targets.Zip(pair.Second.Targets).All(target =>
                (ids is not null && ids.TryGetValue(target.First.NodeId, out var mapped)
                    ? mapped : target.First.NodeId) == target.Second.NodeId &&
                target.First.ParameterId == target.Second.ParameterId));
    }
}
