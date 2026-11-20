#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiberaFx.Unity
{
    public static class LiberaFxProceduralCacheVerification
    {
        public static void Run()
        {
            var scope = new Dictionary<string, LiberaFxProceduralResource>();
            LiberaFxProceduralResource Make(float red, bool animated = false)
            {
                var id = Guid.NewGuid().ToString();
                var values = new float[] { red, .5f, .25f, 1, 0, 0, 0, 0, 0, 0, 0, 0 };
                var inputs = new LiberaFxRenderInputPayload[3];
                for (var block = 0; block < 3; ++block)
                {
                    var defaults = new double[4];
                    for (var c = 0; c < 4; ++c) defaults[c] = values[block * 4 + c];
                    inputs[block] = new LiberaFxRenderInputPayload { id = "stage_" + Guid.Parse(id).ToString("N") + "_" + block,
                        width = 4, defaults = defaults };
                }
                return LiberaFxProceduralResource.Acquire(new LiberaFxProceduralResourcePayload {
                    nodeId = Guid.NewGuid().ToString(), kind = "ProceduralTexture", inputs = inputs,
                    recipe = new LiberaFxProceduralRecipePayload { resolution = 16, outputRegister = 1,
                        operations = new[] { new LiberaFxProceduralPixelPayload { id = id, kind = animated ? 2u : 1u, values = values } } }
                }, scope, null);
            }
            var first = Make(1); var second = Make(1); var different = Make(.75f);
            var dynamicA = Make(1, true); var dynamicB = Make(1, true);
            try
            {
                if (!ReferenceEquals(first, second) || first.texture != second.texture || ReferenceEquals(first, different) || ReferenceEquals(dynamicA, dynamicB))
                    throw new InvalidOperationException("Static cross-effect GPU sharing or dynamic isolation failed.");
                first.Update(null, true, null, 0, 0);
                var revision = first.revision;
                second.Update(null, true, null, 0, 20);
                if (revision != second.revision) throw new InvalidOperationException("A shared static output was dispatched again.");
                first.Dispose(); first = null;
                if (!second.texture || !second.texture.IsCreated()) throw new InvalidOperationException("Releasing one effect destroyed another effect's shared texture.");
            }
            finally
            {
                first?.Dispose(); second.Dispose(); different.Dispose(); dynamicA.Dispose(); dynamicB.Dispose();
            }
            if (second.texture) throw new InvalidOperationException("The final lease did not release the GPU output.");
            var retainedTexture = Make(1);
            scope.Add("texture", retainedTexture);
            var stageId = Guid.NewGuid().ToString();
            var materialInputs = new LiberaFxRenderInputPayload[3];
            for (var block = 0; block < 3; ++block) materialInputs[block] = new LiberaFxRenderInputPayload {
                id = "stage_" + Guid.Parse(stageId).ToString("N") + "_" + block, width = 4, defaults = new double[4] };
            var material = LiberaFxProceduralResource.Acquire(new LiberaFxProceduralResourcePayload {
                nodeId = Guid.NewGuid().ToString(), kind = "ProceduralMaterial", inputs = materialInputs,
                textures = new[] { new LiberaFxProceduralTextureReference { slot = 0, producerNodeId = "texture" } },
                recipe = new LiberaFxProceduralRecipePayload { resolution = 16, outputRegister = 1,
                    operations = new[] { new LiberaFxProceduralPixelPayload { id = stageId, kind = 30, values = new float[12] } } }
            }, scope, null);
            retainedTexture.Dispose();
            try
            {
                if (!retainedTexture.texture || !retainedTexture.texture.IsCreated())
                    throw new InvalidOperationException("A material did not retain its sampled procedural texture.");
            }
            finally { material.Dispose(); }
            if (retainedTexture.texture) throw new InvalidOperationException("A dependency lease leaked after the final material release.");
            Debug.Log("LIBERAFX PROCEDURAL STATIC CACHE PASS: same GPU output, different settings, dynamic isolation, one dispatch, final release.");
        }
    }
}
#endif
