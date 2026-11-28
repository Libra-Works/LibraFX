using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.Views.Graph;

namespace LiberaFx.Views.Windows;

internal sealed class GraphFileComparisonWindow : Window
{
    private readonly TextBox _left = new();
    private readonly TextBox _right = new();
    private readonly Button _compare = new();
    private readonly GraphComparisonView _comparison = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "editor-label" } };
    private readonly LocalizationService _text = LocalizationService.Shared;
    private bool _busy;
    private bool _closed;

    public GraphFileComparisonWindow()
    {
        Title = _text.Get("Vcs.FileCompareTitle");
        Width = 1080;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var left = CreatePicker(_left, "Vcs.LeftFile");
        var right = CreatePicker(_right, "Vcs.RightFile");
        _compare.Content = _text.Get("Vcs.Compare");
        _compare.HorizontalAlignment = HorizontalAlignment.Left;
        _compare.Click += async (_, _) => await CompareAsync();
        _status.Text = _text.Get("Vcs.FileCompareHint");

        var controls = new StackPanel { Spacing = 6, Children = { left, right, _compare } };
        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        root.Children.Add(controls);
        Grid.SetRow(_comparison, 1);
        root.Children.Add(_comparison);
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        Content = root;
        Closed += (_, _) => _closed = true;
        _left.TextChanged += (_, _) => ResetComparison();
        _right.TextChanged += (_, _) => ResetComparison();
        ResetComparison();
    }

    private Control CreatePicker(TextBox path, string labelKey)
    {
        var label = _text.Get(labelKey);
        AutomationProperties.SetName(path, label);
        var browse = new Button { Content = _text.Get("Vcs.BrowseFile") };
        AutomationProperties.SetName(browse, $"{_text.Get("Vcs.BrowseFile")}: {label}");
        browse.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = label,
                    FileTypeFilter = [new FilePickerFileType(_text.Get("Vcs.GraphFiles")) { Patterns = ["*.lfxproj", "*.lfxgroup"] }]
                });
                if (files.FirstOrDefault() is { } selected)
                {
                    using (selected)
                    {
                        if (!_closed && selected.TryGetLocalPath() is { } localPath)
                        {
                            path.Text = localPath;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _status.Text = _text.Get("Vcs.FileReadFailed", ex.Message);
            }
        };
        var field = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        field.Children.Add(path);
        Grid.SetColumn(browse, 1);
        field.Children.Add(browse);
        return new StackPanel { Spacing = 3, Children = { new TextBlock { Text = label }, field } };
    }

    private void ResetComparison()
    {
        _comparison.Clear();
        _status.Text = _text.Get("Vcs.FileCompareHint");
        _compare.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(_left.Text) && !string.IsNullOrWhiteSpace(_right.Text);
    }

    private async Task CompareAsync()
    {
        if (_busy)
        {
            return;
        }
        var left = _left.Text?.Trim() ?? "";
        var right = _right.Text?.Trim() ?? "";
        if (!Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right) ||
            !GraphComparisonFile.IsSupported(left) || !GraphComparisonFile.IsSupported(right))
        {
            _status.Text = _text.Get("Vcs.FileCompareHint");
            return;
        }

        _busy = true;
        _compare.IsEnabled = false;
        _comparison.Clear();
        _status.Text = _text.Get("Vcs.FileComparing");
        try
        {
            var before = await Task.Run(() => GraphComparisonFile.ReadLocalAsync(left));
            var after = await Task.Run(() => GraphComparisonFile.ReadLocalAsync(right));
            if (!_closed && left == _left.Text?.Trim() && right == _right.Text?.Trim())
            {
                _status.Text = _text.Get(_comparison.ShowComparison(before, after) ? "Vcs.Legend" : "Vcs.NoGraphChanges");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                                   System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            if (!_closed && left == _left.Text?.Trim() && right == _right.Text?.Trim())
            {
                _status.Text = _text.Get("Vcs.FileReadFailed", ex.GetBaseException().Message);
            }
        }
        finally
        {
            _busy = false;
            _compare.IsEnabled = !string.IsNullOrWhiteSpace(_left.Text) && !string.IsNullOrWhiteSpace(_right.Text);
        }
    }
}
