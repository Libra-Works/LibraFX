using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Project;
using LiberaFx.ViewModels;

namespace LiberaFx.Services;

public sealed class ProjectFileService
{
    public const string FileExtension = ".lfxproj";

    private const int CurrentFormatVersion = 5;
    private const int FirstSupportedFormatVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    public async Task<SavedLiberaFxProject> SaveAsync(Stream stream, GraphWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(workspace);

        var snapshot = workspace.CreateSnapshot();
        var history = workspace.History.CreateSnapshot();
        var document = new LiberaFxProjectDocument
        {
            FormatVersion = CurrentFormatVersion,
            Nodes = snapshot.Nodes
                .Select(node => new LiberaFxProjectNode(
                    node.Id,
                    node.Name,
                    node.Kind.ToString(),
                    node.X,
                    node.Y,
                    node.Curve,
                    node.ChildGraph,
                    node.Parameters))
                .ToList(),
            Connections = snapshot.Connections
                .Select(connection => new LiberaFxProjectConnection(
                    connection.SourceNodeId,
                    connection.TargetNodeId))
                .ToList(),
            History = history
        };

        await JsonSerializer.SerializeAsync(stream, document, SerializerOptions);
        return new SavedLiberaFxProject(snapshot, history);
    }

    public async Task<LoadedLiberaFxProject> LoadAsync(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var document = await JsonSerializer.DeserializeAsync<LiberaFxProjectDocument>(
            stream,
            SerializerOptions);

        if (document is null)
        {
            throw new InvalidDataException("The project file is empty.");
        }

        if (document.FormatVersion < FirstSupportedFormatVersion ||
            document.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Project format version {document.FormatVersion} is not supported.");
        }

        if (document.Nodes is null || document.Connections is null)
        {
            throw new InvalidDataException("The project file is missing its graph data.");
        }

        var nodes = new List<GraphNodeSnapshot>(document.Nodes.Count);
        foreach (var node in document.Nodes)
        {
            if (node is null ||
                !GraphNodeDefinition.TryFind(node.Kind, out var definition))
            {
                throw new InvalidDataException("The project contains an unknown node type.");
            }

            nodes.Add(new GraphNodeSnapshot(
                node.Id,
                definition!.Kind,
                node.Name,
                node.X,
                node.Y,
                definition.PortType,
                definition.PortCategory,
                node.Curve,
                node.ChildGraph,
                node.Parameters));
        }

        var connections = document.Connections
            .Select(connection => connection is null
                ? throw new InvalidDataException("The project contains an empty connection.")
                : new GraphConnectionSnapshot(connection.SourceNodeId, connection.TargetNodeId))
            .ToArray();

        var graph = new GraphSnapshot(nodes, connections);
        ValidateGraphSnapshot(graph);

        if (document.FormatVersion >= 2 && document.History is null)
        {
            throw new InvalidDataException("The project file is missing its history timeline.");
        }

        if (document.History is { } history)
        {
            ValidateHistory(history, graph);
        }

        return new LoadedLiberaFxProject(graph, document.History);
    }

    private static void ValidateHistory(UndoRedoHistorySnapshot history, GraphSnapshot graph)
    {
        if (history.Entries is null)
        {
            throw new InvalidDataException("The project history is missing its entries.");
        }

        foreach (var entry in history.Entries)
        {
            if (entry is null || entry.State is null)
            {
                throw new InvalidDataException("The project history contains an empty state.");
            }

            ValidateGraphSnapshot(entry.State);
        }

        var current = history.Entries.FirstOrDefault(entry => entry.Id == history.CurrentNodeId);
        if (current is null || !GraphSnapshot.ContentEquals(current.State, graph))
        {
            throw new InvalidDataException("The project graph does not match its saved history position.");
        }
    }

    private static void ValidateGraphSnapshot(GraphSnapshot snapshot)
    {
        try
        {
            GraphWorkspaceViewModel.ValidateSnapshot(snapshot);
        }
        catch (ArgumentNullException ex)
        {
            throw new InvalidDataException("The project contains an incomplete graph snapshot.", ex);
        }
    }
}
