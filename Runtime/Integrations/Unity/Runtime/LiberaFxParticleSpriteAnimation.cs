using System;

namespace LiberaFx.Unity
{
    internal struct ParticleSpriteSample
    {
        public double u, v, width, height, x, y, rotation, scaleX, scaleY;
        public uint cell;
    }
    [Serializable]
    internal sealed class ParticleSpriteAnimation
    {
        public int version, partCount;
        public double[] values = Array.Empty<double>();
        private static bool Integer(double value, int maximum) => value >= 0 && value <= maximum && Math.Floor(value) == value;
        internal void Validate()
        {
            if (version != 1 || partCount < 0 || partCount > 32 || values == null || values.Length % 4 != 0 ||
                values.Length < partCount * 12 || values.Length > 1200000) throw new InvalidOperationException("Invalid particle sprite timeline.");
            foreach (var value in values)
                if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e30) throw new InvalidOperationException("Non-finite particle sprite data.");
            var cursor = partCount * 3;
            for (var part = 0; part < partCount; ++part)
            {
                var h = part * 12;
                if (values[h] <= 0 || values[h] > 10000 || values[h + 1] <= 0 || values[h + 1] > 4096000000d ||
                    values[h + 2] != cursor || !Integer(values[h + 3], 4096) || values[h + 3] < 1 ||
                    !Integer(values[h + 4], values.Length / 4) || !Integer(values[h + 5], 256) ||
                    !Integer(values[h + 6], 8192) || values[h + 6] < 1 || !Integer(values[h + 7], 8192) || values[h + 7] < 1 ||
                    !Integer(values[h + 8], 1) || !Integer(values[h + 9], 1) || values[h + 10] != 0 || values[h + 11] != 0)
                    throw new InvalidOperationException("Invalid particle sprite header.");
                var frames = (int)values[h + 3]; var keys = (int)values[h + 5];
                if (frames > (values.Length / 4 - cursor) / 2) throw new InvalidOperationException("Particle sprite frames exceed data.");
                var previous = 0d;
                for (var frame = 0; frame < frames; ++frame)
                {
                    var f = (cursor + frame * 2) * 4;
                    if (values[f] <= previous || values[f] > values[h + 1] ||
                        values[f + 1] < 0 || values[f + 2] < 0 || values[f + 3] <= 0 || values[f + 4] <= 0 ||
                        values[f + 1] + values[f + 3] > 1.000001 || values[f + 2] + values[f + 4] > 1.000001 ||
                        !Integer(values[f + 5], 8192) || values[f + 5] < 1 || !Integer(values[f + 6], 8192) || values[f + 6] < 1 ||
                        !Integer(values[f + 7], 511)) throw new InvalidOperationException("Invalid particle sprite frame.");
                    previous = values[f];
                }
                cursor += frames * 2;
                if (values[h + 4] != cursor || keys > (values.Length / 4 - cursor) / 2) throw new InvalidOperationException("Invalid particle sprite key offset.");
                previous = -1;
                for (var key = 0; key < keys; ++key)
                {
                    var k = (cursor + key * 2) * 4;
                    if (values[k] <= previous || values[k] < 0 || values[k] > values[h + 1] || values[k + 6] != 0 || values[k + 7] != 0)
                        throw new InvalidOperationException("Invalid particle sprite transform key.");
                    previous = values[k];
                }
                cursor += keys * 2;
            }
            if (cursor != values.Length / 4) throw new InvalidOperationException("Unexpected particle sprite trailing data.");
        }
        internal ParticleSpriteSample Sample(int part, double age)
        {
            if (part < 0 || part >= partCount || double.IsNaN(age) || double.IsInfinity(age) || age < 0)
                throw new InvalidOperationException("Invalid particle sprite age/part.");
            var h = part * 12; var ticks = age * values[h];
            if (double.IsInfinity(ticks)) throw new InvalidOperationException("Particle sprite time overflow.");
            ticks = values[h + 8] != 0 ? ticks % values[h + 1] : Math.Min(ticks, values[h + 1]);
            var offset = (int)values[h + 2] * 4; var low = 0; var high = (int)values[h + 3] - 1;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (ticks < values[offset + middle * 8]) high = middle; else low = middle + 1;
            }
            var f = offset + low * 8;
            var result = new ParticleSpriteSample
            {
                u = values[f + 1],
                v = values[f + 2],
                width = values[f + 3],
                height = values[f + 4],
                cell = (uint)values[f + 7],
                scaleX = 1,
                scaleY = 1
            };
            if (values[h + 9] == 0) return result;
            result.scaleX = result.width; result.scaleY = result.height;
            var keys = (int)values[h + 5]; if (keys == 0) return result;
            offset = (int)values[h + 4] * 4; low = 0; high = keys - 1;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (ticks <= values[offset + middle * 8]) high = middle; else low = middle + 1;
            }
            var right = offset + low * 8; var left = low > 0 ? right - 8 : right;
            var t = values[right] > values[left] ? Math.Max(0, Math.Min(1, (ticks - values[left]) / (values[right] - values[left]))) : 0;
            result.x = Mix(left, right, 1, t); result.y = Mix(left, right, 2, t); result.rotation = Mix(left, right, 3, t);
            result.scaleX *= Mix(left, right, 4, t); result.scaleY *= Mix(left, right, 5, t);
            return result;
        }
        private double Mix(int left, int right, int i, double t) => values[left + i] + (values[right + i] - values[left + i]) * t;
        internal bool TextureMatches(int width, int height)
        {
            for (var part = 0; part < partCount; ++part)
                if (values[part * 12 + 6] != width || values[part * 12 + 7] != height) return false;
            return true;
        }
        internal void ValidateGpuPrecision()
        {
            Validate();
            for (var part = 0; part < partCount; ++part)
            {
                var h = part * 12;
                for (var channel = 0; channel < 2; ++channel)
                {
                    var offset = (int)values[h + (channel == 0 ? 2 : 4)] * 4;
                    var count = (int)values[h + (channel == 0 ? 3 : 5)];
                    var previous = channel == 0 ? 0f : -1f;
                    for (var i = 0; i < count; ++i)
                    {
                        var current = (float)values[offset + i * 8];
                        if (current <= previous) throw new InvalidOperationException("Sprite timing exceeds GPU float precision.");
                        previous = current;
                    }
                }
            }
        }
    }
}
