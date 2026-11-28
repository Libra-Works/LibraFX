using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiberaFx.Services;

/// <summary>Prepared, engine-neutral sprite timeline. Offsets address float4 records.</summary>
public sealed record ParticleSpriteAnimation(
    [property:JsonPropertyName("version")] int Version,
    [property:JsonPropertyName("partCount")] int PartCount,
    [property:JsonPropertyName("values")] double[] Values)
{
    public void ValidateGpuPrecision()
    {
        for (var part=0;part<PartCount;++part)
        {
            var h=part*12;
            for (var channel=0;channel<2;++channel)
            {
                var offset=(int)Values[h+(channel==0 ? 2 : 4)]*4;
                var count=(int)Values[h+(channel==0 ? 3 : 5)];
                var previous=channel==0 ? 0f : -1f;
                for (var i=0;i<count;++i)
                {
                    var current=(float)Values[offset+i*8];
                    if (current<=previous)
                        throw new InvalidDataException("Sprite frame/key times are too close for GPU float precision. Increase their spacing.");
                    previous=current;
                }
            }
        }
    }
    public static ParticleSpriteAnimation? Compile(SpriteAnimationData? animation, SpriteCompositionData? composition)
    {
        if (animation is not null && composition is not null)
            throw new InvalidDataException("Choose a sprite sequence or a sprite composition, not both.");
        if (animation is null && composition is null) return null;
        animation?.Validate();
        composition?.Validate();
        var parts = composition is null ? new[] { new SpriteCompositionTrack { Frames = animation!.Frames } }
            : composition.Tracks.Where(track => track.Visible).ToArray();
        var cells = animation?.Cells ?? composition!.Cells;
        var cellIndexes = cells.Select((cell, index) => (cell.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var compositionDuration=composition?.DurationFrames ?? 0;
        if (composition is not null)
            foreach (var track in parts)
            {
                var duration=0d;
                foreach (var frame in track.Frames) duration+=frame.DurationFrames;
                compositionDuration=Math.Max(compositionDuration,duration);
            }
        var values = Enumerable.Repeat(0d, parts.Length * 12).ToList();
        for (var part = 0; part < parts.Length; ++part)
        {
            var track = parts[part];
            var frames = composition is null
                ? track.Frames.Skip(animation!.StartFrame).Take(animation.EndFrame - animation.StartFrame + 1).ToArray()
                : track.Frames.ToArray();
            var fps = animation?.Fps ?? composition!.Fps;
            var width = animation?.TextureWidth ?? composition!.TextureWidth;
            var height = animation?.TextureHeight ?? composition!.TextureHeight;
            var frameOffset = values.Count / 4;
            var end = 0d;
            foreach (var frame in frames)
            {
                var index = cellIndexes[frame.CellId];
                var cell = cells[index];
                var next=end+frame.DurationFrames;
                if (next<=end) throw new InvalidDataException("Sprite frame duration is too small for the timeline's numeric precision.");
                end=next;
                values.AddRange([end, (double)cell.X / width, (double)cell.Y / height, (double)cell.Width / width,
                    (double)cell.Height / height, cell.Width, cell.Height, index]);
            }
            var keyOffset = values.Count / 4;
            foreach (var key in track.Keys)
                values.AddRange([key.Frame, key.X / width, key.Y / height, key.RotationDegrees * Math.PI / 180,
                    key.ScaleX, key.ScaleY, 0, 0]);
            var header = part * 12;
            var fields = new[] { fps, composition is null ? end : compositionDuration, frameOffset, frames.Length,
                keyOffset, track.Keys.Count, width, height, (animation?.Loop ?? composition!.Loop) ? 1d : 0d,
                composition is null ? 0d : 1d, 0, 0 };
            for (var i = 0; i < fields.Length; ++i) values[header + i] = fields[i];
        }
        return new(1, parts.Length, values.ToArray());
    }

    public static object? CompileNode(System.Text.Json.JsonElement animation, System.Text.Json.JsonElement composition) =>
        Compile(SpriteAnimationData.Deserialize(animation), SpriteCompositionData.Deserialize(composition));
}
