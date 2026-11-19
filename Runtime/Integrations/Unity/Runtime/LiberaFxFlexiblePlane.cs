using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    [Serializable] internal sealed class LiberaFxFlexiblePlanePayload
    {
        public int version;
        public string sourceKind, sourceNodeId, facing, uvMode;
        public LiberaFxFlexiblePointPayload[] points;
        public int capacity;
        public double maxAge, repeatLength;
        public double[] up;
    }

    [Serializable] internal sealed class LiberaFxFlexiblePointPayload
    {
        public double[] position, color;
        public double width;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LiberaFxFlexiblePoint
    {
        public float x, y, z, width, r, g, b, a;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LiberaFxFlexibleTimedPoint
    {
        public double time;
        public LiberaFxFlexiblePoint point;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LiberaFxFlexibleHistory
    {
        public uint count, reserved;
        public double lastTime;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LiberaFxFlexibleOptions
    {
        public uint facing, uvMode;
        public float upX, upY, upZ, cameraX, cameraY, cameraZ, repeatLength;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LiberaFxFlexibleCounts
    {
        public uint vertices, indices;
    }

    internal static class LiberaFxFlexibleNative
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string Library = "__Internal";
#else
        private const string Library = "liberafx_native";
#endif
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_flexible_plane_generate(
            [In] LiberaFxFlexiblePoint[] points, uint pointCount, ref LiberaFxFlexibleOptions options,
            [Out] SpriteVertex[] vertices, uint vertexCapacity, [Out] uint[] indices, uint indexCapacity,
            out LiberaFxFlexibleCounts counts);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_flexible_history_update(ref LiberaFxFlexibleHistory history,
            [In, Out] LiberaFxFlexibleTimedPoint[] storage, uint capacity, double time, double maxAge,
            ref LiberaFxFlexiblePoint point);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_flexible_history_copy(ref LiberaFxFlexibleHistory history,
            [In] LiberaFxFlexibleTimedPoint[] storage, uint capacity, [Out] LiberaFxFlexiblePoint[] points,
            uint pointCapacity, out uint count);
    }

    /// <summary>Immutable definition produced before any GameObject is created.</summary>
    internal sealed class LiberaFxPreparedFlexiblePlane
    {
        internal LiberaFxFlexiblePlanePayload payload;
        internal LiberaFxRenderInputPayload[] inputs;
        internal Dictionary<string, LiberaFxRenderInputPayload> byId;
        internal LiberaFxFlexiblePoint[] points;
        internal Texture texture;
        internal Material material;
        internal string textureProperty;
        internal Camera camera;
        internal string historySignature;

        private static float Finite(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue)
            {
                throw new InvalidOperationException("Flexible Plane contains a non-finite or out-of-range value.");
            }
            return (float)value;
        }

        private static void Vector(double[] value, int length)
        {
            if (value == null || value.Length != length)
            {
                throw new InvalidOperationException("Flexible Plane vector has an invalid width.");
            }
            foreach (var lane in value)
            {
                Finite(lane);
            }
        }

        internal static LiberaFxPreparedFlexiblePlane Prepare(LiberaFxRenderNodePayload node,
            LiberaFxFlexiblePlanePayload plane, Texture texture, Material material,
            string textureProperty, Camera camera)
        {
            if (node == null || node.kind != "flexiblePlane" || plane == null || plane.version != 1 ||
                !Guid.TryParse(node.nodeId, out var drawId) || drawId == Guid.Empty ||
                !Guid.TryParse(plane.sourceNodeId, out var sourceId) || sourceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(node.assetPath) || !texture || !material ||
                string.IsNullOrWhiteSpace(textureProperty) || !material.HasProperty(textureProperty) ||
                node.billboard || (node.materials != null && node.materials.Length != 0) ||
                (node.textures != null && node.textures.Length != 0))
            {
                throw new InvalidOperationException("Invalid Flexible Plane draw, texture, or material binding.");
            }
            var history = plane.sourceKind == "history";
            if ((!history && plane.sourceKind != "list") ||
                (plane.facing != "camera" && plane.facing != "fixedUp") ||
                (plane.uvMode != "stretch" && plane.uvMode != "repeat") ||
                (plane.facing == "camera" && !camera) || Finite(plane.repeatLength) <= 0)
            {
                throw new InvalidOperationException("Invalid Flexible Plane source, facing, camera or UV settings.");
            }
            Vector(plane.up, 3);
            if (history && (plane.capacity < 2 || plane.capacity > 4096 ||
                Finite(plane.maxAge) <= 0 || (plane.points != null && plane.points.Length != 0)))
            {
                throw new InvalidOperationException("Invalid Flexible Plane history settings.");
            }
            if (!history && (plane.points == null || plane.points.Length < 2 || plane.points.Length > 64 ||
                plane.capacity != 0 || plane.maxAge != 0))
            {
                throw new InvalidOperationException("Invalid Flexible Plane point list.");
            }
            var prepared = new LiberaFxPreparedFlexiblePlane
            {
                payload = plane, inputs = node.inputs, texture = texture, material = material,
                textureProperty = textureProperty, camera = camera,
                points = new LiberaFxFlexiblePoint[history ? 0 : plane.points.Length],
                byId = new Dictionary<string, LiberaFxRenderInputPayload>(StringComparer.Ordinal)
            };
            for (var index = 0; index < prepared.points.Length; index++)
            {
                var p = plane.points[index];
                if (p == null)
                {
                    throw new InvalidOperationException("Invalid Flexible Plane point.");
                }
                Vector(p.position, 3);
                Vector(p.color, 4);
                var width = Finite(p.width);
                if (width < 0)
                {
                    throw new InvalidOperationException("Flexible Plane point width is negative.");
                }
                prepared.points[index] = new LiberaFxFlexiblePoint
                {
                    x = Finite(p.position[0]), y = Finite(p.position[1]), z = Finite(p.position[2]),
                    width = width, r = Finite(p.color[0]), g = Finite(p.color[1]),
                    b = Finite(p.color[2]), a = Finite(p.color[3])
                };
            }
            if (node.inputs == null)
            {
                throw new InvalidOperationException("Flexible Plane inputs are missing.");
            }
            foreach (var input in node.inputs)
            {
                if (input == null || input.id == null || prepared.byId.ContainsKey(input.id) ||
                    input.materialParameter != null || input.width < 1 || input.width > 4 ||
                    input.sourceComponent < 0 || input.sourceComponent + input.width > 4)
                {
                    throw new InvalidOperationException("Invalid Flexible Plane input.");
                }
                Vector(input.defaults, input.width);
                if (!string.IsNullOrEmpty(input.sourceNodeId))
                {
                    if (!Guid.TryParse(input.sourceNodeId, out var id) || id == Guid.Empty)
                    {
                        throw new InvalidOperationException("Invalid Flexible Plane input source.");
                    }
                    input.sourceNodeBytes = new NativeSignal(input.sourceNodeId).Id;
                }
                prepared.byId.Add(input.id, input);
            }
            prepared.Require("width", 1);
            prepared.Require("tint", 4);
            if (prepared.byId["width"].defaults[0] < 0)
            {
                throw new InvalidOperationException("Flexible Plane default width multiplier is negative.");
            }
            if (history)
            {
                prepared.Require("position", 3);
                prepared.Require("pointWidth", 1);
                prepared.Require("pointColor", 4);
                if (prepared.byId["pointWidth"].defaults[0] < 0)
                {
                    throw new InvalidOperationException("Flexible Plane default point width is negative.");
                }
                if (prepared.byId.Count != 5)
                {
                    throw new InvalidOperationException("Unexpected Flexible Plane history inputs.");
                }
                var signature = new System.Text.StringBuilder();
                signature.Append(plane.capacity).Append('|').Append(plane.maxAge.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                foreach (var id in new[] { "position", "pointWidth", "pointColor" })
                {
                    var input = prepared.byId[id];
                    signature.Append('|').Append(input.sourceNodeId).Append(':').Append(input.sourceComponent);
                    foreach (var lane in input.defaults)
                    {
                        signature.Append(':').Append(lane.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
                prepared.historySignature = signature.ToString();
            }
            else
            {
                var count = Math.Min(4, prepared.points.Length);
                for (var index = 0; index < count; index++)
                {
                    prepared.Require("position" + index, 3);
                }
                if (prepared.byId.Count != count + 2)
                {
                    throw new InvalidOperationException("Unexpected Flexible Plane list inputs.");
                }
            }
            return prepared;
        }

        private void Require(string id, int width)
        {
            if (!byId.TryGetValue(id, out var input) || input.width != width)
            {
                throw new InvalidOperationException("Flexible Plane is missing input " + id + ".");
            }
        }
    }

    /// <summary>One instance per root/child scope; share across draws in that scope only.</summary>
    internal sealed class LiberaFxFlexiblePlaneHistoryScope
    {
        private sealed class Entry
        {
            internal string signature;
            internal LiberaFxFlexibleHistory state;
            internal LiberaFxFlexibleTimedPoint[] storage;
            internal double sampleTime;
            internal LiberaFxFlexiblePoint sample;
            internal bool sampled;
        }
        private readonly Dictionary<Guid, Entry> entries = new Dictionary<Guid, Entry>();

        internal uint Sample(LiberaFxPreparedFlexiblePlane definition, double time,
            LiberaFxFlexiblePoint sample, LiberaFxFlexiblePoint[] output)
        {
            var key = Guid.Parse(definition.payload.sourceNodeId);
            if (!entries.TryGetValue(key, out var entry))
            {
                entry = new Entry
                {
                    signature = definition.historySignature,
                    storage = new LiberaFxFlexibleTimedPoint[definition.payload.capacity]
                };
                entries.Add(key, entry);
            }
            if (entry.signature != definition.historySignature)
            {
                throw new InvalidOperationException("Shared Flexible Plane history has conflicting definitions.");
            }
            if (entry.sampled)
            {
                var previous = entry.sample;
                if (entry.sampleTime != time || previous.x != sample.x || previous.y != sample.y ||
                    previous.z != sample.z || previous.width != sample.width || previous.r != sample.r ||
                    previous.g != sample.g || previous.b != sample.b || previous.a != sample.a)
                {
                    throw new InvalidOperationException("Shared Flexible Plane source changed within one scope update.");
                }
            }
            else
            {
                var result = LiberaFxFlexibleNative.libra_flexible_history_update(ref entry.state,
                    entry.storage, (uint)entry.storage.Length, time, definition.payload.maxAge, ref sample);
                if (result != NativeResult.Ok)
                {
                    throw new InvalidOperationException("Flexible Plane history update failed: " + result);
                }
                entry.sampled = true;
                entry.sampleTime = time;
                entry.sample = sample;
            }
            var copyResult = LiberaFxFlexibleNative.libra_flexible_history_copy(ref entry.state, entry.storage,
                (uint)entry.storage.Length, output, (uint)output.Length, out var count);
            if (copyResult != NativeResult.Ok)
            {
                throw new InvalidOperationException("Flexible Plane history copy failed: " + copyResult);
            }
            return count;
        }

        internal void BeginUpdate()
        {
            foreach (var entry in entries.Values)
            {
                entry.sampled = false;
            }
        }

        internal void Reset()
        {
            entries.Clear();
        }
    }

    /// <summary>Owner-thread-only dynamic geometry submitted without a renderer GameObject.</summary>
    internal sealed class LiberaFxFlexiblePlaneRenderer : IDisposable
    {
        private readonly LiberaFxPreparedFlexiblePlane definition;
        private readonly LiberaFxFlexiblePlaneHistoryScope history;
        private readonly Transform parent;
        private readonly Mesh mesh;
        private readonly LiberaFxDrawPropertyState propertyState;
        private readonly LiberaFxRenderSubmission submission;
        private readonly LiberaFxRenderStatisticsCounter statistics;
        private readonly LiberaFxFlexiblePoint[] points;
        private readonly SpriteVertex[] nativeVertices;
        private readonly uint[] nativeIndices;
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<Color> colors = new List<Color>();
        private readonly List<int> indices = new List<int>();
        private readonly int ownerThread;
        private bool disposed;

        internal LiberaFxFlexiblePlaneRenderer(Transform parent, LiberaFxPreparedFlexiblePlane definition,
            LiberaFxFlexiblePlaneHistoryScope history,
            LiberaFxRenderStatisticsCounter statistics = null)
        {
            if (!parent || definition == null || history == null)
            {
                throw new ArgumentNullException("Flexible Plane requires a parent, definition and history scope.");
            }
            this.definition = definition;
            this.history = history;
            this.parent = parent;
            this.statistics = statistics;
            ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var capacity = definition.payload.sourceKind == "history" ? definition.payload.capacity : definition.points.Length;
            points = new LiberaFxFlexiblePoint[capacity];
            nativeVertices = new SpriteVertex[capacity * 2];
            nativeIndices = new uint[(capacity - 1) * 6];
            Mesh createdMesh = null;
            try
            {
                createdMesh = new Mesh { name = "Flexible Plane", indexFormat = IndexFormat.UInt32 };
                createdMesh.MarkDynamic();
                vertices.Capacity = nativeVertices.Length;
                uv.Capacity = nativeVertices.Length;
                colors.Capacity = nativeVertices.Length;
                indices.Capacity = nativeIndices.Length;
                propertyState = new LiberaFxDrawPropertyState(1);
                propertyState.SetTexture(Shader.PropertyToID(definition.textureProperty), definition.texture);
                submission = new LiberaFxRenderSubmission(propertyState, statistics);
                submission.owner = parent;
                submission.Configure(createdMesh, definition.material, 0,
                    parent.gameObject.layer, statistics);
            }
            catch
            {
                DestroyOwned(createdMesh);
                throw;
            }
            mesh = createdMesh;
        }

        internal void Update(NativeInstance.NativeSpawnableHostBase host, bool root, byte[] container,
            ulong instance, double time)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThread ||
                disposed || !parent || host == null || double.IsNaN(time) || double.IsInfinity(time))
            {
                throw new InvalidOperationException("Invalid Flexible Plane renderer state or time.");
            }
            var count = 0u;
            if (definition.payload.sourceKind == "history")
            {
                var p = Read(host, root, container, instance, "position");
                var w = Read(host, root, container, instance, "pointWidth").x;
                var c = Read(host, root, container, instance, "pointColor");
                if (w < 0)
                {
                    throw new InvalidOperationException("Flexible Plane history width is negative.");
                }
                var sample = new LiberaFxFlexiblePoint { x = p.x, y = p.y, z = p.z, width = w,
                    r = c.x, g = c.y, b = c.z, a = c.w };
                count = history.Sample(definition, time, sample, points);
            }
            else
            {
                Array.Copy(definition.points, points, points.Length);
                count = (uint)points.Length;
                for (var index = 0; index < Math.Min(4, points.Length); index++)
                {
                    var p = Read(host, root, container, instance, "position" + index);
                    points[index].x = p.x;
                    points[index].y = p.y;
                    points[index].z = p.z;
                }
            }
            var width = Read(host, root, container, instance, "width").x;
            var tint = Read(host, root, container, instance, "tint");
            if (width < 0)
            {
                throw new InvalidOperationException("Flexible Plane width multiplier is negative.");
            }
            for (var index = 0; index < count; index++)
            {
                points[index].width = Checked(points[index].width * (double)width);
                points[index].r = Checked(points[index].r * (double)tint.x);
                points[index].g = Checked(points[index].g * (double)tint.y);
                points[index].b = Checked(points[index].b * (double)tint.z);
                points[index].a = Checked(points[index].a * (double)tint.w);
            }
            var camera = Vector3.zero;
            if (definition.payload.facing == "camera")
            {
                if (!definition.camera)
                {
                    throw new InvalidOperationException("Flexible Plane camera is unavailable.");
                }
                var determinant = parent.localToWorldMatrix.determinant;
                if (float.IsNaN(determinant) || float.IsInfinity(determinant) || Math.Abs(determinant) < 1e-12f)
                {
                    throw new InvalidOperationException("Flexible Plane camera transform is singular.");
                }
                camera = parent.InverseTransformPoint(definition.camera.transform.position);
            }
            var options = new LiberaFxFlexibleOptions
            {
                facing = definition.payload.facing == "camera" ? 1u : 0u,
                uvMode = definition.payload.uvMode == "repeat" ? 1u : 0u,
                upX = Checked(definition.payload.up[0]), upY = Checked(definition.payload.up[1]),
                upZ = Checked(definition.payload.up[2]), cameraX = Checked(camera.x),
                cameraY = Checked(camera.y), cameraZ = Checked(camera.z),
                repeatLength = Checked(definition.payload.repeatLength)
            };
            var result = LiberaFxFlexibleNative.libra_flexible_plane_generate(points, count, ref options,
                nativeVertices, (uint)nativeVertices.Length, nativeIndices, (uint)nativeIndices.Length,
                out var counts);
            if (result != NativeResult.Ok)
            {
                throw new InvalidOperationException("Flexible Plane geometry failed: " + result);
            }
            if (counts.vertices == 0 || counts.indices == 0)
            {
                submission.enabled = false;
                return;
            }
            vertices.Clear();
            uv.Clear();
            colors.Clear();
            indices.Clear();
            for (var index = 0; index < counts.vertices; index++)
            {
                var v = nativeVertices[index];
                vertices.Add(new Vector3(v.X, v.Y, v.Z));
                uv.Add(new Vector2(v.U, v.V));
                colors.Add(new Color(v.R, v.G, v.B, v.A));
            }
            for (var index = 0; index < counts.indices; index++)
            {
                indices.Add((int)nativeIndices[index]);
            }
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            submission.layer = parent.gameObject.layer;
            submission.SetMatrix(parent.localToWorldMatrix);
            submission.enabled = true;
            statistics?.GeometryUpload(vertices.Count);
        }

        private Vector4 Read(NativeInstance.NativeSpawnableHostBase host, bool root, byte[] container,
            ulong instance, string id)
        {
            var input = definition.byId[id];
            if (string.IsNullOrEmpty(input.sourceNodeId))
            {
                var defaults = input.defaults;
                return new Vector4(
                    input.width > 0 ? Checked(defaults[0]) : 0,
                    input.width > 1 ? Checked(defaults[1]) : 0,
                    input.width > 2 ? Checked(defaults[2]) : 0,
                    input.width > 3 ? Checked(defaults[3]) : 0);
            }
            var result = root ? host.ReadRootValue(input.sourceNodeBytes, out var value)
                : host.ReadChildValue(container, instance, input.sourceNodeBytes, out value);
            if (result != NativeResult.Ok || value.ComponentCount > 4 ||
                input.sourceComponent + input.width > value.ComponentCount)
            {
                throw new InvalidOperationException("Flexible Plane source read failed: " + result);
            }
            return new Vector4(
                input.width > 0 ? Checked(Component(value, input.sourceComponent)) : 0,
                input.width > 1 ? Checked(Component(value, input.sourceComponent + 1)) : 0,
                input.width > 2 ? Checked(Component(value, input.sourceComponent + 2)) : 0,
                input.width > 3 ? Checked(Component(value, input.sourceComponent + 3)) : 0);
        }

        private static double Component(NativeValue value, int component)
        {
            if (component == 0)
            {
                return value.X;
            }
            if (component == 1)
            {
                return value.Y;
            }
            if (component == 2)
            {
                return value.Z;
            }
            return value.W;
        }

        private static float Checked(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue)
            {
                throw new InvalidOperationException("Flexible Plane value cannot be represented as a float.");
            }
            return (float)value;
        }

        public void Dispose()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThread)
            {
                throw new InvalidOperationException("Flexible Plane must be disposed on its owner thread.");
            }
            if (disposed)
            {
                return;
            }
            disposed = true;
            submission.Dispose();
            DestroyOwned(mesh);
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (!value)
            {
                return;
            }
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }
    }
}
