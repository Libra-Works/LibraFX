using UnityEngine;

namespace LiberaFx.Unity
{
    internal static class LiberaFxBuiltinParticleTextures
    {
        private static Texture2D radialGlow;
        // One shared, generated linear-alpha texture; no project asset or import is required.
        internal static Texture2D RadialGlow
        {
            get
            {
                if (radialGlow) return radialGlow;
                const int size = 64;
                var pixels = new Color32[size * size];
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        var u = (x + .5f) / size * 2 - 1;
                        var v = (y + .5f) / size * 2 - 1;
                        pixels[y * size + x] = new Color32(255, 255, 255,
                            (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(1 - Mathf.Sqrt(u * u + v * v))));
                    }
                radialGlow = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
                {
                    name = "LIBERA FX Radial Glow", hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                };
                radialGlow.SetPixels32(pixels);
                radialGlow.Apply(false, true);
                return radialGlow;
            }
        }
    }
}
