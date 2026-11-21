using UnityEngine;
namespace LiberaFx.Unity
{
    public partial struct CoordinateBasis
    {
        public Matrix4x4 EffectToHostMatrix()
        {
            var identity = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            var values = Convert(SpatialValueKind.CanonicalDrawTransform, true, identity);
            var matrix = new Matrix4x4();
            for (var row = 0; row < 4; row++)
                for (var column = 0; column < 4; column++) matrix[row, column] = (float)values[row * 4 + column];
            return matrix;
        }
    }
}
