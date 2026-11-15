using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    // Native evaluation knows no Mesh, Material, texture pointer or Unity API.
    // This owner-thread adapter pulls numeric ports, then touches Unity objects.
    [DisallowMultipleComponent]
    public sealed class LiberaFxRenderer : MonoBehaviour
    {
        [SerializeField] private TextAsset compiledProgram;
        [SerializeField] private Mesh modelMesh;
        [SerializeField] private Material material;
        [SerializeField] private Texture spriteTexture;
        [SerializeField] private bool sprite;
        [SerializeField] private bool dynamicQuad;
        [SerializeField] private int atlasColumns = 4, atlasRows = 4;
        [SerializeField] private float framesPerSecond = 8;
        [SerializeField] private string positionNode = LiberaFxSampleProgram.MotionId;
        [SerializeField] private string colorNode = LiberaFxSampleProgram.ColorId;
        [SerializeField] private string atlasFrameNode = LiberaFxSampleProgram.TimeId;
        [SerializeField] private string deformationNode = LiberaFxSampleProgram.MotionId;
        [SerializeField] private string defaultNode = LiberaFxSampleProgram.GainId;
        [SerializeField] private int defaultInputSlot = 1, defaultComponent = 0;
        [SerializeField] private float defaultGain = 0.2f;

        private NativeInstance instance;
        private uint defaultBinding;
        private uint[] bindings;
        private NativeValue[] values;
        private Mesh ownedMesh;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private MaterialPropertyBlock properties;
        private Vertex[] vertices;
        private double elapsed;
        private bool sample;
        private readonly int colorProperty = Shader.PropertyToID("_Color");
        private readonly int textureProperty = Shader.PropertyToID("_MainTex");
        private readonly int atlasProperty = Shader.PropertyToID("_AtlasRect");

        [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 20)]
        private struct Vertex
        {
            public Vector3 Position;
            public Vector2 Uv;
            public Vertex(float x, float y, float u, float v)
            {
                Position = new Vector3(x, y, 0);
                Uv = new Vector2(u, v);
            }
        }

        // Call on a newly created inactive object before SetActive(true).
        // Real .lfxb TextAssets use the exact same bind/update/read path.
        public void SetupCubeSample(Mesh cubeMesh, Material sharedMaterial)
        {
            compiledProgram = null;
            modelMesh = cubeMesh;
            material = sharedMaterial;
            sprite = false;
            sample = true;
        }
        public void SetupAtlasSample(Texture atlas, Material sharedMaterial, int columns, int rows, bool deform = false)
        {
            compiledProgram = null;
            spriteTexture = atlas;
            material = sharedMaterial;
            atlasColumns = columns;
            atlasRows = rows;
            sprite = true;
            dynamicQuad = deform;
            sample = true;
        }
        public NativeResult SetDefaultGain(double gain) =>
            instance == null || defaultBinding == 0
                ? NativeResult.NotReady : instance.SetParameter(defaultBinding, gain);

        private void OnEnable()
        {
            try
            {
                if (!material || (!sprite && !modelMesh))
                    throw new InvalidOperationException("Assign a shared material and a model mesh (or choose sprite).");
                if (sprite && (!spriteTexture || atlasColumns <= 0 || atlasRows <= 0))
                    throw new InvalidOperationException("Assign a sprite atlas and a positive atlas grid.");
                var bytes = compiledProgram ? compiledProgram.bytes : LiberaFxSampleProgram.Create();
                sample = !compiledProgram;
                using (var program = new NativeProgram(bytes))
                    instance = program.CreateInstance(); // Native instance retains immutable program.
                bindings = new uint[dynamicQuad && sprite ? 4 : 3];
                bindings[0] = instance.BindValue(positionNode);
                bindings[1] = instance.BindValue(colorNode);
                bindings[2] = instance.BindValue(atlasFrameNode);
                if (bindings.Length == 4) bindings[3] = instance.BindValue(deformationNode);
                values = new NativeValue[bindings.Length];
                if (!string.IsNullOrWhiteSpace(defaultNode))
                {
                    defaultBinding = instance.BindParameter(defaultNode, (uint)defaultInputSlot, (uint)defaultComponent);
                    NativeInstance.Check(instance.SetParameter(defaultBinding, defaultGain));
                }
                meshFilter = GetComponent<MeshFilter>();
                if (!meshFilter) meshFilter = gameObject.AddComponent<MeshFilter>();
                meshRenderer = GetComponent<MeshRenderer>();
                if (!meshRenderer) meshRenderer = gameObject.AddComponent<MeshRenderer>();
                properties = new MaterialPropertyBlock();
                meshRenderer.sharedMaterial = material; // Never Renderer.material.
                if (sprite)
                {
                    CreateQuad();
                    meshFilter.sharedMesh = ownedMesh;
                    properties.SetTexture(textureProperty, spriteTexture);
                }
                else meshFilter.sharedMesh = modelMesh;
                elapsed = 0;
                meshRenderer.enabled = true;
            }
            catch (Exception error)
            {
                Debug.LogException(error, this);
                enabled = false;
            }
        }
        private void CreateQuad()
        {
            vertices = new[] {
                new Vertex(-0.5f, -0.5f, 0, 0), new Vertex(0.5f, -0.5f, 1, 0),
                new Vertex(0.5f, 0.5f, 1, 1), new Vertex(-0.5f, 0.5f, 0, 1)
            };
            ownedMesh = new Mesh { name = "LiberaFx owned quad" };
            if (dynamicQuad) ownedMesh.MarkDynamic();
            ownedMesh.SetVertexBufferParams(4,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
            ownedMesh.SetVertexBufferData(vertices, 0, 0, 4);
            ownedMesh.SetIndices(new[] { 0, 2, 1, 0, 3, 2 }, MeshTopology.Triangles, 0);
            ownedMesh.bounds = new Bounds(Vector3.zero, new Vector3(1, 1, 0.01f));
        }
        private void Update()
        {
            if (instance == null) return;
            elapsed += Time.deltaTime;
            var result = instance.Evaluate(sample ? elapsed % 2.0 : elapsed, bindings, values);
            if (result != NativeResult.Ok)
            {
                Debug.LogError("LiberaFx evaluation failed: " + result, this);
                enabled = false;
                return;
            }
            for (var i = 0; i < values.Length; ++i)
            {
                var value = values[i];
                if (!UnityFloat(value.X) || !UnityFloat(value.Y) ||
                    !UnityFloat(value.Z) || !UnityFloat(value.W))
                {
                    Debug.LogError("LiberaFx output cannot be represented as finite Unity floats.", this);
                    enabled = false;
                    return;
                }
            }
            var position = values[0];
            transform.localPosition = new Vector3((float)position.X,
                position.ComponentCount >= 2 ? (float)position.Y : 0,
                position.ComponentCount >= 3 ? (float)position.Z : 0);
            var color = values[1];
            if (color.ComponentCount != 4)
            {
                Debug.LogError("The color port must output four components.", this);
                enabled = false;
                return;
            }
            properties.SetColor(colorProperty, new Color((float)color.X, (float)color.Y, (float)color.Z, (float)color.W));
            if (sprite)
            {
                result = NativeInstance.SampleAtlas(values[2].X * framesPerSecond,
                    (uint)atlasColumns, (uint)atlasRows, out var atlas);
                if (result != NativeResult.Ok) { enabled = false; return; }
                // Core UVs are top-left; Unity texture UVs are bottom-left.
                properties.SetVector(atlasProperty, new Vector4(atlas.U, 1 - atlas.V - atlas.Height, atlas.Width, atlas.Height));
                if (dynamicQuad)
                {
                    // Same allocated four-vertex capacity for every frame. Mesh
                    // upload occurs here on Unity's main thread, not in native jobs.
                    var amplitude = (float)values[3].X;
                    vertices[2].Position.y = vertices[3].Position.y = 0.5f + amplitude;
                    ownedMesh.SetVertexBufferData(vertices, 0, 0, 4, 0,
                        MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                    var height = 1 + Mathf.Abs(amplitude);
                    ownedMesh.bounds = new Bounds(new Vector3(0, amplitude * 0.5f, 0), new Vector3(1, height, 0.01f));
                }
            }
            else properties.SetVector(atlasProperty, new Vector4(0, 0, 1, 1));
            meshRenderer.SetPropertyBlock(properties);
        }
        private static bool UnityFloat(double value) =>
            !double.IsNaN(value) && value >= -float.MaxValue && value <= float.MaxValue;
        private void OnDisable()
        {
            instance?.Dispose();
            instance = null;
            defaultBinding = 0;
            if (meshRenderer) { meshRenderer.SetPropertyBlock(null); meshRenderer.enabled = false; }
            if (ownedMesh)
            {
                if (meshFilter && meshFilter.sharedMesh == ownedMesh) meshFilter.sharedMesh = null;
                Destroy(ownedMesh);
                ownedMesh = null;
            }
            bindings = null;
            values = null;
            vertices = null;
        }
    }
}
