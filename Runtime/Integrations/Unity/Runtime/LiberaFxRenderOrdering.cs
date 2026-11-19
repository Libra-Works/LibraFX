using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    // Per-camera policy before batching. Backends can consume these same keys
    // without changing authored data. Graphics may still reorder the calls.
    internal static class LiberaFxRenderOrdering
    {
        internal readonly struct Entry : IComparable<Entry>
        {
            internal readonly LiberaFxRenderSubmission submission;
            internal readonly int renderQueue;
            internal readonly float cameraDepth;
            internal readonly int drawOrder;
            internal readonly ulong stableId;
            internal readonly bool hasAuthoredOrder;

            internal Entry(LiberaFxRenderSubmission submission, Camera camera)
            {
                this.submission = submission;
                renderQueue = submission.material.renderQueue;
                cameraDepth = GetCameraDepth(camera, submission.worldBounds.center);
                drawOrder = submission.drawOrder.GetValueOrDefault();
                hasAuthoredOrder = submission.drawOrder.HasValue;
                stableId = submission.stableId;
            }

            public int CompareTo(Entry other)
            {
                var result = renderQueue.CompareTo(other.renderQueue);
                if (result != 0)
                {
                    return result;
                }
                result = drawOrder.CompareTo(other.drawOrder);
                if (result != 0)
                {
                    return result;
                }
                if (renderQueue > (int)RenderQueue.AlphaTest)
                {
                    result = other.cameraDepth.CompareTo(cameraDepth);
                    if (result != 0)
                    {
                        return result;
                    }
                }
                return stableId.CompareTo(other.stableId);
            }
        }

        internal static void Sort(List<Entry> entries)
        {
            entries.Sort();
        }

        private static float GetCameraDepth(Camera camera, Vector3 center)
        {
            var mode = camera.transparencySortMode;
            if (mode == TransparencySortMode.Default)
            {
                mode = GraphicsSettings.transparencySortMode;
                if (mode == TransparencySortMode.CustomAxis)
                {
                    return Vector3.Dot(center - camera.transform.position, GraphicsSettings.transparencySortAxis);
                }
            }
            var offset = center - camera.transform.position;
            if (mode == TransparencySortMode.CustomAxis)
            {
                return Vector3.Dot(offset, camera.transparencySortAxis);
            }
            if (mode == TransparencySortMode.Orthographic ||
                (mode == TransparencySortMode.Default && camera.orthographic))
            {
                return Vector3.Dot(offset, camera.transform.forward);
            }
            return offset.magnitude;
        }
    }
}
