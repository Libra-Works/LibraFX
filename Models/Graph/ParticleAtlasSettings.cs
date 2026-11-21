using System.Text.Json.Serialization;
namespace LiberaFx.Models.Graph;

public sealed record ParticleAtlasSettings(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("columns")] int Columns = 1,
    [property: JsonPropertyName("rows")] int Rows = 1,
    [property: JsonPropertyName("fps")] double Fps = 0,
    [property: JsonPropertyName("loop")] bool Loop = true,
    [property: JsonPropertyName("startFrame")] int StartFrame = 0,
    [property: JsonPropertyName("overLife")] bool OverLife = false)
{
    [JsonIgnore]
    public bool IsValid => Columns is >= 1 and <= 64 && Rows is >= 1 and <= 64 &&
        Columns * Rows <= 256 && StartFrame >= 0 && StartFrame < Columns * Rows &&
        double.IsFinite(Fps) && Fps is >= 0 and <= 1000;
}
