using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiberaFx.Services;

public sealed record SpriteAnimationCell(Guid Id, string Name, int X, int Y, int Width, int Height);
public sealed record SpriteAnimationFrame(Guid Id, Guid CellId, double DurationFrames);

/// <summary>
/// Engine-neutral sprite-sheet authoring data. Rectangles are pixel coordinates with a
/// top-left origin; frame timing is measured in ticks at Fps.
/// </summary>
public sealed record SpriteAnimationData
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public int Version { get; init; } = CurrentVersion;
    public int TextureWidth { get; init; }
    public int TextureHeight { get; init; }
    public List<SpriteAnimationCell> Cells { get; init; } = [];
    public List<SpriteAnimationFrame> Frames { get; init; } = [];
    public double Fps { get; init; } = 12;
    public int StartFrame { get; init; }
    public int EndFrame { get; init; }
    public bool Loop { get; init; } = true;

    public void Validate(bool requireFrames = true)
    {
        if (Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported sprite animation data version {Version}.");
        if (TextureWidth is < 1 or > 8192 || TextureHeight is < 1 or > 8192)
            throw new InvalidDataException("Sprite source image dimensions must be between 1 and 8192 pixels.");
        if (!double.IsFinite(Fps) || Fps is <= 0 or > 10000)
            throw new InvalidDataException("Sprite animation FPS must be finite and between 0 and 10000.");
        if (Cells is null || Frames is null || Cells.Count > 512 || Frames.Count > 4096)
            throw new InvalidDataException("Sprite animation exceeds the supported cell or frame count.");

        var cellIds = new HashSet<Guid>();
        var cellNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in Cells)
        {
            if (cell.Id == Guid.Empty || !cellIds.Add(cell.Id))
                throw new InvalidDataException("Sprite cells require unique, non-empty stable IDs.");
            if (string.IsNullOrWhiteSpace(cell.Name) || cell.Name.Length > 80 || !cellNames.Add(cell.Name.Trim()))
                throw new InvalidDataException("Sprite cell names must be non-empty, unique and at most 80 characters.");
            if (cell.X < 0 || cell.Y < 0 || cell.Width <= 0 || cell.Height <= 0 ||
                (long)cell.X + cell.Width > TextureWidth || (long)cell.Y + cell.Height > TextureHeight)
                throw new InvalidDataException($"Sprite cell '{cell.Name}' must be a non-empty rectangle inside the source image.");
        }

        if (Frames.Count == 0)
        {
            if (requireFrames) throw new InvalidDataException("Add at least one sprite frame.");
            return;
        }
        var frameIds = new HashSet<Guid>();
        foreach (var frame in Frames)
        {
            if (frame.Id == Guid.Empty || !frameIds.Add(frame.Id))
                throw new InvalidDataException("Sprite frames require unique, non-empty stable IDs.");
            if (!cellIds.Contains(frame.CellId))
                throw new InvalidDataException("A sprite frame refers to a missing cell.");
            if (!double.IsFinite(frame.DurationFrames) || frame.DurationFrames <= 0 || frame.DurationFrames > 1_000_000)
                throw new InvalidDataException("Sprite frame duration must be finite and positive.");
        }
        if (StartFrame < 0 || EndFrame < StartFrame || EndFrame >= Frames.Count)
            throw new InvalidDataException("Sprite playback range must select existing frames.");
    }

    public string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static SpriteAnimationData? Deserialize(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var document = JsonDocument.Parse(raw);
            value = document.RootElement.Clone();
        }
        if (value.ValueKind == JsonValueKind.Object &&
            !value.TryGetProperty("version", out _) && !value.TryGetProperty("Version", out _))
        {
            if (!value.EnumerateObject().Any()) return null;
            throw new InvalidDataException("Sprite animation data is missing its version.");
        }
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 1_048_576)
            throw new InvalidDataException("Sprite animation data must be a bounded JSON object.");
        var data = JsonSerializer.Deserialize<SpriteAnimationData>(value.GetRawText(), JsonOptions)
            ?? throw new InvalidDataException("Sprite animation data is empty.");
        data.Validate();
        return data;
    }

    public SpriteAnimationFrame FrameAt(double frameInput, double elapsedSeconds, bool playing)
    {
        Validate();
        if (double.IsFinite(frameInput) && frameInput >= 0)
        {
            var requested = Math.Floor(frameInput);
            if (Loop)
            {
                var length = EndFrame - StartFrame + 1;
                var relative = (requested - StartFrame) % length;
                if (relative < 0) relative += length;
                requested = StartFrame + relative;
            }
            else
                requested = Math.Clamp(requested, StartFrame, EndFrame);
            return Frames[(int)requested];
        }

        if (playing && (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0))
            throw new InvalidDataException("Sprite playback time is not finite and non-negative.");
        var ticks = playing ? elapsedSeconds * Fps : 0;
        if (!double.IsFinite(ticks)) throw new InvalidDataException("Sprite playback time exceeded the supported numeric range.");
        var total = 0d;
        for (var index = StartFrame; index <= EndFrame; index++) total += Frames[index].DurationFrames;
        if (Loop)
            ticks %= total;
        else
            ticks = Math.Min(ticks, Math.BitDecrement(total));
        for (var index = StartFrame; index <= EndFrame; index++)
        {
            if (ticks < Frames[index].DurationFrames) return Frames[index];
            ticks -= Frames[index].DurationFrames;
        }
        return Frames[EndFrame];
    }

    public SpriteAnimationCell CellFor(SpriteAnimationFrame frame) =>
        Cells.Single(cell => cell.Id == frame.CellId);

    public double[] UvRect(SpriteAnimationCell cell) =>
    [
        (double)cell.X / TextureWidth,
        (double)cell.Y / TextureHeight,
        (double)cell.Width / TextureWidth,
        (double)cell.Height / TextureHeight
    ];
}
