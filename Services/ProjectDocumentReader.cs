using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Project;
using LiberaFx.Models.Resources;

namespace LiberaFx.Services;

public static class ProjectDocumentReader
{
    public const int CurrentFormatVersion = 13;
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<LoadedLiberaFxProject> LoadAsync(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var document = await JsonSerializer.DeserializeAsync<LiberaFxProjectDocument>(stream, Options)
            ?? throw new InvalidDataException("The project file is empty.");
        if (document.FormatVersion < 1 || document.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException($"Project format version {document.FormatVersion} is not supported.");
        if (document.Nodes is null || document.Connections is null)
            throw new InvalidDataException("The project file is missing its graph data.");

        var nodes = new List<GraphNodeSnapshot>(document.Nodes.Count);
        foreach (var node in document.Nodes)
        {
            if (node is null || !GraphNodeDefinition.TryFind(node.Kind, out var definition))
                throw new InvalidDataException("The project contains an unknown node type.");
            nodes.Add(new GraphNodeSnapshot(node.Id, definition!.Kind, node.Name, node.X, node.Y,
                definition.PortType, definition.PortCategory, node.Curve, node.ChildGraph, node.Parameters,
                node.GroupAsset, node.IsPolymorphic, node.GroupOverrides) { SignalKeys = node.SignalKeys });
        }
        var connections = document.Connections.Select(connection => connection is null
            ? throw new InvalidDataException("The project contains an empty connection.")
            : new GraphConnectionSnapshot(connection.SourceNodeId, connection.TargetNodeId,
                connection.TargetPortId, connection.SourcePortId)).ToArray();
        var graph = new GraphSnapshot(nodes, connections)
        {
            AutoDestroyWhenIdle = document.AutoDestroyWhenIdle,
            ResourceIdentities = document.ResourceIdentities
        };
        if (document.ResourceIdentities is { } resourceIdentities)
            ResourceIdentityStore.Validate(resourceIdentities);
        ValidateGraph(graph);
        if (document.FormatVersion >= 2 && document.History is null)
            throw new InvalidDataException("The project file is missing its history timeline.");
        if (document.History is { } history) ValidateHistory(history, graph);
        if (document.FormatVersion < 12 &&
            (SignalTimeline.ContainsKeys(graph) ||
             document.History?.Entries?.Any(entry => SignalTimeline.ContainsKeys(entry.State)) == true))
            throw new InvalidDataException("Signal timeline keys require project format 12 or later.");
        if (document.FormatVersion < 10 &&
            (GroupExposedItems.ContainsMetadata(graph) ||
             document.History?.Entries?.Any(entry => GroupExposedItems.ContainsMetadata(entry.State)) == true))
            throw new InvalidDataException("This project uses group exposed items with an older format version.");
        return new LoadedLiberaFxProject(graph, document.History);
    }

    private static void ValidateHistory(UndoRedoHistorySnapshot history, GraphSnapshot graph)
    {
        if (history.Entries is null)
            throw new InvalidDataException("The project history is missing its entries.");
        foreach (var entry in history.Entries)
        {
            if (entry is null || entry.State is null)
                throw new InvalidDataException("The project history contains an empty state.");
            ValidateGraph(entry.State);
        }
        var current = history.Entries.FirstOrDefault(entry => entry.Id == history.CurrentNodeId);
        if (current is null || !GraphSnapshot.ContentEquals(current.State, graph))
            throw new InvalidDataException("The project graph does not match its saved history position.");
    }

    private static void ValidateGraph(GraphSnapshot snapshot)
    {
        try { GraphValidator.ValidateDocument(snapshot); }
        catch (ArgumentNullException ex)
        {
            throw new InvalidDataException("The project contains an incomplete graph snapshot.", ex);
        }
        if (snapshot.ResourceIdentities is { } resourceIdentities)
            ResourceIdentityStore.Validate(resourceIdentities);
    }
}
