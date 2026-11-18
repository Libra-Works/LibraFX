using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class ExtensionHostTests
{
    public static void VerifyCrashProvider(string sampleDirectory)
    {
        const string id = "liberafx.sample.redmine-crash";
        const string redmineId = "liberafx.sample.redmine.crash-reports";
        const string assembly = "LiberaFx.Sample.RedmineCrash.dll";
        const string manifest = "liberafx-extension.json";
        var root = Path.Combine(AppDataDirectory.Root, "crash-provider-host-tests", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "Extensions", "redmine");
        Directory.CreateDirectory(package);
        File.Copy(Path.Combine(sampleDirectory, manifest), Path.Combine(package, manifest));
        File.Copy(Path.Combine(sampleDirectory, assembly), Path.Combine(package, assembly));
        var config = Path.Combine(package, "redmine.json");
        const string validConfiguration =
            """{"BaseUrl":"https://example.invalid/redmine/","ProjectId":42,"CredentialTarget":"LiberaFx.ContractTest"}""";
        var graph = new ActiveGraphProvider(new GraphWorkspaceViewModel(new UndoRedoManager()));
        var github = new GitHubCrashReportProvider("fixture/reports", "LiberaFx.ContractTest");

        File.WriteAllText(config, "{broken");
        using (var untrusted = CreateHost())
        {
            RequireBuiltInOnly(untrusted);
            if (untrusted.Extensions.Single().Enabled || untrusted.SetEnabled(id, true, explicitlyTrusted: false) ||
                !untrusted.SetEnabled(id, true, explicitlyTrusted: true))
            {
                throw new InvalidOperationException("The external provider must require explicit trust.");
            }
        }
        using (var broken = CreateHost())
        {
            RequireBuiltInOnly(broken);
            if (!broken.Extensions.Single().Status.StartsWith("Load failed:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("An approved provider with broken configuration was not diagnosed.");
            }
        }
        File.WriteAllText(config, validConfiguration);
        using (var changed = CreateHost())
        {
            RequireBuiltInOnly(changed);
            if (changed.Extensions.Single().Enabled || !changed.SetEnabled(id, true, explicitlyTrusted: true))
            {
                throw new InvalidOperationException("Changing provider configuration must require reapproval.");
            }
        }
        using (var enabled = CreateHost())
        {
            if (!enabled.CrashReportProviders.Any(provider => provider.Id == redmineId) ||
                !enabled.SetEnabled(id, false, explicitlyTrusted: false))
            {
                throw new InvalidOperationException("The independently built Redmine DLL did not load or disable.");
            }
            RequireBuiltInOnly(enabled);
        }
        using (var restarted = CreateHost())
        {
            RequireBuiltInOnly(restarted);
        }
        var manifestPath = Path.Combine(package, manifest);
        var manifestContent = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, manifestContent.Replace("\"apiVersion\": 1", "\"apiVersion\": 999",
            StringComparison.Ordinal));
        using (var incompatible = CreateHost())
        {
            RequireBuiltInOnly(incompatible);
            if (incompatible.SetEnabled(id, true, explicitlyTrusted: true))
            {
                throw new InvalidOperationException("The crash provider can be enabled with an incompatible SDK version.");
            }
        }
        File.WriteAllText(manifestPath, manifestContent.Replace(assembly, "Missing.Redmine.dll",
            StringComparison.Ordinal));
        using var missing = CreateHost();
        RequireBuiltInOnly(missing);
        if (missing.Extensions.Count != 0 && missing.Extensions.Single().Enabled)
        {
            throw new InvalidOperationException("A provider whose DLL is missing must not be enabled.");
        }

        EditorExtensionHost CreateHost() => new(graph, root, [github]);

        void RequireBuiltInOnly(EditorExtensionHost host)
        {
            if (host.CrashReportProviders.Count != 1 || !ReferenceEquals(host.CrashReportProviders[0], github))
            {
                throw new InvalidOperationException("The external provider unexpectedly registered or replaced GitHub.");
            }
        }
    }

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
