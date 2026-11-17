using UnityEngine;

namespace LiberaFx.Unity
{
    [CreateAssetMenu(menuName = "LiberaFx/Spawnable Package", fileName = "SpawnableEffect")]
    public sealed class LiberaFxSpawnableAsset : ScriptableObject
    {
        [SerializeField] private byte[] packageBytes;

        public byte[] PackageBytes => packageBytes;

        public void SetPackageBytes(byte[] bytes)
        {
            packageBytes = bytes;
        }
    }
}
