using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class EditorAppearanceAudit
{
    public static void Capture(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var main = new MainViewModel();
        using var crashDrafts = new CrashDraftStore();
        try
        {
            (string Name, Func<Window> Create)[] windows =
            [
                ("Settings", () => new SettingsWindow { DataContext = main }),
                ("StartupSetup", () => new StartupSetupWindow(main, new StartupSetupState(), crashDrafts)),
                ("CrashReports", () => new CrashReportsWindow(crashDrafts, () => [])),
                ("About", () => new AboutWindow()),
                ("Help", () => new HelpWindow()),
                ("Splash", () => new SplashWindow()),
                ("ColorEditor", () => new ColorEditorWindow()),
                ("AnythingSearch", () => new AnythingSearchWindow(main)),
                ("GroupUpdates", () => new GroupUpdatesWindow()),
                ("GroupParameters", () => new GroupExposedEditorWindow()),
                ("UnsavedChanges", () => new UnsavedChangesWindow())
            ];
            string[] panes = ["Content", "SpriteEditor", "LocalProjects", "Log", "Mcp", "PreviewConnections",
                "PreviewProfiler", "Problems", "RevisionHistory", "LayoutPresets", "Chat", "LiveGame"];
            var factory = (DockFactory)main.Layout.Factory!;
            var report = new List<string> { "Scheme\tWindow\tBackground\tForeground\tVisible text sizes" };
            var workspace = new MainWindow { Width = 1440, Height = 900, DataContext = main };
            workspace.Show();
            try
            {
                    var setupState = new StartupSetupState();
                    if (setupState.IsPending)
                    {
                        var setup = new StartupSetupWindow(main, setupState, crashDrafts);
                        setup.Show(workspace);
                        try
                        {
                            setup.FindControl<ComboBox>("ColorSchemePicker")!.SelectedItem = "Dracula";
                            setup.FindControl<Button>("ContinueButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            if (setup.IsVisible || new StartupSetupState().IsPending ||
                                new ThemeSettingsService().Current.ColorScheme != "Dracula")
                            {
                                throw new InvalidOperationException("First-run setup did not persist the selected color scheme.");
                            }
                        }
                        finally
                        {
                            if (setup.IsVisible)
                            {
                                setup.Close();
                            }
                        }
                    }
                foreach (var scheme in new[] { "Dracula", "SolarizedLight" })
                {
                    main.SelectedColorScheme = scheme;
                    if (main.SelectedColorScheme != scheme) throw new InvalidOperationException(main.AppearanceStatus);
                    var output = Path.Combine(directory, scheme);
                    Directory.CreateDirectory(output);
                    main.ShowDockPanel("Timeline");
                    Snapshot(workspace, "Main");
                    foreach (var pane in panes)
                    {
                        if (!main.ShowDockPanel(pane))
                        {
                            throw new InvalidOperationException($"Missing dock pane: {pane}");
                        }
                        var tool = factory.Tools.Single(tool => tool.Id == pane);
                        var host = factory.FindRoot(tool)?.Window?.Host as Window;
                        Snapshot(host ?? workspace, pane);
                    }
                    foreach (var (name, create) in windows)
                    {
                        var window = create();
                        window.Show();
                        try { Snapshot(window, name); }
                        finally { window.Close(); }
                    }

                    void Snapshot(Window window, string name)
                    {
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        using var bitmap = new RenderTargetBitmap(new PixelSize(
                            (int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
                        bitmap.Render(window);
                        bitmap.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
                        var fonts = window.GetVisualDescendants().OfType<TextBlock>()
                            .Where(text => text.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(text.Text))
                            .GroupBy(text => text.FontSize).OrderBy(group => group.Key)
                            .Select(group => $"{group.Key}:{group.Count()}");
                        report.Add($"{scheme}\t{name}\t{ColorOf(window.Background)}\t{ColorOf(window.Foreground)}\t{string.Join(", ", fonts)}");
                    }
                }
            }
            finally
            {
                foreach (var window in main.Layout.Windows?.ToArray() ?? [])
                {
                    window.Host?.Exit();
                }
                workspace.Close();
            }
            File.WriteAllLines(Path.Combine(directory, "appearance.tsv"), report);
        }
        finally
        {
            main.DisposeStorageFiles();
        }
    }

    private static string ColorOf(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToString() : brush?.GetType().Name ?? "(none)";

}
