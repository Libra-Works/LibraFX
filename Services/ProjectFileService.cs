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

    private const int CurrentFormatVersion = ProjectDocumentReader.CurrentFormatVersion;

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
        GraphValidator.ValidateDocument(snapshot);
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
                    node.Parameters,
                    node.GroupAsset,
                    node.IsPolymorphic,
                    node.GroupOverrides) { SignalKeys = node.SignalKeys })
                .ToList(),
            Connections = snapshot.Connections
                .Select(connection => new LiberaFxProjectConnection(
                    connection.SourceNodeId,
                    connection.TargetNodeId,
                    connection.TargetPortId,
                    connection.SourcePortId))
                .ToList(),
            AutoDestroyWhenIdle = snapshot.AutoDestroyWhenIdle,
            History = history
        };

        await JsonSerializer.SerializeAsync(stream, document, SerializerOptions);
        return new SavedLiberaFxProject(snapshot, history);
    }

    public Task<LoadedLiberaFxProject> LoadAsync(Stream stream) => ProjectDocumentReader.LoadAsync(stream);
}
