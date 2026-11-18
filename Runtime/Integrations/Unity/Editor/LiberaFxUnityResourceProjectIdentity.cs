#if UNITY_EDITOR
using UnityEngine;

namespace LiberaFx.Unity
{
    // This asset is intentionally project-owned and source-controllable. The separate
    // Library marker makes a copied project require an explicit keep-or-fork decision.
    public sealed class LiberaFxUnityResourceProjectIdentity : ScriptableObject
    {
        [SerializeField] private string projectId = "";
        public string ProjectId => projectId;

        internal void SetProjectId(string value)
        {
            projectId = value;
        }
    }
}
#endif
