using UnityEngine;

namespace LiberaFx.Unity
{
    // This Resources asset holds a build-time reference to the fixed, trusted
    // compute asset, so it is included even in player builds with shader stripping.
    internal sealed class LibraFluid2DResources : ScriptableObject
    {
        public ComputeShader compute;
    }
}
