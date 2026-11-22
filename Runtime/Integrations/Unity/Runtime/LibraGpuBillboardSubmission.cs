using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    // Retain simulation output between updates, but submit draws while the
    // camera is collecting renderers. Camera callbacks never advance compute.
    internal sealed class LibraGpuBillboardSubmission : IDisposable
    {
        private readonly Transform owner;
        private readonly Action<Camera, Vector3> draw;
        private Camera requestedCamera;
        private Vector3 emitterPosition;
        private bool ready, disposed;
        internal LibraGpuBillboardSubmission(Transform owner, Action<Camera, Vector3> draw)
        {
            this.owner = owner;
            this.draw = draw;
            Camera.onPreCull += BeforeBuiltinCamera;
            RenderPipelineManager.beginCameraRendering += BeforeScriptableCamera;
        }

        internal void Update(Vector3 position, Camera camera)
        {
            emitterPosition = position;
            requestedCamera = camera;
            ready = true;
        }

        internal void Clear()
        {
            ready = false;
            requestedCamera = null;
        }

        private void BeforeBuiltinCamera(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                Draw(camera);
            }
        }

        private void BeforeScriptableCamera(ScriptableRenderContext context, Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                Draw(camera);
            }
        }

        private void Draw(Camera camera)
        {
            if (!ready || disposed || !camera || !owner || !owner.gameObject.activeInHierarchy ||
                (camera.cullingMask & (1 << owner.gameObject.layer)) == 0 ||
                !ReferenceEquals(requestedCamera, null) && requestedCamera != camera)
            {
                return;
            }

#if UNITY_EDITOR
            var scene = owner.gameObject.scene;
            if (scene.IsValid())
            {
                if (camera.overrideSceneCullingMask != 0 &&
                    (camera.overrideSceneCullingMask &
                     UnityEditor.SceneManagement.EditorSceneManager.GetSceneCullingMask(scene)) == 0)
                {
                    return;
                }
                var cameraScene = camera.scene;
                if (camera.overrideSceneCullingMask == 0 &&
                    (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene) ||
                     cameraScene.IsValid() && UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(cameraScene)) &&
                    cameraScene != scene)
                {
                    return;
                }
            }
#endif
            draw(camera, emitterPosition);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Clear();
            Camera.onPreCull -= BeforeBuiltinCamera;
            RenderPipelineManager.beginCameraRendering -= BeforeScriptableCamera;
        }
    }
}
