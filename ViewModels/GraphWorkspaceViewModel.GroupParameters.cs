using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    public bool CanPublishGroupParameter(GraphNodeViewModel node, string parameterId)
    {
        if (_graphPath.Count == 0 || !Nodes.Contains(node))
        {
            return false;
        }

        var definition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId);
        if (definition is null || definition.Hidden || !GroupExposedItems.SupportsParameter(node.Definition, definition))
        {
            return false;
        }

        return GetCurrentGraph().ExposedItems?.Any(item => item.Editor == "parameter" &&
            item.Targets[0].NodeId == node.Id && item.Targets[0].ParameterId == parameterId) != true;
    }

    public Guid PublishGroupParameter(GraphNodeViewModel node, string parameterId)
    {
        if (!CanPublishGroupParameter(node, parameterId))
        {
            throw new InvalidOperationException("The selected parameter cannot be exposed here.");
        }

        var definition = node.Definition.Parameters.Single(parameter => parameter.Id == parameterId);
        var item = new GroupExposedItem(Guid.NewGuid(), $"{node.Name}: {definition.Name}", "", "",
            GetCurrentGraph().ExposedItems?.Count ?? 0, "parameter", [new(node.Id, parameterId)]);
        return PublishGroupItem(item);
    }

    public bool CanPublishGroupCurves() => CanPublishGroupCurves(_selectedNodes);

    public bool CanPublishGroupCurves(IReadOnlyList<GraphNodeViewModel> nodes)
    {
        if (_graphPath.Count == 0 || nodes.Count is < 1 or > 4 || nodes.Distinct().Count() != nodes.Count ||
            nodes.Any(node => !Nodes.Contains(node) || node.Curve is null))
        {
            return false;
        }

        var existing = (GetCurrentGraph().ExposedItems ?? []).Where(item => GroupExposedItems.CurveChannels(item.Editor).Length != 0)
            .SelectMany(item => item.Targets).Select(target => target.NodeId).ToHashSet();
        return nodes.All(node => !existing.Contains(node.Id));
    }

    public Guid PublishGroupCurves() => PublishGroupCurves(_selectedNodes);

    public Guid PublishGroupCurves(IReadOnlyList<GraphNodeViewModel> nodes)
    {
        if (!CanPublishGroupCurves(nodes))
        {
            throw new InvalidOperationException("Select one to four unexposed F-Curve nodes inside a group.");
        }

        var item = new GroupExposedItem(Guid.NewGuid(),
            nodes.Count == 1 ? nodes[0].Name : $"Vector{nodes.Count} Curve", "", "",
            GetCurrentGraph().ExposedItems?.Count ?? 0,
            nodes.Count == 1 ? "curve" : $"vector{nodes.Count}Curve",
            nodes.Select(node => new GroupExposedTarget(node.Id, null)).ToArray());
        return PublishGroupItem(item);
    }

    private Guid PublishGroupItem(GroupExposedItem item)
    {
        var before = CreateSnapshot();
        var parent = before;
        foreach (var id in _graphPath)
        {
            var owner = parent.Nodes.Single(candidate => candidate.Id == id);
            if (owner.GroupAsset is { } reference)
            {
                var asset = GroupAssets?.Load(reference.Id)
                    ?? throw new InvalidOperationException("The group library is unavailable.");
                if (asset.Locked)
                {
                    throw new InvalidOperationException("The group asset is locked.");
                }
            }
            parent = owner.ChildGraph!;
        }
        var items = (parent.ExposedItems ?? []).Append(item).ToArray();
        GroupExposedItems.Validate(items);
        var after = ReplaceGraph(before, _graphPath, 0, parent with { ExposedItems = items });
        ValidateSnapshot(after);
        ExecuteEdit(new ReplaceGraphSnapshotCommand(this, before, after,
            LocalizationService.Shared.Get("Messages.History.PublishGroupParameter")));
        return item.Id;
    }

    public void UpdateGroupExposedItem(GraphNodeViewModel group, GroupExposedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        GroupExposedItems.Validate([item]);
        if (!Nodes.Contains(group) || group.ChildGraph is null)
        {
            throw new InvalidOperationException("Select a group in this graph.");
        }

        var before = CreateSnapshot();
        var current = before;
        foreach (var id in _graphPath)
        {
            var owner = current.Nodes.Single(candidate => candidate.Id == id);
            EnsureAssetUnlocked(owner.GroupAsset);
            current = owner.ChildGraph!;
        }
        var original = current.Nodes.Single(candidate => candidate.Id == group.Id);
        EnsureAssetUnlocked(original.GroupAsset);
        var child = original.ChildGraph!;
        var items = child.ExposedItems?.ToArray() ?? [];
        var index = Array.FindIndex(items, candidate => candidate.Id == item.Id);
        if (index < 0)
        {
            throw new InvalidOperationException("The exposed item no longer exists.");
        }

        var previous = items[index];
        if (previous.Editor != item.Editor || !previous.Targets.SequenceEqual(item.Targets))
        {
            throw new InvalidOperationException("An exposed item's targets cannot be changed here.");
        }

        if (item.Presentation == "slider" || item.Minimum is not null || item.Maximum is not null || item.Step is not null)
        {
            var target = item.Targets.SingleOrDefault();
            var node = target is null ? null : child.Nodes.FirstOrDefault(candidate => candidate.Id == target.NodeId);
            var definition = node is null ? null : GraphNodeDefinition.All.FirstOrDefault(candidate => candidate.Kind == node.Kind);
            var parameter = definition is null ? null :
                GameIoSchema.Effective(RenderNodeSchema.Effective(definition, node!.Parameters), node.Parameters)
                    .Parameters.FirstOrDefault(candidate => candidate.Id == target!.ParameterId);
            if (item.Editor != "parameter" || parameter?.Type is not ("float" or "int"))
            {
                throw new InvalidOperationException("Ranges and sliders require a numeric parameter.");
            }

            var initial = node!.Parameters?.FirstOrDefault(candidate =>
                candidate.Id == parameter.Id && candidate.Type == parameter.Type)?.Value ?? parameter.DefaultValue;
            if (item.Minimum is { } minimum && initial.GetDouble() < minimum ||
                item.Maximum is { } maximum && initial.GetDouble() > maximum)
            {
                throw new InvalidOperationException("The group default is outside the requested range.");
            }
        }
        items[index] = item;
        GroupExposedItems.Validate(items);
        var changedGroup = original with { ChildGraph = child with { ExposedItems = items } };
        var candidateGraph = current with
        {
            Nodes = current.Nodes.Select(node => node.Id == group.Id ? changedGroup : node).ToArray()
        };
        var materialized = GroupExposedItems.Materialize(candidateGraph);
        GraphValidator.Validate(materialized);
        var after = ReplaceGraph(before, _graphPath, 0, candidateGraph);
        ValidateSnapshot(after);
        if (GraphSnapshot.ContentEquals(before, after))
        {
            return;
        }

        ExecuteEdit(new ReplaceGraphSnapshotCommand(this, before, after,
            LocalizationService.Shared.Get("Messages.History.ConfigureGroupParameter")));
    }

    private void EnsureAssetUnlocked(GroupAssetReference? reference)
    {
        if (reference is null)
        {
            return;
        }

        var asset = GroupAssets?.Load(reference.Id)
            ?? throw new InvalidOperationException("The group library is unavailable.");
        if (asset.Locked)
        {
            throw new InvalidOperationException("The group asset is locked.");
        }
    }

    public void SetGroupInstanceValue(GraphNodeViewModel node, GroupInstanceValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if ((!Nodes.Contains(node) && !ReferenceEquals(ContainingSpawnable, node)) || node.ChildGraph is null)
        {
            throw new InvalidOperationException("Select a group in this graph.");
        }

        GroupExposedItems.ValidateOverrides([value]);
        var values = node.GroupOverrides?.ToList() ?? [];
        var item = new GroupInstanceValue(value.ItemId, value.Components.Select(component =>
            new GroupInstanceComponent(component.Value?.Clone(),
                component.Curve is { } curve ? new FCurveSnapshot(curve.Keys.ToArray()) : null)).ToArray());
        var index = values.FindIndex(existing => existing.ItemId == item.ItemId);
        if (index < 0)
        {
            values.Add(item);
        }
        else
        {
            values[index] = item;
        }

        var result = values.AsReadOnly();
        GroupExposedItems.ValidateOverrides(result);
        if (GroupExposedItems.OverridesEqual(node.GroupOverrides, result))
        {
            return;
        }

        ApplyGroupOverrides(node, result);
    }

    private void ApplyGroupOverrides(GraphNodeViewModel node, IReadOnlyList<GroupInstanceValue>? values)
    {
        var before = CreateSnapshot();
        var path = ReferenceEquals(ContainingSpawnable, node)
            ? _graphPath.Take(_graphPath.Count - 1).ToArray() : _graphPath.ToArray();
        var parent = before;
        foreach (var id in path)
        {
            parent = parent.Nodes.Single(candidate => candidate.Id == id).ChildGraph!;
        }

        var snapshot = parent.Nodes.Single(candidate => candidate.Id == node.Id) with
        {
            GroupOverrides = values
        };
        var materialized = GroupExposedItems.Materialize(new GraphSnapshot([snapshot], []));
        GraphValidator.Validate(materialized);
        if (!ReferenceEquals(ContainingSpawnable, node))
        {
            ExecuteEdit(new SetGroupOverridesCommand(node, node.GroupOverrides, values));
            return;
        }
        var updated = parent with
        {
            Nodes = parent.Nodes.Select(candidate => candidate.Id == node.Id ? snapshot : candidate).ToArray()
        };
        var after = ReplaceGraph(before, path, 0, updated);
        ValidateSnapshot(after);
        ExecuteEdit(new ReplaceGraphSnapshotCommand(this, before, after,
            LocalizationService.Shared.Get("Messages.History.GroupInstanceValue")), containingInstanceValue: true);
    }

    public void ResetGroupInstanceValue(GraphNodeViewModel node, Guid itemId)
    {
        if ((!Nodes.Contains(node) && !ReferenceEquals(ContainingSpawnable, node)) || node.ChildGraph is null)
        {
            throw new InvalidOperationException("Select a group in this graph.");
        }

        if (node.GroupOverrides?.Any(value => value.ItemId == itemId) != true)
        {
            return;
        }

        var remaining = node.GroupOverrides.Where(value => value.ItemId != itemId).ToArray();
        ApplyGroupOverrides(node, remaining.Length == 0 ? null : remaining);
    }
}
