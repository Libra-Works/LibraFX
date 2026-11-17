using System.Text.Json;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

namespace LiberaFx.Tests;

internal static class LifecycleNodePresentationTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        var localization = LocalizationService.Shared;
        var originalLanguage = localization.SelectedLanguage;
        try
        {
            foreach (var languageId in new[] { "en", "ja" })
            {
                localization.SelectedLanguage = localization.Languages.Single(language => language.Id == languageId);
                VerifyLifecycleSummaries();
            }
            VerifyBodyDoubleClickNavigation();
        }
        finally
        {
            localization.SelectedLanguage = originalLanguage;
        }
    }

    private static void VerifyLifecycleSummaries()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var spawnable = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        Check(spawnable.IsLifecycleNode &&
              spawnable.LifecycleSummary == LocalizationService.Shared.Get(
                  "Editor.Lifecycle.LifeTimeSummary",
                  LocalizationService.Shared.Get("Editor.Lifecycle.Unbounded")),
            "A Spawnable with no Life Time must visibly report an unbounded lifetime.");
        Check(spawnable.LifecycleRoleLabel == LocalizationService.Shared.Get("Editor.Lifecycle.OwnerRole") &&
              spawnable.LifecycleRoleLabel != graph.CreateNode(GraphNodeKind.Signal).LifecycleRoleLabel,
            "Spawnable must have a distinct, localized expandable-owner role.");

        Check(graph.EnterGraph(spawnable), "Cannot enter Spawnable for lifecycle summary test.");
        var lifetime = graph.CreateNode(new GraphNodeKind("LifeTime"));
        graph.SetParameter(lifetime, "seconds", JsonSerializer.SerializeToElement(4.5));
        var exposedId = graph.PublishGroupParameter(lifetime, "seconds");
        graph.NavigateToGraph(0);
        spawnable = graph.GetNode(spawnable.Id);
        var configured = LocalizationService.Shared.Get("Editor.Lifecycle.Seconds",
            4.5d.ToString("G", CultureInfo.CurrentCulture));
        Check(spawnable.LifecycleSummary == LocalizationService.Shared.Get(
                  "Editor.Lifecycle.LifeTimeSummary", configured),
            "Spawnable summary must reflect the configured child Life Time.");

        graph.SetGroupInstanceValue(spawnable, new GroupInstanceValue(exposedId,
            [new GroupInstanceComponent(JsonSerializer.SerializeToElement(9d))]));
        var overridden = LocalizationService.Shared.Get("Editor.Lifecycle.InstanceOverride",
            LocalizationService.Shared.Get("Editor.Lifecycle.Seconds",
                9d.ToString("G", CultureInfo.CurrentCulture)));
        Check(spawnable.LifecycleSummary == LocalizationService.Shared.Get(
                  "Editor.Lifecycle.LifeTimeSummary", overridden),
            "Spawnable summary must show a published per-instance Life Time override.");

        Check(graph.EnterGraph(spawnable), "Cannot re-enter Spawnable for dynamic input summary test.");
        var source = graph.CreateNode(GraphNodeKind.Float);
        lifetime = graph.GetNode(lifetime.Id);
        Check(graph.TryConnectNodes(source, lifetime, out _, targetPortId: "seconds"),
            "Cannot connect a dynamic Life Time input.");
        graph.NavigateToGraph(0);
        spawnable = graph.GetNode(spawnable.Id);
        Check(spawnable.LifecycleSummary == LocalizationService.Shared.Get(
                  "Editor.Lifecycle.LifeTimeSummary",
                  LocalizationService.Shared.Get("Editor.Lifecycle.InputDependent")),
            "A connected Life Time must be described as input-dependent, not as its default or override.");

        var delay = graph.CreateNode(new GraphNodeKind("Delay"));
        Check(delay.LifecycleSummary == LocalizationService.Shared.Get("Editor.Lifecycle.DelaySummary",
                  LocalizationService.Shared.Get("Editor.Lifecycle.Seconds",
                      1d.ToString("G", CultureInfo.CurrentCulture))) &&
              delay.LifecycleRoleLabel == LocalizationService.Shared.Get("Editor.Lifecycle.DelayRole"),
            "Delay must show its duration and its non-container role.");
        var delayedSignal = graph.CreateNode(GraphNodeKind.Signal);
        Check(graph.TryConnectNodes(delayedSignal, delay, out _, targetPortId: "signal") &&
              graph.TryConnectNodes(graph.CreateNode(GraphNodeKind.Float), delay, out _, targetPortId: "seconds"),
            "Cannot connect Delay's signal and seconds inputs.");
        Check(delay.LifecycleSummary == LocalizationService.Shared.Get("Editor.Lifecycle.DelaySummary",
                  LocalizationService.Shared.Get("Editor.Lifecycle.InputDependent")),
            "A connected Delay duration must be described as input-dependent.");

        var lifetimeDefinition = GraphNodeDefinition.All.Single(definition =>
            definition.Kind == new GraphNodeKind("LifeTime"));
        var invalidLifetime = new GraphNodeViewModel(graph, lifetimeDefinition, "Life Time", 0, 0,
            parameters: [new NodeParameterSnapshot("seconds", "float", JsonSerializer.SerializeToElement(-2d))]);
        Check(invalidLifetime.LifecycleSummary == LocalizationService.Shared.Get(
                  "Editor.Lifecycle.LifeTimeSummary", LocalizationService.Shared.Get("Editor.Lifecycle.Invalid")),
            "An unsupported negative Life Time must be identified as invalid, not shown as a valid duration.");
    }

    private static void VerifyBodyDoubleClickNavigation()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        Check(graph.EnterGraph(group), "Cannot enter Graph Group to create an interactive pin.");
        graph.CreateNode(new GraphNodeKind("GroupInputSignal"));
        graph.NavigateToGraph(0);
        group = graph.GetNode(group.Id);
        var signal = graph.CreateNode(GraphNodeKind.Signal);
        var groupView = new GraphNodeView { DataContext = group };
        var signalView = new GraphNodeView { DataContext = signal };
        var panel = new StackPanel { Children = { groupView, signalView } };
        var window = new Window { Content = panel, Width = 700, Height = 500, ShowActivated = false };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var groupBody = groupView.FindControl<Border>("NodeBody")
                ?? throw new InvalidOperationException("Graph Group body was not found.");
            RaiseDoubleClick(groupBody, window, KeyModifiers.None);
            Check(!graph.IsRootGraph, "Double-clicking a group's non-interactive body must enter it.");

            graph.NavigateToGraph(0);
            signal = graph.GetNode(signal.Id);
            signalView.DataContext = signal;
            Dispatcher.UIThread.RunJobs();
            RaiseDoubleClick(signalView.FindControl<Border>("NodeBody")!, window, KeyModifiers.None);
            Check(graph.IsRootGraph, "Double-clicking an ordinary Signal node must not navigate.");

            group = graph.GetNode(group.Id);
            groupView.DataContext = group;
            Dispatcher.UIThread.RunJobs();
            var input = groupView.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Tag is GraphInputPortDefinition);
            RaiseDoubleClick(input, window, KeyModifiers.None);
            Check(graph.IsRootGraph, "Double-clicking an interactive group port must not enter the group.");

            RaiseDoubleClick(groupView.FindControl<Border>("NodeBody")!, window, KeyModifiers.Control);
            Check(graph.IsRootGraph, "Control-modified group double-click must preserve selection gestures.");
            RaiseDoubleClick(groupView.FindControl<Border>("NodeBody")!, window, KeyModifiers.Shift);
            Check(graph.IsRootGraph, "Shift-modified group double-click must not be consumed as navigation.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void RaiseDoubleClick(Control target, Window window, KeyModifiers modifiers)
    {
        var pointer = new Pointer(51, PointerType.Mouse, true);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, new Point(20, 20), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            modifiers, 2));
    }
}
