namespace LiberaFx.Models.Appearance;

public enum AccentPalette
{
    SchemeDefault,
    Blue,
    Purple,
    Teal,
    Orange,
    Pink
}

public sealed record ThemeSettings(string ColorScheme, AccentPalette AccentPalette);
