using System;
using UnityEngine;

namespace LiberaFx.Unity
{
    public sealed partial class LiberaFxSpawnableComponent
    {
        private const string CurrentCameraProperty = "__LFX_CAMERA_V1";
        private readonly double[] currentCameraLanes = new double[4];
        private Camera[] viewCameras = Array.Empty<Camera>();
        private bool usesCurrentCamera;

        [field: SerializeField]
        public Camera EffectCameraOverride { get; set; }

        private void BindCurrentCamera(NativeSpawnableHost current)
        {
            usesCurrentCamera = false;
            for (var index = 0u; ; ++index)
            {
                var result = current.GetGameIoPortName(index, out var name);
                if (result == NativeResult.InvalidArgument)
                {
                    return;
                }
                NativeInstance.Check(result);
                if (name == CurrentCameraProperty)
                {
                    usesCurrentCamera = true;
                    return;
                }
            }
        }

        private Camera ResolveEffectCamera()
        {
            if (EffectCameraOverride)
            {
                return EffectCameraOverride;
            }
            var rendering = Camera.current;
            if (rendering && rendering.cameraType == CameraType.Game)
            {
                return rendering;
            }

            var count = Camera.allCamerasCount;
            if (count == 0)
            {
                return null;
            }
            if (viewCameras.Length < count)
            {
                viewCameras = new Camera[count];
            }
            Camera selected = null;
            count = Camera.GetAllCameras(viewCameras);
            for (var index = 0; index < count; ++index)
            {
                var candidate = viewCameras[index];
                viewCameras[index] = null;
                if (!candidate || candidate.cameraType != CameraType.Game || candidate.targetTexture ||
                    candidate.targetDisplay != 0 || (candidate.cullingMask & (1 << gameObject.layer)) == 0)
                {
                    continue;
                }
                if (!selected || candidate.depth > selected.depth)
                {
                    selected = candidate;
                }
            }
            return selected;
        }

        private NativeResult PublishCurrentCamera(NativeSpawnableHost current)
        {
            if (!usesCurrentCamera)
            {
                return NativeResult.Ok;
            }
            var camera = ResolveEffectCamera();
            var position = camera ? camera.transform.position : Vector3.zero;
            currentCameraLanes[0] = position.x;
            currentCameraLanes[1] = position.y;
            currentCameraLanes[2] = position.z;
            currentCameraLanes[3] = camera ? 1 : 0;
            return current.SetGameProperty(CurrentCameraProperty, currentCameraLanes);
        }
    }
}
