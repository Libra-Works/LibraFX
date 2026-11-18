using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class ExtensionHostTests
{
    public static void Run(string sampleDirectory)
    {
        var root = Path.Combine(AppDataDirectory.Root, "extension-host-tests", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "Extensions", "sample");
        Directory.CreateDirectory(package);
        foreach (var file in Directory.GetFiles(sampleDirectory))
            File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var provider = new ActiveGraphProvider(graph);
        const string id = "liberafx.sample.add-comment";
        using (var untrusted = new EditorExtensionHost(provider, root))
        {
            if (untrusted.Commands.Count != 0 || untrusted.Extensions.Single().Enabled ||
                untrusted.SetEnabled(id, true, explicitlyTrusted: false) ||
                !untrusted.SetEnabled(id, true, explicitlyTrusted: true) || untrusted.Commands.Count != 0)
                throw new InvalidOperationException("Discovery must not execute untrusted packages or load newly approved code.");
        }
        using (var trusted = new EditorExtensionHost(provider, root))
        {
            if (trusted.Commands.Count != 1)
                throw new InvalidOperationException("Trusted external sample did not load: " + trusted.Extensions.Single().Status);
            trusted.ExecuteAsync(id + "/add-comment").GetAwaiter().GetResult();
            if (graph.Nodes.Count != 1 || !graph.History.CanUndo)
                throw new InvalidOperationException("Extension edits must use document history.");
            graph.History.Undo();
            if (graph.Nodes.Count != 0) throw new InvalidOperationException("Extension edit was not undoable.");
            trusted.SetEnabled(id, false, explicitlyTrusted: false);
            try
            {
                trusted.ExecuteAsync(id + "/add-comment").GetAwaiter().GetResult();
                throw new Exception("A disabled extension command was executable.");
            }
            catch (InvalidOperationException) { }
        }
        using (var approve = new EditorExtensionHost(provider, root))
            if (!approve.SetEnabled(id, true, explicitlyTrusted: true)) throw new InvalidOperationException(approve.Status);
        File.AppendAllText(Path.Combine(package, "liberafx-extension.json"), " ");
        using (var changed = new EditorExtensionHost(provider, root))
            if (changed.Commands.Count != 0 || changed.Extensions.Single().Enabled)
                throw new InvalidOperationException("A modified package retained approval.");
        var preferences = Path.Combine(root, "extensions.json");
        using (var stale = new EditorExtensionHost(provider, root))
        {
            File.WriteAllText(preferences, "{}");
            if (stale.SetEnabled(id, true, explicitlyTrusted: true) || File.ReadAllText(preferences) != "{}")
                throw new InvalidOperationException("Concurrent preference changes must not be overwritten.");
        }
        File.WriteAllText(preferences, "{invalid");
        using (var corrupt = new EditorExtensionHost(provider, root))
            if (corrupt.SetEnabled(id, true, explicitlyTrusted: true) || File.ReadAllText(preferences) != "{invalid")
                throw new InvalidOperationException("Corrupt preferences must be preserved without executing plugins.");
    }
}
