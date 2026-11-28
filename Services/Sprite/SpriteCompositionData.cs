using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiberaFx.Services.Sprite;

public sealed record SpriteTransformKey(Guid Id, double Frame, double X, double Y,
    double RotationDegrees, double ScaleX, double ScaleY);

public readonly record struct SpritePose(double X, double Y, double RotationDegrees, double ScaleX, double ScaleY);

public sealed record SpriteCompositionTrack
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "Part";
    public bool Visible { get; init; } = true;
    public List<SpriteAnimationFrame> Frames { get; init; } = [];
    public List<SpriteTransformKey> Keys { get; init; } = [];

    public SpritePose PoseAt(double frame)
    {
        if (Keys.Count == 0) return new(0, 0, 0, 1, 1);
        static SpritePose Pose(SpriteTransformKey key) =>
            new(key.X, key.Y, key.RotationDegrees, key.ScaleX, key.ScaleY);
        if (double.IsNaN(frame) || frame <= Keys[0].Frame) return Pose(Keys[0]);
        for (var i = 1; i < Keys.Count; i++)
        {
            var right = Keys[i];
            if (frame > right.Frame) continue;
            var left = Keys[i - 1];
            var t = (frame - left.Frame) / (right.Frame - left.Frame);
            double Mix(double a, double b) => a + (b - a) * t;
            return new(Mix(left.X, right.X), Mix(left.Y, right.Y), Mix(left.RotationDegrees, right.RotationDegrees),
                Mix(left.ScaleX, right.ScaleX), Mix(left.ScaleY, right.ScaleY));
        }
        return Pose(Keys[^1]);
    }
}

/// <summary>Top-left atlas rectangles and centered part pivots, with X right and Y up.</summary>
public sealed record SpriteCompositionData
{
    private const int MaxJsonLength = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public int Version { get; init; } = 1;
    public int TextureWidth { get; init; }
    public int TextureHeight { get; init; }
    public double Fps { get; init; } = 12;
    public bool Loop { get; init; } = true;
    public List<SpriteAnimationCell> Cells { get; init; } = [];
    public List<SpriteCompositionTrack> Tracks { get; init; } = [];

    [JsonIgnore]
    public double DurationFrames => Tracks.Aggregate(1d, (duration, track) =>
        Math.Max(duration, Math.Max(track.Frames.Sum(frame => frame.DurationFrames),
            track.Keys.Count == 0 ? 0 : track.Keys[^1].Frame)));

    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException($"Unsupported sprite composition version {Version}.");
        if (TextureWidth is < 1 or > 8192 || TextureHeight is < 1 or > 8192)
            throw new InvalidDataException("Sprite source dimensions must be between 1 and 8192 pixels.");
        if (!double.IsFinite(Fps) || Fps is <= 0 or > 10000)
            throw new InvalidDataException("Sprite composition FPS must be finite, positive and at most 10000.");
        if (Tracks is null || Cells is null || Tracks.Count is < 1 or > 32 || Cells.Count > 512)
            throw new InvalidDataException("A sprite composition requires 1–32 parts and at most 512 cells.");

        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool NameIsValid(string? name, HashSet<string> names) =>
            !string.IsNullOrWhiteSpace(name) && name.Length <= 80 && names.Add(name.Trim());
        bool AddId(Guid id) => id != Guid.Empty && ids.Add(id);
        foreach (var cell in Cells)
            if (cell is null || !AddId(cell.Id) || !NameIsValid(cell.Name, names) ||
                cell.X < 0 || cell.Y < 0 || cell.Width <= 0 || cell.Height <= 0 ||
                (long)cell.X + cell.Width > TextureWidth || (long)cell.Y + cell.Height > TextureHeight)
                throw new InvalidDataException("Sprite cells require unique IDs and names and must be within the source image.");
        var cellIds = ids.ToHashSet();
        names.Clear();
        foreach (var track in Tracks)
        {
            if (track is null || !AddId(track.Id) || !NameIsValid(track.Name, names))
                throw new InvalidDataException("Sprite parts require unique IDs and names.");
            if (track.Frames is null || track.Keys is null || track.Frames.Count > 4096 || track.Keys.Count > 256)
                throw new InvalidDataException("A sprite part supports up to 4096 frames and 256 pose keys.");
            if (track.Visible && track.Frames.Count == 0)
                throw new InvalidDataException($"Visible sprite part '{track.Name}' requires at least one frame.");
            foreach (var frame in track.Frames)
                if (frame is null || !AddId(frame.Id) || !cellIds.Contains(frame.CellId) ||
                    !double.IsFinite(frame.DurationFrames) || frame.DurationFrames is <= 0 or > 1_000_000)
                    throw new InvalidDataException($"Part '{track.Name}' has an invalid frame ID, cell or duration.");
            var previous = -1d;
            foreach (var key in track.Keys)
            {
                if (key is null || !AddId(key.Id) || !double.IsFinite(key.Frame) ||
                    key.Frame <= previous || key.Frame is < 0 or > 4_096_000_000d ||
                    !Bounded(key.X) || !Bounded(key.Y) || !Bounded(key.RotationDegrees) ||
                    !ValidScale(key.ScaleX) || !ValidScale(key.ScaleY))
                    throw new InvalidDataException($"Part '{track.Name}' requires sorted, unique pose keys with bounded finite values.");
                previous = key.Frame;
            }
        }
    }

    private static bool Bounded(double value) => double.IsFinite(value) && Math.Abs(value) <= 1_000_000;
    private static bool ValidScale(double value) => double.IsFinite(value) && value != 0 && Math.Abs(value) <= 10_000;

    public string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static SpriteCompositionData? Deserialize(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Length > MaxJsonLength) throw new InvalidDataException("Sprite composition data exceeds its size limit.");
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !(document.RootElement.TryGetProperty("version", out _) || document.RootElement.TryGetProperty("Version", out _)))
            throw new InvalidDataException("Sprite composition data requires an explicit version.");
        var data = document.RootElement.Deserialize<SpriteCompositionData>(JsonOptions)
            ?? throw new InvalidDataException("Sprite composition data is empty.");
        data.Validate();
        return data;
    }

    public double SampleFrame(double seconds)
    {
        var ticks = seconds * Fps;
        if (!double.IsFinite(ticks) || ticks < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Sprite playback time must be finite and non-negative.");
        var duration = DurationFrames;
        return Loop ? ticks % duration : Math.Min(ticks, duration);
    }
}
