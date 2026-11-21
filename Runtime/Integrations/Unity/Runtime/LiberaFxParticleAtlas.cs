using System;
namespace LiberaFx.Unity
{
    [Serializable] public sealed class ParticleAtlas
    {
        public bool enabled, loop = true, overLife;
        public int columns = 1, rows = 1, startFrame;
        public double fps;
        public bool IsValid => columns >= 1 && columns <= 64 && rows >= 1 && rows <= 64 &&
            columns*rows <= 256 && startFrame >= 0 && startFrame < columns*rows &&
            !double.IsNaN(fps) && !double.IsInfinity(fps) && fps >= 0 && fps <= 1000;
        public int Frame(double age, double life)
        {
            if (!enabled || !IsValid || double.IsNaN(age) || double.IsInfinity(age) || double.IsNaN(life) || double.IsInfinity(life)) return 0;
            int count = columns*rows; age = Math.Max(0,age);
            if (overLife) return Math.Min(count-1,startFrame+(int)((life>0?Math.Min(1,age/life):0)*(count-startFrame)));
            if (fps == 0) return startFrame;
            if (loop) return (startFrame+(int)Math.Floor(age % (count/fps)*fps)) % count;
            return age >= (count-1-startFrame)/fps ? count-1 : startFrame+(int)Math.Floor(age*fps);
        }
    }
}
