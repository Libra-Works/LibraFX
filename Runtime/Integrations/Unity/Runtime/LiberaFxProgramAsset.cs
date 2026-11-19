using UnityEngine;

namespace LiberaFx.Unity
{
    // Legacy raw LFXB only. New exports use LiberaFxSpawnableAsset, even for scalar roots.
    public sealed class LiberaFxProgramAsset : ScriptableObject
    {
        [SerializeField] private byte[] programBytes;

        public byte[] ProgramBytes => programBytes;

        public void SetProgramBytes(byte[] bytes)
        {
            programBytes = bytes;
        }
    }
}
