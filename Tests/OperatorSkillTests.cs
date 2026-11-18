using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.Views;
using ModelContextProtocol.Server;

internal static class OperatorSkillTests
{
    public static void Run()
    {
        var source = File.ReadAllBytes(OperatorSkillPackage.BundledSkillPath);
        var text = System.Text.Encoding.UTF8.GetString(source);
        var tools = typeof(LiberaFxMcpTools).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null).ToHashSet(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"`((?:libera_|gpu_)[a-z0-9_]+)`"))
            Require(tools.Contains(match.Groups[1].Value), $"Bundled skill documents a missing MCP tool: {match.Value}");
        var root = Path.Combine(Path.GetTempPath(), "liberafx-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var localization = LocalizationService.Shared;
        var originalLanguage = localization.SelectedLanguage;
        var window = new McpWindow();
        window.Show();
        try
        {
            var destination = window.FindControl<TextBox>("OperatorSkillsDirectoryBox")!;
            var replace = window.FindControl<CheckBox>("ReplaceOperatorSkillBox")!;
            var status = window.FindControl<TextBlock>("OperatorSkillStatus")!;
            destination.Text = root;
            Install(window);
            var path = Path.Combine(root, OperatorSkillPackage.SkillName, "SKILL.md");
            Require(File.ReadAllBytes(path).SequenceEqual(source), "MCP window did not install the bundled skill.");
            Install(window);
            Require(status.Text == localization.Get("OperatorSkill.Current", path) &&
                Directory.GetFiles(Path.GetDirectoryName(path)!, "SKILL.md.backup-*").Length == 0,
                "Identical installs should be idempotent and create no backups.");
            File.WriteAllText(path, "User-customized operator instructions.");
            var customized = File.ReadAllBytes(path);
            Install(window);
            Require(File.ReadAllBytes(path).SequenceEqual(customized) &&
                status.Text!.StartsWith(localization.Get("OperatorSkill.Failed", "")),
                "An update replaced customized instructions without explicit permission.");
            replace.IsChecked = true;
            Install(window);
            var backup = Directory.GetFiles(Path.GetDirectoryName(path)!, "SKILL.md.backup-*").Single();
            Require(File.ReadAllBytes(path).SequenceEqual(source) && File.ReadAllBytes(backup).SequenceEqual(customized) &&
                status.Text == localization.Get("OperatorSkill.Updated", path, backup),
                "An explicit update must install the bundled version and preserve the previous instructions.");
            var sentinel = Path.Combine(root, "unrelated.txt");
            File.WriteAllText(sentinel, "Retain unrelated files.");
            destination.Text = "relative-directory";
            Install(window);
            Require(File.ReadAllText(sentinel) == "Retain unrelated files." &&
                Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0,
                "Rejected installation changed unrelated files or left temporary files.");
            localization.SelectedLanguage = localization.Languages.Single(language => language.Id == "ja");
            Dispatcher.UIThread.RunJobs();
            Require(window.FindControl<Button>("InstallOperatorSkillButton")!.Content?.ToString() ==
                    localization.Get("OperatorSkill.Install") &&
                AutomationProperties.GetName(window.FindControl<IconButton>("CopyOperatorSkillPathButton")!) ==
                    localization.Get("OperatorSkill.CopyPath"),
                "Operator installation controls must update their labels and accessibility names in Japanese.");
            Console.WriteLine("Bundled operator skill tool references, UI installation, protected updates and backups passed.");
        }
        finally
        {
            window.Close();
            localization.SelectedLanguage = originalLanguage;
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Install(McpWindow window)
    {
        var button = window.FindControl<Button>("InstallOperatorSkillButton")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (button.IsEnabled) return;
        var frame = new DispatcherFrame();
        using var poll = DispatcherTimer.Run(() =>
        {
            if (button.IsEnabled) frame.Continue = false;
            return !button.IsEnabled;
        }, TimeSpan.FromMilliseconds(10));
        using var timeout = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.PushFrame(frame);
        Require(button.IsEnabled, "Operator skill installation timed out.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
