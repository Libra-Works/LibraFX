using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class SpriteEditorView : UserControl
{
    private SpriteEditorViewModel _model = null!;
    private SpriteEditorViewModel? _attachedModel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public SpriteEditorView()
    {
        InitializeComponent();
        _timer.Tick += Tick;
        AttachedToVisualTree += (_, _) => AttachModel();
        DetachedFromVisualTree += (_, _) => DetachModel();
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree()) AttachModel();
        };
    }

    private void AttachModel()
    {
        if (ReferenceEquals(_attachedModel, DataContext)) return;
        DetachModel();
        if (DataContext is SpriteEditorViewModel model)
        {
            _attachedModel = _model = model;
            model.PropertyChanged += ModelChanged;
            model.Catalog.PropertyChanged += ModelChanged;
            model.InitializeCatalog();
            _clock.Restart();
            _timer.Start();
        }
    }

    private void DetachModel()
    {
        _timer.Stop();
        if (_attachedModel is not { } model) return;
        model.PropertyChanged -= ModelChanged;
        model.Catalog.PropertyChanged -= ModelChanged;
        _attachedModel = null;
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender == _model && e.PropertyName is nameof(SpriteEditorViewModel.Playing) or nameof(SpriteEditorViewModel.Frame))
            _clock.Restart();
        Atlas.InvalidateVisual();
        FramePreview.InvalidateVisual();
        CompositionPreview.InvalidateVisual();
        Timeline.Refresh();
    }

    private void Tick(object? sender, EventArgs e) => _model.Tick(_clock.Elapsed.TotalSeconds);
    private void OnLoadSelected(object? sender, RoutedEventArgs e) => _model.LoadSelected();
    private void OnUseEnginePreview(object? sender, RoutedEventArgs e) => _model.ClearSourceImage();
    private void OnAddCell(object? sender, RoutedEventArgs e) => _model.AddCell();
    private void OnDeleteCell(object? sender, RoutedEventArgs e) => _model.RemoveCell();
    private void OnAddFrame(object? sender, RoutedEventArgs e) => _model.AddFrame();
    private void OnInsertFrame(object? sender, RoutedEventArgs e) => _model.InsertFrame();
    private void OnCellDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) => _model.AddFrame();
    private void OnDuplicateFrame(object? sender, RoutedEventArgs e) => _model.DuplicateFrame();
    private void OnDeleteFrame(object? sender, RoutedEventArgs e) => _model.RemoveFrame();
    private void OnFrameUp(object? sender, RoutedEventArgs e) => _model.MoveFrame(-1);
    private void OnFrameDown(object? sender, RoutedEventArgs e) => _model.MoveFrame(1);
    private void OnPreviousFrame(object? sender, RoutedEventArgs e) =>
        _model.SelectFrame(Math.Max(0, _model.PreviewFrame - 1));
    private void OnNextFrame(object? sender, RoutedEventArgs e) =>
        _model.SelectFrame(Math.Min(Math.Max(0, _model.Frames.Count - 1), _model.PreviewFrame + 1));
    private void OnTogglePlayback(object? sender, RoutedEventArgs e) => _model.Playing = !_model.Playing;
    private void OnResetZoom(object? sender, RoutedEventArgs e) { Atlas.ResetZoom(); CompositionPreview.ResetZoom(); }
    private void OnAddPart(object? sender, RoutedEventArgs e) => _model.AddPart();
    private void OnRemovePart(object? sender, RoutedEventArgs e) => _model.RemovePart();
    private void OnPartBackward(object? sender, RoutedEventArgs e) => _model.MovePart(-1);
    private void OnPartForward(object? sender, RoutedEventArgs e) => _model.MovePart(1);
    private void OnSetKey(object? sender, RoutedEventArgs e) => _model.SetTransformKey();
    private void OnDeleteKey(object? sender, RoutedEventArgs e) => _model.RemoveTransformKey();
    private void OnAuthoringTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AuthoringTabs?.SelectedIndex == 1 && PartProperties is not null) PartProperties.IsExpanded = true;
    }

    private async void OnAutoFlipbook(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanDrawCells || !_model.TryGridSize(out _, out _))
        {
            _model.GenerateGridFlipbook();
            return;
        }
        if (_model.Frames.Count > 0)
        {
            var text = LocalizationService.Shared;
            var dialog = new Window
            {
                Title = text.Get("Sprite.AutoFlipbook"), Width = 480, SizeToContent = SizeToContent.Height,
                CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var cancel = new Button { Content = text.Get("Sprite.Cancel") };
            var confirm = new Button { Content = text.Get("Sprite.ReplaceTimeline") };
            cancel.Click += (_, _) => dialog.Close(false);
            confirm.Click += (_, _) => dialog.Close(true);
            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = text.Get("Sprite.ReplaceTimelineNotice", _model.Frames.Count),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, confirm } }
                }
            };
            if (TopLevel.GetTopLevel(this) is not Window owner || !await dialog.ShowDialog<bool>(owner)) return;
        }
        _model.GenerateGridFlipbook();
    }
    private void OnScrub(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider { IsFocused: false, IsPointerOver: false } ||
            _model is null || _model.Frames.Count == 0) return;
        if (_model is not null)
            _model.SelectFrame(Math.Clamp((int)Math.Round(e.NewValue), 0, _model.Frames.Count - 1));
    }

    private async void OnOpenLocal(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { StorageProvider: { } storage }) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Shared.Get("Sprite.OpenLocal"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("PNG / JPEG") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + count > CommentImage.MaximumSourceBytes)
                    throw new InvalidDataException(LocalizationService.Shared.Get("Sprite.InvalidImage"));
                buffer.Write(chunk, 0, count);
            }
            if (!CommentImage.IsValidSource(buffer.ToArray(), out _, out _))
                throw new InvalidDataException(LocalizationService.Shared.Get("Sprite.InvalidImage"));
            buffer.Position = 0;
            _model.SetLocalPreview(new Bitmap(buffer));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or ArgumentException or FormatException or NotSupportedException)
        {
            _model.SetError(exception.Message);
        }
    }
    private void OnApplySelected(object? sender, RoutedEventArgs e) => _model.Apply(true);
    private void OnCreate(object? sender, RoutedEventArgs e) => _model.Apply(false);
}
