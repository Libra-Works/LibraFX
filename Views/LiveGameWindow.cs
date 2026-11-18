using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

/// <summary>Actual game editing UI; never routes to PreviewHost or auto-discovers games.</summary>
public sealed class LiveGameWindow : Window
{
    private readonly LiveGameWindowViewModel model;
    private readonly Func<CancellationToken, Task<string?>> chooseDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock explanation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock directoryLabel = new();
    private readonly TextBlock restartNotice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock targetLabel = new();
    private readonly TextBox directory = new() { Name = "LiveGameDirectory" };
    private readonly Button browse = new() { Name = "LiveGameBrowse" };
    private readonly CheckBox consent = new() { Name = "LiveGameConsent" };
    private readonly Button connect = new() { Name = "LiveGameConnect" };
    private readonly Button disconnect = new() { Name = "LiveGameDisconnect" };
    private readonly Button refresh = new() { Name = "LiveGameRefresh" };
    private readonly Button apply = new() { Name = "LiveGameApply" };
    private readonly Button cancel = new() { Name = "LiveGameCancel" };
    private readonly ListBox instances = new() { Name = "LiveGameInstances", MinHeight = 120 };
    public LiveGameWindowViewModel Model => model;

    public LiveGameWindow(LiveGameWindowViewModel model, Func<CancellationToken, Task<string?>>? chooseDirectory = null)
    {
        this.model = model;
        this.chooseDirectory = chooseDirectory ?? PickDirectoryAsync;
        DataContext = model;
        Width = 820; Height = 620; MinWidth = 580; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        directory.Bind(TextBox.TextProperty, new Binding(nameof(model.EndpointDirectory)) { Mode = BindingMode.TwoWay });
        consent.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(model.Consent)) { Mode = BindingMode.TwoWay });
        instances.ItemsSource = model.Instances;
        instances.Bind(ListBox.SelectedItemProperty, new Binding(nameof(model.SelectedInstance)) { Mode = BindingMode.TwoWay });
        connect.Command = model.ConnectCommand; disconnect.Command = model.DisconnectCommand;
        refresh.Command = model.RefreshCommand; apply.Command = model.ApplyCommand; cancel.Command = model.CancelCommand;
        var destination = new TextBlock { Name = "LiveGameDestination", TextWrapping = TextWrapping.Wrap };
        destination.Bind(TextBlock.TextProperty, new Binding(nameof(model.Destination)));
        var status = new TextBox { Name = "LiveGameStatus", IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        status.Bind(TextBox.TextProperty, new Binding(nameof(model.Status)));
        var path = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        path.Children.Add(directory); Grid.SetColumn(browse, 1); path.Children.Add(browse);
        var top = new StackPanel { Spacing = 8, Children =
        {
            explanation, directoryLabel, path, consent,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { connect, disconnect, refresh } },
            destination, targetLabel
        } };
        var bottom = new StackPanel { Spacing = 8, Children =
        {
            restartNotice,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, cancel } },
            status
        } };
        var content = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10 };
        content.Children.Add(top); Grid.SetRow(instances, 1); content.Children.Add(instances);
        Grid.SetRow(bottom, 2); content.Children.Add(bottom);
        Content = content;
        browse.Click += async (_, _) =>
        {
            if (model.Busy) return;
            try
            {
                var selected = await this.chooseDirectory(lifetime.Token);
                if (!lifetime.IsCancellationRequested && selected is not null) model.EndpointDirectory = selected;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { model.ReportLocalFailure(error); }
        };
        model.PropertyChanged += ModelChanged;
        LocalizationService.Shared.LanguageChanged += LanguageChanged;
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            model.Dispose();
            model.PropertyChanged -= ModelChanged;
            LocalizationService.Shared.LanguageChanged -= LanguageChanged;
            lifetime.Dispose();
            DataContext = null;
        };
        RefreshLabels();
    }
    private async Task<string?> PickDirectoryAsync(CancellationToken token)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = Text("ChooseDirectory"), AllowMultiple = false }).WaitAsync(token);
        return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
    }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(model.Busy))
            directory.IsEnabled = browse.IsEnabled = consent.IsEnabled = instances.IsEnabled = !model.Busy;
    }
    private void LanguageChanged(object? sender, EventArgs args) { RefreshLabels(); model.RefreshLocalizedText(); }
    private void RefreshLabels()
    {
        Title = Text("Title");
        explanation.Text = Text("Explanation"); directoryLabel.Text = Text("Directory");
        restartNotice.Text = Text("RestartNotice"); targetLabel.Text = Text("Targets");
        browse.Content = Text("ChooseDirectory"); connect.Content = Text("Connect"); disconnect.Content = Text("Disconnect");
        refresh.Content = Text("Refresh"); apply.Content = Text("Apply"); cancel.Content = Text("Cancel");
        consent.Content = new TextBlock { Text = Text("Consent"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(directory, Text("Directory"));
        AutomationProperties.SetName(instances, Text("Targets"));
        instances.ItemTemplate = new FuncDataTemplate<LiveGameInstance>((instance, _) => instance is null ? null :
            new StackPanel { Margin = new Thickness(4), Spacing = 3, Children =
            {
                new TextBlock { Text = instance.Label },
                new TextBlock { Text = instance.Target },
                new TextBlock { Text = Text("InstanceState", instance.RenderCount, Text(instance.Finished ? "Finished" : "Playing")) }
            } });
    }
    private static string Text(string key, params object?[] values) => LocalizationService.Shared.Get("LiveGame." + key, values);
}
