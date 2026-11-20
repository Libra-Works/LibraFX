using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    // DrawMeshInstancedIndirect is queued until camera rendering. Retain its resources
    // through frame submission, and through a GPU fence where the API supports polling.
    internal sealed class LibraGpuRetirement : MonoBehaviour
    {
        static LibraGpuRetirement host;
        internal static int PendingCount { get; private set; }

        internal static void Retire(ComputeBuffer particles, ComputeBuffer args, Mesh mesh, bool queuedDraw)
        {
            if (particles == null && args == null && mesh == null) return;
            if (!queuedDraw) { Release(particles, args, mesh); return; }
            PendingCount++;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                    UnityEditor.EditorApplication.delayCall += () => RetireInEditor(particles, args, mesh);
                return;
            }
#endif
            if (host == null)
            {
                var obj = new GameObject("Libra GPU resource retirement") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(obj);
                host = obj.AddComponent<LibraGpuRetirement>();
            }
            host.StartCoroutine(host.RetireAfterFrame(particles, args, mesh));
        }

        // A fluid owns more than two buffers plus a texture and private material.
        // Keep all of them alive together through queued compute and camera draws.
        internal static void Retire(Action release)
        {
            if (release == null) return;
            PendingCount++;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                    UnityEditor.EditorApplication.delayCall += () => RetireActionInEditor(release);
                return;
            }
#endif
            if (host == null)
            {
                var obj = new GameObject("Libra GPU resource retirement") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(obj);
                host = obj.AddComponent<LibraGpuRetirement>();
            }
            host.StartCoroutine(host.RetireActionAfterFrame(release));
        }

        IEnumerator RetireActionAfterFrame(Action release)
        {
            yield return null;
            if (SystemInfo.supportsGraphicsFence && SystemInfo.supportsAsyncCompute)
            {
                var fence = Graphics.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                while (!fence.passed) yield return null;
            }
            release();
            PendingCount--;
        }

        IEnumerator RetireAfterFrame(ComputeBuffer particles, ComputeBuffer args, Mesh mesh)
        {
            // Resume on the next frame, after queued camera draws have been submitted.
            // WaitForEndOfFrame does not resume in the Editor's batch mode.
            yield return null;
            if (SystemInfo.supportsGraphicsFence && SystemInfo.supportsAsyncCompute)
            {
                var fence = Graphics.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                while (!fence.passed) yield return null;
            }
            Finish(particles, args, mesh);
        }

#if UNITY_EDITOR
        static void RetireActionInEditor(Action release)
        {
            if (!SystemInfo.supportsGraphicsFence || !SystemInfo.supportsAsyncCompute)
            {
                release(); PendingCount--; return;
            }
            var fence = Graphics.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            UnityEditor.EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!fence.passed) return;
                UnityEditor.EditorApplication.update -= poll;
                release(); PendingCount--;
            };
            UnityEditor.EditorApplication.update += poll;
        }

        static void RetireInEditor(ComputeBuffer particles, ComputeBuffer args, Mesh mesh)
        {
            if (!SystemInfo.supportsGraphicsFence || !SystemInfo.supportsAsyncCompute) { Finish(particles, args, mesh); return; }
            var fence = Graphics.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            UnityEditor.EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!fence.passed) return;
                UnityEditor.EditorApplication.update -= poll;
                Finish(particles, args, mesh);
            };
            UnityEditor.EditorApplication.update += poll;
        }
#endif
        static void Finish(ComputeBuffer particles, ComputeBuffer args, Mesh mesh)
        {
            Release(particles, args, mesh);
            PendingCount--;
        }
        static void Release(ComputeBuffer particles, ComputeBuffer args, Mesh mesh)
        {
            particles?.Release(); args?.Release();
            if (mesh != null) { if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh); }
        }
    }
}
