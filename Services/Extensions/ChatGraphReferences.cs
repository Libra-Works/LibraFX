using System.Text.RegularExpressions;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;

namespace LiberaFx.Services.Extensions;

public sealed record ChatNodeReference(string DocumentId, IReadOnlyList<Guid> ParentPath, Guid NodeId, string Label);

public static class ChatGraphReferences
{
    private static readonly Regex Links = new(
        @"\[([^\]\r\n]{1,160})\]\(liberafx-node:([0-9a-fA-F/-]{36,1200})\)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string DisplayText(string text) => Links.Replace(text, "$1");

    public static IReadOnlyList<ChatNodeReference> Read(string text, GraphDocumentViewModel document,
        IReadOnlyList<Guid> currentPath)
    {
        var root = document.Workspace.CreateSnapshot();
        var references = new List<ChatNodeReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Links.Matches(text))
        {
            var value = match.Groups[2].Value;
            var absolute = value.StartsWith('/');
            var parts = (absolute ? value[1..] : value).Split('/');
            if (parts.Length > 33 || parts.Any(part => !Guid.TryParseExact(part, "D", out _))) continue;
            var ids = parts.Select(Guid.Parse).ToArray();
            var path = ids.Length == 1 && !absolute ? currentPath.ToArray() : ids[..^1];
            var nodeId = ids[^1];
            if (!TryFind(root, path, nodeId, out var label)) continue;
            if (seen.Add(string.Join("/", path.Append(nodeId))))
                references.Add(new(document.Id!, path, nodeId, label));
            if (references.Count == 32) break;
        }
        return references;
    }

    public static bool Navigate(MainViewModel main, ChatNodeReference reference)
    {
        var document = main.Documents.FirstOrDefault(document => document.Id == reference.DocumentId);
        if (document is null || !TryFind(document.Workspace.CreateSnapshot(),
                reference.ParentPath, reference.NodeId, out _)) return false;
        main.ActivateDocument(document);
        document.Workspace.NavigateToNode(reference.ParentPath, reference.NodeId);
        return document.Workspace.CurrentGraphPath.SequenceEqual(reference.ParentPath) &&
            document.Workspace.SelectedNodes.Any(node => node.Id == reference.NodeId);
    }

    private static bool TryFind(GraphSnapshot graph, IReadOnlyList<Guid> path, Guid nodeId, out string label)
    {
        var names = new List<string>();
        foreach (var id in path)
        {
            var group = graph.Nodes.FirstOrDefault(node => node.Id == id);
            if (group?.ChildGraph is null) { label = ""; return false; }
            names.Add(group.Name);
            graph = group.ChildGraph;
        }
        var target = graph.Nodes.FirstOrDefault(node => node.Id == nodeId);
        if (target is null) { label = ""; return false; }
        names.Add(target.Name);
        label = string.Join(" / ", names);
        return true;
    }
}
