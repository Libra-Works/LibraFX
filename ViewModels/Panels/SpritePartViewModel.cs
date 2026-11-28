using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Services.Sprite;

namespace LiberaFx.ViewModels.Panels;

public sealed class SpritePartViewModel : ObservableObject
{
    private string _name;
    private bool _visible;
    public Guid Id { get; }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public bool Visible { get => _visible; set => SetProperty(ref _visible, value); }
    public ObservableCollection<SpriteAnimationFrame> Frames { get; } = [];
    public ObservableCollection<SpriteTransformKey> Keys { get; } = [];

    public SpritePartViewModel(SpriteCompositionTrack data)
    {
        Id = data.Id;
        _name = data.Name;
        _visible = data.Visible;
        foreach (var frame in data.Frames) Frames.Add(frame);
        foreach (var key in data.Keys) Keys.Add(key);
    }

    public SpriteCompositionTrack Snapshot() => new()
    {
        Id = Id, Name = Name, Visible = Visible, Frames = Frames.ToList(), Keys = Keys.ToList()
    };

    public SpritePose PoseAt(double frame) => new SpriteCompositionTrack { Keys = Keys.ToList() }.PoseAt(frame);
}
