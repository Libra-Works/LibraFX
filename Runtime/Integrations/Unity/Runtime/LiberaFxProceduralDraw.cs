using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LiberaFx.Unity
{
    internal sealed class LiberaFxProceduralDraw : IDisposable
    {
        private readonly Transform parent;
        private readonly LiberaFxProceduralResource model;
        private readonly LiberaFxRenderNodePayload definition;
        private readonly LiberaFxRenderSubmission submission;
        private readonly LiberaFxDrawPropertyState properties;
        private readonly Material material;
        private readonly LiberaFxRenderInputPayload position, rotation, scale, tint;
        private readonly int colorId = Shader.PropertyToID("_Color");

        internal LiberaFxProceduralDraw(Transform parent, LiberaFxRenderNodePayload draw,
            Dictionary<string, LiberaFxProceduralResource> resources, LiberaFxRenderStatisticsCounter statistics)
        {
            this.parent = parent; definition = draw;
            if (!resources.TryGetValue(draw.proceduralModel, out model) || !model.mesh)
                throw new InvalidOperationException("Procedural draw model is outside this resource scope.");
            var shader = Resources.Load<Shader>("LibraProcedural");
            if (!shader || !shader.isSupported) throw new InvalidOperationException("Procedural draw shader is unavailable.");
            try
            {
                material = new Material(shader) { name = "Libera FX Procedural Material" };
                Texture texture = Texture2D.whiteTexture;
                if (!string.IsNullOrEmpty(draw.proceduralMaterial))
                {
                    if (!resources.TryGetValue(draw.proceduralMaterial, out var producer) || producer.definition.kind != "ProceduralMaterial")
                        throw new InvalidOperationException("Procedural material is outside this resource scope.");
                    texture = producer.texture;
                    material.SetFloat("_DstBlend", producer.definition.recipe.blend == "additive" ? 1 : 10);
                    material.SetFloat("_Cull", producer.definition.recipe.doubleSided ? 0 : 2);
                    material.SetFloat("_Emission", producer.definition.recipe.emission);
                }
                properties = new LiberaFxDrawPropertyState(2);
                properties.SetTexture(Shader.PropertyToID("_MainTex"), texture);
                properties.SetVector(colorId, Vector4.one);
                submission = new LiberaFxRenderSubmission(properties, statistics) { owner = parent };
                submission.Configure(model.mesh, material, 0, parent.gameObject.layer, statistics);
                position = draw.inputs.Single(p => p.id == "position"); rotation = draw.inputs.Single(p => p.id == "rotation");
                scale = draw.inputs.Single(p => p.id == "scale"); tint = draw.inputs.Single(p => p.id == "tint");
            }
            catch { Dispose(); throw; }
        }
        internal void Update(NativeInstance.NativeSpawnableHostBase host, bool root, byte[] container, ulong instance)
        {
            var p = LiberaFxProceduralValues.Read(position, host, root, container, instance);
            var r = LiberaFxProceduralValues.Read(rotation, host, root, container, instance);
            var s = LiberaFxProceduralValues.Read(scale, host, root, container, instance);
            var color = LiberaFxProceduralValues.Read(tint, host, root, container, instance);
            properties.SetVector(colorId, color);
            submission.layer = parent.gameObject.layer;
            submission.SetMatrix(parent.localToWorldMatrix * Matrix4x4.TRS(p, Quaternion.Euler(r.x, r.y, r.z), s) * Matrix4x4.Translate(-model.pivot));
            submission.enabled = true;
        }
        public void Dispose()
        {
            submission?.Dispose();
            if (material) LiberaFxProceduralResource.Destroy(material);
        }
    }
}
