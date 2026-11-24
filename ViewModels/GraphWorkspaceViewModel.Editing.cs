using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    public void MoveNode(GraphNodeViewModel node, double x, double y)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("The node is not in this graph.");
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentException("A finite position is required.");
        }

        if (node.X == x && node.Y == y)
        {
            return;
        }

        if (node.IsComment)
        {
            var dx = x - node.X;
            var dy = y - node.Y;
            MoveNodes(GetMoveTargets([node]).Select(item => (item, item.X + dx, item.Y + dy)));
            return;
        }
        ExecuteEdit(new MoveGraphNodeCommand(node, node.X, node.Y, x, y));
    }

    public IReadOnlyList<GraphNodeViewModel> GetMoveTargets(IEnumerable<GraphNodeViewModel> selection)
    {
        var selected = selection.Where(Nodes.Contains).Distinct().ToArray();
        var result = selected.ToHashSet();
        foreach (var comment in selected.Where(node => node.IsComment))
        {
            var right = comment.X + comment.CommentWidth;
            var bottom = comment.Y + comment.CommentHeight;
            foreach (var node in Nodes)
            {
                if (ReferenceEquals(node, comment))
                {
                    continue;
                }

                var height = node.IsComment ? node.CommentHeight : node.RenderedHeight;
                if (node.X >= comment.X && node.Y >= comment.Y &&
                    node.X + node.RenderedWidth <= right && node.Y + height <= bottom)
                {
                    result.Add(node);
                }
            }
        }
        return Nodes.Where(result.Contains).ToArray();
    }

    public void MoveNodes(IEnumerable<(GraphNodeViewModel Node, double X, double Y)> positions)
    {
        var commands = positions
            .Where(item => Nodes.Contains(item.Node) &&
                           double.IsFinite(item.X) && double.IsFinite(item.Y) &&
                           (item.Node.X != item.X || item.Node.Y != item.Y))
            .Select(item => new MoveGraphNodeCommand(item.Node, item.Node.X, item.Node.Y, item.X, item.Y))
            .ToArray();
        if (commands.Length > 0)
        {
            ExecuteEdit(new CompositeUndoableCommand(LocalizationService.Shared.Get("Messages.History.MoveNodes"), commands));
        }
    }

    public void RenameNode(GraphNodeViewModel node, string name)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("The node is not in this graph.");
        }

        if (node.Name == name)
        {
            return;
        }

        ExecuteEdit(new RenameGraphNodeCommand(node, node.Name, name));
    }

    public void SetCurve(GraphNodeViewModel node, FCurveSnapshot curve, string description)
    {
        if (!Nodes.Contains(node) || node.Definition.Kind != GraphNodeKind.FloatFCurve || node.Curve is null)
        {
            throw new InvalidOperationException("Select an F-Curve node in this graph.");
        }

        FCurve.Validate(curve);
        if (node.Curve.Keys.SequenceEqual(curve.Keys))
        {
            return;
        }

        ExecuteEdit(new SetFCurveCommand(node, node.Curve, curve, description));
    }

    public void SetSignalKeys(GraphNodeViewModel node, IReadOnlyList<SignalTimelineKey> keys, string description)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("Select a Signal entry in this graph.");
        }

        SignalTimeline.Validate(node.BaseDefinition.Kind, keys);
        if (!SignalTimeline.Equal(node.SignalKeys, keys))
        {
            ExecuteEdit(new SetSignalKeysCommand(node, node.SignalKeys, keys.ToArray(), description));
        }
    }

    public void SetParameter(GraphNodeViewModel node, string id, JsonElement value)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("The node is not in this graph.");
        }

        if (RenderNodeSchema.IsRenderNode(node.Definition) && id == "materialSchema")
        {
            throw new InvalidOperationException("Assign an engine asset to update the material schema.");
        }
        if (ProceduralResourceSchema.IsMetadata(node.Definition.Kind) && id == "proceduralState")
        {
            var recipe = ProceduralResourceSchema.Read(node.BaseDefinition,
                [new NodeParameterSnapshot("proceduralState", "string", value)]);
            ApplyProceduralRecipe(node, recipe);
            return;
        }

        if (RenderNodeSchema.IsRenderNode(node.Definition) && RenderNodeSchema.TryGetMaterialSlot(id, out _))
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Resources.MaterialBrowseRequired"));
        }

        var definition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == id)
            ?? throw new InvalidOperationException("The node parameter no longer exists.");
        if (!NodeParameter.IsValidValue(definition.Type, value))
        {
            throw new ArgumentException("The value is invalid for this parameter.", nameof(value));
        }

        if ((RenderNodeSchema.IsRenderNode(node.Definition) || RenderNodeSchema.IsTextureOnlyDraw(node.Definition)) &&
            id == "assetPath" &&
            value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } assetPath &&
            !(node.Definition.TypeName == "CpuParticleSpriteDraw" && assetPath == "builtin:white") &&
            !RenderNodeSchema.IsSafeAssetPath(assetPath))
        {
            throw new ArgumentException("The engine asset path is invalid.", nameof(value));
        }

        if (RenderNodeSchema.IsRenderNode(node.Definition) &&
            RenderNodeSchema.Read(GraphNodeDefinition.All.Single(item => item.Kind == node.Definition.Kind), node.Parameters)
                .FirstOrDefault(parameter => parameter.Id == id && parameter.Kind == "texture") is not null &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } texturePath &&
            !RenderNodeSchema.IsSafeAssetPath(texturePath))
        {
            throw new ArgumentException("The texture parameter must use a safe engine asset path.", nameof(value));
        }

        if (NodeParameter.ValuesEqual(node.GetParameterValue(definition), value))
        {
            return;
        }

        var parameters = node.Parameters?.ToList() ?? [];
        var index = parameters.FindIndex(parameter => parameter.Id == id);
        var updated = new NodeParameterSnapshot(id, definition.Type, value.Clone());
        if (index < 0)
        {
            parameters.Add(updated);
        }
        else
        {
            parameters[index] = updated;
        }

        var commands = new List<IUndoableCommand>();
        if (node.IsSequence)
        {
            var count = SequenceSchema.OutputCount(parameters);
            var valid = Enumerable.Range(0, count).Select(SequenceSchema.OutputId).ToHashSet(StringComparer.Ordinal);
            commands.AddRange(Connections.Where(connection => connection.Source == node &&
                    !valid.Contains(connection.SourcePortId ?? string.Empty)).Select(connection =>
                (IUndoableCommand)new RemoveGraphConnectionCommand(this, connection)));
        }
        else if (node.BaseDefinition.DynamicPins is { Count: > 0 } dynamicPins &&
                 dynamicPins.Any(group => group.ParameterId == id))
        {
            var next = DynamicPinSchema.Effective(node.BaseDefinition, parameters);
            var validInputs = next.InputPorts?.Select(port => port.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var validOutputs = next.OutputPorts?.Select(port => port.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            commands.AddRange(Connections.Where(connection =>
                    connection.Target == node && !validInputs.Contains(connection.TargetPortId ?? string.Empty) ||
                    connection.Source == node && !validOutputs.Contains(connection.SourcePortId ?? string.Empty))
                .Select(connection => (IUndoableCommand)new RemoveGraphConnectionCommand(this, connection)));
        }
        commands.Add(new SetNodeParametersCommand(node, node.Parameters, parameters.AsReadOnly(), definition.Name));
        ExecuteEdit(commands.Count == 1 ? commands[0] :
            new CompositeUndoableCommand(LocalizationService.Shared.Get("Editor.Sequence.EditOutputs"), commands));
    }

    public void SetNodeLocked(GraphNodeViewModel node, bool locked)
    {
        if (!Nodes.Contains(node) || IsReadOnly || node.IsLocked == locked) return;
        UpdateBoolEditorFlag(node, GraphNodeViewModel.NodeLockedId, locked,
            LocalizationService.Shared.Get(locked ? "Panels.LockNodeParameters" : "Panels.UnlockNodeParameters"));
    }

    public void SetParameterLocked(GraphNodeViewModel node, string parameterId, bool locked)
    {
        if (!Nodes.Contains(node) || IsReadOnly || node.IsLocked || node.IsParameterLocked(parameterId) == locked) return;
        UpdateBoolEditorFlag(node, GraphNodeViewModel.ParameterLockId(parameterId), locked,
            LocalizationService.Shared.Get(locked ? "Panels.LockParameter" : "Panels.UnlockParameter", parameterId));
    }

    private void UpdateBoolEditorFlag(GraphNodeViewModel node, string id, bool enabled, string description)
    {
        var values = node.Parameters?.Where(value => value.Id != id).ToList() ?? [];
        if (enabled)
            values.Add(new(id, "bool", JsonSerializer.SerializeToElement(true)));
        ExecuteEdit(new SetNodeParametersCommand(node, node.Parameters, values.AsReadOnly(), description));
    }

    public void AssignRenderAsset(GraphNodeViewModel node, EngineAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (Nodes.Contains(node) && RenderNodeSchema.IsTextureOnlyDraw(node.Definition))
        {
            RenderNodeAuthoring.ValidateAssetClass(asset, "texture");
            SetParameter(node, "assetPath", JsonSerializer.SerializeToElement(asset.Path));
            return;
        }
        if (!Nodes.Contains(node) || !RenderNodeSchema.IsRenderNode(node.Definition))
        {
            throw new InvalidOperationException("Select a model or sprite render node.");
        }

        RenderNodeAuthoring.ValidateAssetClass(asset, node.Definition.TypeName == "ModelDraw" ? "model" : "texture");
        var assetPath = RenderNodeSchema.IsSafeAssetPath(asset.Path)
            ? asset.Path
            : throw new ArgumentException("The engine asset path is invalid.", nameof(asset));
        ApplyRenderSchema(node, RenderNodeSchema.Encode(asset), assetPath, "Assign engine asset");
    }

    public void AssignRenderMaterial(GraphNodeViewModel node, EngineAsset asset, int slot)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!Nodes.Contains(node) || !RenderNodeSchema.IsRenderNode(node.Definition))
        {
            throw new InvalidOperationException("Select a model or sprite render node.");
        }

        RenderNodeAuthoring.ValidateAssetClass(asset, "material");
        if (!RenderNodeSchema.ReadMaterials(node.Definition, node.Parameters).Any(material => material.Slot == slot))
        {
            throw new ArgumentException("The selected material slot no longer exists.", nameof(slot));
        }

        if (asset.Materials is not { Count: 1 } || asset.Materials[0].Path != asset.Path)
        {
            throw new InvalidDataException("Load the material's parameter metadata before assigning it.");
        }

        var oldIds = RenderNodeSchema.Read(node.Definition, node.Parameters)
            .Where(parameter => parameter.Slot == slot).Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        var identities = CreateSnapshot().ResourceIdentities;
        if (identities is not null &&
            (identities.Uses.Any(use => use.NodeId == node.Id &&
                (use.Location == $"material:{slot}" ||
                 use.Location.StartsWith("texture:", StringComparison.Ordinal) && oldIds.Contains(use.Location["texture:".Length..])) &&
                ResourceIdentityScope.Equal(use.InstancePath, _graphPath)) ||
             identities.ParameterUses.Any(use => use.NodeId == node.Id && oldIds.Contains(use.PinId) &&
                ResourceIdentityScope.Equal(use.InstancePath, _graphPath))))
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Resources.MaterialIdentityManaged"));
        }

        var materials = RenderNodeSchema.ReadEngineMaterials(node.Definition, node.Parameters)
            .Where(material => material.Slot != slot).Append(
                new EngineMaterial(slot, asset.Path, asset.Name, asset.Materials[0].Parameters))
            .OrderBy(material => material.Slot).ToArray();
        ApplyRenderSchema(node, RenderNodeSchema.EncodeMaterials(materials), null,
            LocalizationService.Shared.Get("Resources.AssignMaterialHistory", asset.Name, slot));
    }

    private void ApplyRenderSchema(GraphNodeViewModel node, string schema, string? assetPath, string description)
    {
        var schemaDefinition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == "materialSchema")
            ?? throw new InvalidOperationException("The render node does not define a material schema.");

        var values = node.Parameters?.ToList() ?? [];
        var oldSchema = node.Parameters?.FirstOrDefault(parameter =>
            parameter.Id == schemaDefinition.Id && parameter.Type == schemaDefinition.Type)?.Value
            ?? schemaDefinition.DefaultValue;
        var oldIds = RenderNodeSchema.Describe(oldSchema.ValueKind == JsonValueKind.String
            ? oldSchema.GetString() : "[]").Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        var newIds = RenderNodeSchema.Describe(schema).Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        values.RemoveAll(parameter => RenderNodeSchema.TryGetMaterialSlot(parameter.Id, out _) ||
            oldIds.Contains(parameter.Id) && !newIds.Contains(parameter.Id));
        if (assetPath is not null)
        {
            SetValue(values, "assetPath", "string", JsonSerializer.SerializeToElement(assetPath));
        }

        SetValue(values, schemaDefinition.Id, schemaDefinition.Type, JsonSerializer.SerializeToElement(schema));

        var updated = values.AsReadOnly();
        var newDefinition = RenderNodeSchema.Effective(
            GraphNodeDefinition.All.Single(definition => definition.Kind == node.Definition.Kind), updated);
        var commands = new List<IUndoableCommand>();
        if (!ParameterSnapshotsEqual(node.Parameters, updated))
        {
            commands.Add(new SetNodeParametersCommand(node, node.Parameters, updated, "engine asset"));
        }

        foreach (var connection in Connections.Where(connection => connection.Target == node).ToArray())
        {
            var sourceCategory = connection.Source.Definition.GetOutputPortCategory(connection.SourcePortId);
            var targetCategory = newDefinition.GetInputPortCategory(connection.TargetPortId);
            var validPort = newDefinition.InputPorts?.Any(port => port.Id == connection.TargetPortId) == true;
            if (!validPort || sourceCategory != targetCategory)
            {
                commands.Add(new RemoveGraphConnectionCommand(this, connection));
            }
        }
        if (commands.Count != 0)
        {
            ExecuteEdit(new CompositeUndoableCommand(description, commands));
        }

        static void SetValue(List<NodeParameterSnapshot> parameters, string id, string type, JsonElement value)
        {
            var index = parameters.FindIndex(parameter => parameter.Id == id);
            var item = new NodeParameterSnapshot(id, type, value.Clone());
            if (index < 0)
            {
                parameters.Add(item);
            }
            else
            {
                parameters[index] = item;
            }
        }

        static bool ParameterSnapshotsEqual(
            IReadOnlyList<NodeParameterSnapshot>? left,
            IReadOnlyList<NodeParameterSnapshot> right) =>
            left is not null && left.Count == right.Count &&
            left.Zip(right).All(pair => pair.First.Id == pair.Second.Id && pair.First.Type == pair.Second.Type &&
                NodeParameter.ValuesEqual(pair.First.Value, pair.Second.Value)) ||
            left is null && right.Count == 0;
    }

    internal void SetCommentSize(GraphNodeViewModel node, double width, double height)
    {
        if (!Nodes.Contains(node) || !node.IsComment || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new InvalidOperationException("The comment size is invalid.");
        }

        width = Math.Clamp(width, 180, GraphNodeViewModel.MaxCommentSize);
        height = Math.Clamp(height, 80, GraphNodeViewModel.MaxCommentSize);
        var values = node.Parameters?.ToList() ?? [];
        foreach (var (id, value) in new[] { ("width", width), ("height", height) })
        {
            var item = new NodeParameterSnapshot(id, "float", JsonSerializer.SerializeToElement(value));
            var index = values.FindIndex(existing => existing.Id == id);
            if (index < 0)
            {
                values.Add(item);
            }
            else
            {
                values[index] = item;
            }
        }
        var oldWidth = node.GetParameterValue(node.Definition.Parameters.Single(item => item.Id == "width")).GetDouble();
        var oldHeight = node.GetParameterValue(node.Definition.Parameters.Single(item => item.Id == "height")).GetDouble();
        if (oldWidth == width && oldHeight == height)
        {
            return;
        }

        ExecuteEdit(new SetNodeParametersCommand(node, node.Parameters, values.AsReadOnly(), LocalizationService.Shared.Get("Messages.History.CommentSize")));
    }

    public GraphNodeViewModel CreateNode(GraphNodeKind kind, double? x = null, double? y = null, bool isPolymorphic = false)
        => CreateNodeCore(kind, x, y, isPolymorphic);

    public GraphNodeViewModel CreateCommentAroundSelection(double fallbackX, double fallbackY)
    {
        var selection = SelectedNodes.ToArray();
        if (selection.Length == 0)
        {
            return CreateNode(GraphNodeKind.Comment, fallbackX, fallbackY);
        }

        const double padding = 32;
        var left = selection.Min(node => node.X) - padding;
        var top = selection.Min(node => node.Y) - padding - 32;
        var right = selection.Max(node => node.X + node.RenderedWidth) + padding;
        var bottom = selection.Max(node => node.Y + (node.IsComment ? node.CommentHeight : node.RenderedHeight)) + padding;
        return CreateNodeCore(GraphNodeKind.Comment, left, top, false,
        [
            new("width", "float", JsonSerializer.SerializeToElement(Math.Clamp(right - left, 180, GraphNodeViewModel.MaxCommentSize))),
            new("height", "float", JsonSerializer.SerializeToElement(Math.Clamp(bottom - top, 100, GraphNodeViewModel.MaxCommentSize)))
        ]);
    }

    private GraphNodeViewModel CreateNodeCore(GraphNodeKind kind, double? x, double? y, bool isPolymorphic,
        IReadOnlyList<NodeParameterSnapshot>? parameters = null)
    {
        if ((kind == GraphNodeKind.GraphGroup || kind == GraphNodeKind.SpawnableGroup ||
             CpuParticleSchema.HasChild(kind)) && _graphPath.Count >= MaxGraphDepth)
        {
            throw new InvalidOperationException("The graph hierarchy has reached its maximum depth.");
        }

        ValidateSpawnPlacement([kind]);
        var definition = AvailableNodeDefinitions.FirstOrDefault(item => item.Kind == kind)
            ?? throw new ArgumentException(LocalizationService.Shared.Get("Editor.NodeNotInProject"));
        var family = GraphPolymorphism.TryFamily(kind, out var selectedFamily) ? selectedFamily : null;
        if (isPolymorphic && family is null)
        {
            throw new ArgumentException("The node has no polymorphic family.", nameof(kind));
        }

        var index = _nextNodeIndex++;
        var nodeId = Guid.NewGuid();
        var defaultX = Nodes.Count == 0
            ? 80
            : Nodes.Select(existing => GraphFlowLayout.Bounds(existing).Right + 120).Max();
        var node = new GraphNodeViewModel(
            this,
            definition,
            isPolymorphic ? LocalizationService.Shared.GetEnglishOrDefault(family?.DisplayNameKey ?? definition.DisplayNameKey, definition.Name) : definition.Name,
            x ?? defaultX,
            y ?? 80,
            id: nodeId,
            parameters: GpuParticleEditorService.IsGpu(kind) ? GpuParticleEditorService.InitialParameters(nodeId) : parameters,
            isPolymorphic: isPolymorphic);
        ExecuteEdit(new AddGraphNodeCommand(this, node, Nodes.Count));
        return node;
    }
}
