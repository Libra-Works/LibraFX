using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Crash;
using LiberaFx.Services.Extensions;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Timeline;
using LiberaFx.Views.Windows;

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
                VerifyEditingSurfaces(main, directory);
                foreach (var scheme in new[] { "LibraDark", "LibraLight", "Dracula", "SolarizedLight" })
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

    private static void VerifyEditingSurfaces(MainViewModel main, string directory)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = graph.CreateNode(GraphNodeKind.FloatFCurve);
        var first = Guid.NewGuid();
        graph.SetCurve(node, new FCurveSnapshot([
            new(first, 0, .5, 0, 0, CurveInterpolation.Linear),
            new(Guid.NewGuid(), 1, .5, 0, 0, CurveInterpolation.Linear)
        ]), "Appearance fixture");
        graph.SelectNode(node);
        var graphProvider = new ActiveGraphProvider(graph);
        using var playback = new EditorPlayback();
        using var curveModel = new CurveEditorViewModel(graphProvider, playback);
        curveModel.SelectKey(first);
        var timeline = new TimelineView { DataContext = new TimelineViewModel(graphProvider, playback) };
        var curve = new CurveEditorView { DataContext = curveModel };
        var panel = new Grid { RowDefinitions = new RowDefinitions("*,*") };
        panel.Children.Add(timeline);
        Grid.SetRow(curve, 1);
        panel.Children.Add(curve);
        var window = new Window { Width = 900, Height = 680, Content = panel };
        window.Show();
        var localization = LocalizationService.Shared;
        var originalLanguage = localization.SelectedLanguage;
        try
        {
            var timelineCanvas = timeline.FindControl<TimelineCanvas>("Canvas")!;
            var curveCanvas = curve.FindControl<FCurveCanvas>("CurveCanvas")!;
            var timeUnit = curve.FindControl<ToggleButton>("CurveTimeUnitToggle")!;
            var toggle = new ToggleButtonAutomationPeer(timeUnit).GetProvider<IToggleProvider>()!;
            toggle.Toggle();
            Dispatcher.UIThread.RunJobs();
            if (!playback.ShowSeconds)
            {
                throw new InvalidOperationException("The F-Curve time-unit control did not update shared playback.");
            }
            toggle.Toggle();
            Dispatcher.UIThread.RunJobs();
            if (playback.ShowSeconds)
            {
                throw new InvalidOperationException("The F-Curve time-unit control did not return to frames.");
            }

            foreach (var (scheme, language, width, dpi) in new[] { ("LibraDark", "en", 900, 96),
                         ("CyberTeal", "en", 900, 96), ("LibraLight", "ja", 560, 144), ("LibraDark", "en", 900, 96) })
            {
                window.Width = width;
                localization.SelectedLanguage = localization.Languages.Single(item => item.Id == language);
                main.SelectedColorScheme = scheme;
                if (main.SelectedColorScheme != scheme || localization.SelectedLanguage.Id != language)
                {
                    throw new InvalidOperationException($"Could not apply {scheme}/{language}: {main.AppearanceStatus}");
                }
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                if (OperatingSystem.IsWindows() &&
                    Environment.GetEnvironmentVariable("LIBERAFX_AUDIT_LIVE_PIXELS") == "1")
                {
                    VerifyLiveCanvasPixels(window, timelineCanvas, curveCanvas, scheme);
                }
                var output = Path.Combine(directory, scheme);
                Directory.CreateDirectory(output);
                VerifyCanvas(timelineCanvas, "TimelineCanvas", output, dpi);
                VerifyCanvas(curveCanvas, "FCurveCanvas", output, dpi);
            }
        }
        finally
        {
            window.Close();
            localization.SelectedLanguage = originalLanguage;
        }
    }

    private static void VerifyLiveCanvasPixels(Window window, Control timeline, Control curve, string scheme)
    {
        var handle = window.TryGetPlatformHandle()!.Handle;
        var origin = window.PointToScreen(default);
        var width = (int)Math.Ceiling(window.ClientSize.Width * window.RenderScaling);
        var height = (int)Math.Ceiling(window.ClientSize.Height * window.RenderScaling);
        var expected = ((ISolidColorBrush)ThemeSettingsService.EditorBrush("CurveBackground")).Color;
        var color = (uint)(expected.R | (expected.G << 8) | (expected.B << 16));
        var frame = new DispatcherFrame();
        var ready = false;
        var dc = GetDC(handle);
        var memory = CreateCompatibleDC(dc);
        var bitmap = CreateCompatibleBitmap(dc, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (dc == 0 || memory == 0 || bitmap == 0 || previous == 0)
            {
                throw new InvalidOperationException("Could not allocate a bitmap for the editor window.");
            }
            const uint clientOnly = 1;
            const uint renderFullContent = 2;
            using var sample = DispatcherTimer.Run(() =>
            {
                ready = PrintWindow(handle, memory, clientOnly | renderFullContent)
                    && HasBackground(timeline) && HasBackground(curve);
                if (ready)
                {
                    frame.Continue = false;
                }
                return !ready;
            }, TimeSpan.FromMilliseconds(30));
            using var timeout = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromSeconds(3));
            Dispatcher.UIThread.PushFrame(frame);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(handle, dc);
        }
        if (!ready)
        {
            throw new InvalidOperationException($"Live Timeline and F-Curve did not display {scheme} background {expected}.");
        }

        bool HasBackground(Control canvas)
        {
            for (var i = 0; i < 10; i++)
            {
                var point = canvas.PointToScreen(new Point(canvas.Bounds.Width * .75 + i, canvas.Bounds.Height * .75 + i));
                if (GetPixel(memory, point.X - origin.X, point.Y - origin.Y) == color)
                {
                    return true;
                }
            }
            return false;
        }
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);

    private static void VerifyCanvas(Control canvas, string name, string output, int dpi)
    {
        var size = new PixelSize((int)Math.Ceiling(canvas.Bounds.Width * dpi / 96),
            (int)Math.Ceiling(canvas.Bounds.Height * dpi / 96));
        using var bitmap = new RenderTargetBitmap(size, new Vector(dpi, dpi));
        bitmap.Render(canvas);
        bitmap.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), pinned.AddrOfPinnedObject(),
                pixels.Length, stride);
        }
        finally
        {
            pinned.Free();
        }

        foreach (var (key, minimum) in new[] { ("CurveBackground", 200), ("CurveLine", 20), ("CurveSelected", 8) })
        {
            var expected = ((ISolidColorBrush)ThemeSettingsService.EditorBrush(key)).Color;
            var count = 0;
            for (var index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index] == expected.B && pixels[index + 1] == expected.G &&
                    pixels[index + 2] == expected.R && pixels[index + 3] == expected.A)
                {
                    count++;
                }
            }
            if (count < minimum)
            {
                throw new InvalidOperationException($"{name} did not paint enough {key} pixels ({expected}): {count}.");
            }
        }
    }
}
