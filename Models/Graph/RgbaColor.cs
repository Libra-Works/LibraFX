using Avalonia.Media;

namespace LiberaFx.Models.Graph;

public readonly record struct RgbaColor(double R, double G, double B, double A)
{
    public bool IsValid => double.IsFinite(R) && R >= 0 && double.IsFinite(G) && G >= 0 &&
                           double.IsFinite(B) && B >= 0 && double.IsFinite(A) && A is >= 0 and <= 1;
    public double Intensity => Math.Max(1, Math.Max(R, Math.Max(G, B)));
    public Color Preview => Color.FromArgb(ToByte(A), ToByte(R / Intensity), ToByte(G / Intensity), ToByte(B / Intensity));
    public double[] Components => [R, G, B, A];

    public static RgbaColor FromColor(Color color, double intensity = 1) =>
        new(color.R / 255.0 * intensity, color.G / 255.0 * intensity,
            color.B / 255.0 * intensity, color.A / 255.0);

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
