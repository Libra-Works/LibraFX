using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using Avalonia.Threading;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class SpriteEditorWindow : Window
{
    private readonly SpriteEditorViewModel _model = null!;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public SpriteEditorWindow()
    {
        InitializeComponent();
    }

    public SpriteEditorWindow(MainViewModel main) : this()
    {
        _model = new SpriteEditorViewModel(main);
        DataContext = _model;
        _model.PropertyChanged += ModelChanged;
        _model.Catalog.PropertyChanged += ModelChanged;
        _timer.Tick += Tick;
        Opened += (_, _) =>
        {
            _model.Catalog.RefreshPeers();
            if (_model.Catalog.SelectedPeer is not null) _ = _model.Catalog.SearchCommand.ExecuteAsync(null);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _model.PropertyChanged -= ModelChanged;
            _model.Catalog.PropertyChanged -= ModelChanged;
            _model.Dispose();
        };
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender == _model && e.PropertyName is nameof(SpriteEditorViewModel.Playing) or nameof(SpriteEditorViewModel.Frame))
            _clock.Restart();
        Atlas.InvalidateVisual();
        FramePreview.InvalidateVisual();
    }

    private void Tick(object? sender, EventArgs e) => _model.Tick(_clock.Elapsed.TotalSeconds);
    private void OnLoadSelected(object? sender, RoutedEventArgs e) => _model.LoadSelected();
    private async void OnOpenLocal(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
