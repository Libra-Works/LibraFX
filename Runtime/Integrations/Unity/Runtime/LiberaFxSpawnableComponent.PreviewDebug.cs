namespace LiberaFx.Unity
{
    public sealed partial class LiberaFxSpawnableComponent
    {
        private bool suppressPreviewDebugDraw;
        // Owner thread only. No native Stop/Force/Signal/parameter command.
        // Suppression survives subsequently spawned children and local restarts.
        public void ClearPreviewDebugDraw()
        {
            UnityInstanceSession.RequireMainThread();
            suppressPreviewDebugDraw = true;
            rootRenderScope?.ClearPreviewDebugDraw();
            foreach (var scope in instanceScopes.Values) scope.ClearPreviewDebugDraw();
        }
    }
}
