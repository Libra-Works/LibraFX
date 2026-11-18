using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LiberaFx.Unity
{
    internal enum LiberaFxSkeletalPoseTargetKind : byte { Owner = 0, RegisteredKey = 1 }
    internal enum LiberaFxSkeletalPoseSpace : byte { BoneLocal = 0, Component = 1, World = 2 }

    internal sealed class LiberaFxSkeletalPoseBinding
    {
        private const string MatrixPrefix = "__LFXS1M_";
        private const string ValidityPrefix = "__LFXS1V_";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal Guid SourceNodeId { get; private set; }
        internal LiberaFxSkeletalPoseTargetKind Target { get; private set; }
        internal string Key { get; private set; }
        internal string BonePath { get; private set; }
        internal LiberaFxSkeletalPoseSpace Space { get; private set; }
        internal bool IsValidity { get; private set; }

        internal static bool TryParsePortName(string name, out LiberaFxSkeletalPoseBinding binding)
        {
            binding = null;
            if (name == null) return false;
            var prefix = name.StartsWith(MatrixPrefix, StringComparison.Ordinal) ? MatrixPrefix :
                name.StartsWith(ValidityPrefix, StringComparison.Ordinal) ? ValidityPrefix : null;
            if (prefix == null) return false;
            try
            {
                var token = name.Substring(prefix.Length);
                foreach (var character in token)
                    if (!(character >= 'A' && character <= 'Z' || character >= 'a' && character <= 'z' ||
                        character >= '0' && character <= '9' || character == '-' || character == '_'))
                        return false;
                var base64 = token.Replace('-', '+').Replace('_', '/');
                base64 += new string('=', (4 - base64.Length % 4) % 4);
                var bytes = Convert.FromBase64String(base64);
                if (bytes.Length < 20 || bytes[0] != 1) return false;
                var id = Guid.ParseExact(ToHex(bytes, 1, 16), "N");
                var target = (LiberaFxSkeletalPoseTargetKind)bytes[17];
                var space = (LiberaFxSkeletalPoseSpace)bytes[18];
                var keyLength = bytes[19];
                if (bytes.Length < 21 + keyLength) return false;
                var boneLength = bytes[20 + keyLength];
                if (bytes.Length != 21 + keyLength + boneLength ||
                    (byte)target > (byte)LiberaFxSkeletalPoseTargetKind.RegisteredKey ||
                    (byte)space > (byte)LiberaFxSkeletalPoseSpace.World)
                    return false;
                var key = StrictUtf8.GetString(bytes, 20, keyLength);
                var bone = StrictUtf8.GetString(bytes, 21 + keyLength, boneLength);
                var invalidKey = false;
                foreach (var character in key)
                    if (char.IsControl(character)) { invalidKey = true; break; }
                if (!ValidPath(bone) || StrictUtf8.GetByteCount(key) + StrictUtf8.GetByteCount(bone) > 63 ||
                    id == Guid.Empty ||
                    target == LiberaFxSkeletalPoseTargetKind.Owner && key.Length != 0 ||
                    target == LiberaFxSkeletalPoseTargetKind.RegisteredKey &&
                    (string.IsNullOrWhiteSpace(key) || key.Trim() != key || invalidKey))
                    return false;
                binding = new LiberaFxSkeletalPoseBinding
                {
                    SourceNodeId = id,
                    Target = target,
                    Key = key,
                    BonePath = bone,
                    Space = space,
                    IsValidity = prefix == ValidityPrefix
                };
                return binding.PortName == name;
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException)
            {
                return false;
            }
        }

        internal string PortName
        {
            get
            {
                var bytes = new byte[21 + StrictUtf8.GetByteCount(Key) + StrictUtf8.GetByteCount(BonePath)];
                bytes[0] = 1;
                var id = SourceNodeId.ToString("N");
                for (var index = 0; index < 16; ++index)
                    bytes[index + 1] = Convert.ToByte(id.Substring(index * 2, 2), 16);
                bytes[17] = (byte)Target;
                bytes[18] = (byte)Space;
                var keyBytes = StrictUtf8.GetBytes(Key);
                var boneBytes = StrictUtf8.GetBytes(BonePath);
                bytes[19] = (byte)keyBytes.Length;
                Buffer.BlockCopy(keyBytes, 0, bytes, 20, keyBytes.Length);
                bytes[20 + keyBytes.Length] = (byte)boneBytes.Length;
                Buffer.BlockCopy(boneBytes, 0, bytes, 21 + keyBytes.Length, boneBytes.Length);
                var prefix = IsValidity ? ValidityPrefix : MatrixPrefix;
                return prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }

        private static string ToHex(byte[] bytes, int offset, int count)
        {
            var builder = new StringBuilder(count * 2);
            for (var index = 0; index < count; ++index)
                builder.Append(bytes[offset + index].ToString("x2"));
            return builder.ToString();
        }

        private static bool ValidPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path[0] == '/' || path[path.Length - 1] == '/' ||
                HasControl(path)) return false;
            var parts = path.Split('/');
            foreach (var part in parts)
                if (part.Length == 0 || part == "." || part == "..") return false;
            return true;
        }

        private static bool HasControl(string value)
        {
            foreach (var character in value)
                if (char.IsControl(character)) return true;
            return false;
        }
    }

    internal readonly struct LiberaFxPoseMatrix4
    {
        internal readonly double M00, M01, M02, M03;
        internal readonly double M10, M11, M12, M13;
        internal readonly double M20, M21, M22, M23;
        internal readonly double M30, M31, M32, M33;

        internal LiberaFxPoseMatrix4(Matrix4x4 value)
        {
            M00 = value.m00; M01 = value.m01; M02 = value.m02; M03 = value.m03;
            M10 = value.m10; M11 = value.m11; M12 = value.m12; M13 = value.m13;
            M20 = value.m20; M21 = value.m21; M22 = value.m22; M23 = value.m23;
            M30 = value.m30; M31 = value.m31; M32 = value.m32; M33 = value.m33;
        }

        internal void CopyTo(double[] destination)
        {
            if (destination == null || destination.Length < 16)
                throw new ArgumentException("A pose matrix destination must hold 16 doubles.", nameof(destination));
            destination[0] = M00; destination[1] = M01; destination[2] = M02; destination[3] = M03;
            destination[4] = M10; destination[5] = M11; destination[6] = M12; destination[7] = M13;
            destination[8] = M20; destination[9] = M21; destination[10] = M22; destination[11] = M23;
            destination[12] = M30; destination[13] = M31; destination[14] = M32; destination[15] = M33;
        }
    }

    internal readonly struct LiberaFxSkeletalPoseSnapshot
    {
        internal readonly LiberaFxPoseMatrix4 Matrix;
        internal readonly bool Valid;
        internal readonly ulong TargetGeneration;

        internal LiberaFxSkeletalPoseSnapshot(LiberaFxPoseMatrix4 matrix, bool valid, ulong targetGeneration)
        {
            Matrix = matrix;
            Valid = valid;
            TargetGeneration = targetGeneration;
        }
    }

    internal sealed class LiberaFxSkeletalPoseLease
    {
        internal LiberaFxSkeletalPoseTarget Target;
        internal string Key;
        internal ulong RegistryGeneration;
        internal ulong TargetGeneration;
        internal bool Bound;
        internal bool Invalid;
    }

    internal sealed class LiberaFxSkeletalPoseSourcePorts
    {
        internal LiberaFxSkeletalPoseBinding Binding;
        internal string MatrixName;
        internal string ValidityName;
        internal readonly LiberaFxSkeletalPoseLease Lease = new LiberaFxSkeletalPoseLease();

        internal static bool TryRead(NativeInstance.NativeSpawnableHostBase host,
            out List<LiberaFxSkeletalPoseSourcePorts> sources, out string error)
        {
            sources = new List<LiberaFxSkeletalPoseSourcePorts>();
            error = null;
            var found = new Dictionary<Guid, LiberaFxSkeletalPoseSourcePorts>();
            for (uint index = 0; index <= 256; ++index)
            {
                var result = host.GetGameIoPortName(index, out var name);
                if (result == NativeResult.InvalidArgument) break;
                if (result != NativeResult.Ok)
                {
                    error = "Could not read runtime Game I/O input name: " + result + ".";
                    return false;
                }
                if (!LiberaFxSkeletalPoseBinding.TryParsePortName(name, out var binding))
                {
                    if (name.StartsWith("__LFXS1M_", StringComparison.Ordinal) ||
                        name.StartsWith("__LFXS1V_", StringComparison.Ordinal))
                    {
                        error = "Malformed SOURCE descriptor in compiled package.";
                        return false;
                    }
                    continue;
                }
                if (!found.TryGetValue(binding.SourceNodeId, out var item))
                {
                    item = new LiberaFxSkeletalPoseSourcePorts { Binding = binding };
                    found.Add(binding.SourceNodeId, item);
                }
                if (item.Binding.Target != binding.Target || item.Binding.Key != binding.Key ||
                    item.Binding.BonePath != binding.BonePath || item.Binding.Space != binding.Space)
                {
                    error = "SOURCE descriptor pair does not match.";
                    return false;
                }
                if (binding.IsValidity)
                {
                    if (item.ValidityName != null) { error = "Duplicate SOURCE validity input."; return false; }
                    item.ValidityName = name;
                }
                else
                {
                    if (item.MatrixName != null) { error = "Duplicate SOURCE Matrix input."; return false; }
                    item.MatrixName = name;
                }
            }
            foreach (var item in found.Values)
            {
                if (item.MatrixName == null || item.ValidityName == null)
                {
                    error = "SOURCE package is missing its Matrix or validity input.";
                    return false;
                }
                sources.Add(item);
            }
            return true;
        }

        internal NativeResult Publish(LiberaFxSpawnableComponent owner,
            NativeInstance.NativeSpawnableHostBase host)
        {
            var valid = LiberaFxSkeletalPoseTarget.TryCapture(Binding, owner, Lease, out var snapshot);
            var values = new double[16];
            if (valid) snapshot.Matrix.CopyTo(values); // Per-frame, immutable after publication.
            var result = host.SetGameProperty(MatrixName, values);
            if (result != NativeResult.Ok) return result;
            return host.SetGameProperty(ValidityName, new[] { valid && snapshot.Valid ? 1d : 0d });
        }
    }

    /// <summary>
    /// Attach directly to the intended SkinnedMeshRenderer object. Register
    /// non-owner targets explicitly with RegisterTarget; no scene lookup occurs.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiberaFxSkeletalPoseTarget : MonoBehaviour
    {
        private sealed class RegistryEntry
        {
            internal WeakReference<LiberaFxSkeletalPoseTarget> Target;
            internal ulong Generation;
        }

        private sealed class Registration : IDisposable
        {
            private readonly string key;
            private readonly ulong generation;
            private bool disposed;

            internal Registration(string value, ulong revision) { key = value; generation = revision; }

            public void Dispose()
            {
                RequireMainThread();
                if (disposed) return;
                disposed = true;
                if (Registry.TryGetValue(key, out var current) && current.Generation == generation)
                    Registry.Remove(key);
            }
        }

        private static readonly Dictionary<string, RegistryEntry> Registry =
            new Dictionary<string, RegistryEntry>(StringComparer.Ordinal);
        private static int mainThreadId;
        private static ulong nextGeneration;

        [SerializeField] private SkinnedMeshRenderer skinnedMesh;
        [SerializeField] private Transform skeletonRoot;

        private Mesh initialMesh;
        private SkinnedMeshRenderer initialRenderer;
        private Transform initialSkeletonRoot;
        private Transform[] initialBones;
        private ulong targetGeneration;
        private bool invalidated;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeMainThread() =>
            mainThreadId = Thread.CurrentThread.ManagedThreadId;

        public static IDisposable RegisterTarget(string key, LiberaFxSkeletalPoseTarget target)
        {
            RequireMainThread();
            var invalidKey = key == null;
            if (key != null)
                foreach (var character in key)
                    if (char.IsControl(character)) { invalidKey = true; break; }
            if (string.IsNullOrWhiteSpace(key) || key.Trim() != key || invalidKey ||
                target == null || !target.TryGetTargetGeneration(out _))
                throw new ArgumentException("Register a live skeletal target with a valid exact key.");
            var generation = NextGeneration();
            Registry[key] = new RegistryEntry
            {
                Target = new WeakReference<LiberaFxSkeletalPoseTarget>(target),
                Generation = generation
            };
            return new Registration(key, generation);
        }

        private void OnEnable()
        {
            if (mainThreadId == 0) InitializeMainThread();
            RequireMainThread();
            invalidated = false;
            initialMesh = skinnedMesh != null ? skinnedMesh.sharedMesh : null;
            initialRenderer = skinnedMesh;
            initialSkeletonRoot = skeletonRoot;
            initialBones = skinnedMesh != null ? skinnedMesh.bones : null;
            targetGeneration = NextGeneration();
        }

        private void OnDisable()
        {
            RequireMainThread();
            invalidated = true;
            targetGeneration = NextGeneration();
        }

        private void OnDestroy()
        {
            RequireMainThread();
            invalidated = true;
            targetGeneration = NextGeneration();
        }

        internal static bool TryCapture(LiberaFxSkeletalPoseBinding binding,
            LiberaFxSpawnableComponent owner, LiberaFxSkeletalPoseLease lease,
            out LiberaFxSkeletalPoseSnapshot snapshot)
        {
            RequireMainThread();
            snapshot = new LiberaFxSkeletalPoseSnapshot(default, false, 0);
            if (binding == null || owner == null || lease == null || lease.Invalid) return false;
            if (!lease.Bound)
            {
                LiberaFxSkeletalPoseTarget selected;
                ulong registryGeneration;
                if (binding.Target == LiberaFxSkeletalPoseTargetKind.Owner)
                {
                    selected = owner.GetComponent<LiberaFxSkeletalPoseTarget>();
                    registryGeneration = 0;
                }
                else
                {
                    if (!Registry.TryGetValue(binding.Key, out var entry) ||
                        !entry.Target.TryGetTarget(out selected) || selected == null)
                        return false;
                    registryGeneration = entry.Generation;
                }
                if (selected == null || !selected.TryGetTargetGeneration(out var targetGeneration))
                    return false;
                lease.Target = selected;
                lease.Key = binding.Target == LiberaFxSkeletalPoseTargetKind.RegisteredKey ? binding.Key : null;
                lease.RegistryGeneration = registryGeneration;
                lease.TargetGeneration = targetGeneration;
                lease.Bound = true;
            }

            var target = lease.Target;
            if (target == null || !target.TryGetTargetGeneration(out var currentGeneration) ||
                currentGeneration != lease.TargetGeneration ||
                !IsCurrentLease(binding, lease, target))
            {
                lease.Invalid = true;
                return false;
            }

            if (!target.TryGetMatrix(binding.BonePath, binding.Space, out var matrix))
                return false;
            snapshot = new LiberaFxSkeletalPoseSnapshot(new LiberaFxPoseMatrix4(matrix), true, currentGeneration);
            return true;
        }

        private static bool IsCurrentLease(LiberaFxSkeletalPoseBinding binding,
            LiberaFxSkeletalPoseLease lease, LiberaFxSkeletalPoseTarget target)
        {
            if (binding.Target == LiberaFxSkeletalPoseTargetKind.Owner) return lease.Key == null;
            return Registry.TryGetValue(binding.Key, out var entry) &&
                entry.Generation == lease.RegistryGeneration &&
                entry.Target.TryGetTarget(out var registered) && registered == target;
        }

        private bool TryGetTargetGeneration(out ulong generation)
        {
            RequireMainThread();
            generation = targetGeneration;
            if (this == null || !isActiveAndEnabled || invalidated || skinnedMesh == null ||
                skeletonRoot == null || skinnedMesh.sharedMesh == null)
                return false;
            if (initialMesh == null) initialMesh = skinnedMesh.sharedMesh;
            if (initialBones == null || initialBones.Length == 0)
                initialBones = skinnedMesh.bones;
            var currentBones = skinnedMesh.bones;
            var bonesChanged = currentBones == null || initialBones == null ||
                currentBones.Length != initialBones.Length;
            if (!bonesChanged)
                for (var index = 0; index < currentBones.Length; ++index)
                    if (currentBones[index] != initialBones[index]) { bonesChanged = true; break; }
            if (skinnedMesh != initialRenderer || skeletonRoot != initialSkeletonRoot ||
                skinnedMesh.sharedMesh != initialMesh || bonesChanged)
            {
                invalidated = true;
                targetGeneration = NextGeneration();
                generation = targetGeneration;
                return false;
            }
            return true;
        }

        private bool TryGetMatrix(string bonePath, LiberaFxSkeletalPoseSpace space, out Matrix4x4 matrix)
        {
            matrix = default;
            if (!TryGetTargetGeneration(out _)) return false;
            var bone = skeletonRoot.Find(bonePath);
            if (bone == null || Array.IndexOf(skinnedMesh.bones, bone) < 0) return false;
            Matrix4x4 world = bone.localToWorldMatrix;
            switch (space)
            {
                case LiberaFxSkeletalPoseSpace.BoneLocal:
                    if (bone.parent == null) return false;
                    matrix = bone.parent.worldToLocalMatrix * world;
                    break;
                case LiberaFxSkeletalPoseSpace.Component:
                    matrix = skinnedMesh.transform.worldToLocalMatrix * world;
                    break;
                case LiberaFxSkeletalPoseSpace.World:
                    matrix = world;
                    break;
                default:
                    return false;
            }
            return Finite(matrix);
        }

        private static bool Finite(Matrix4x4 matrix)
        {
            for (var row = 0; row < 4; ++row)
            for (var column = 0; column < 4; ++column)
            {
                var value = matrix[row, column];
                if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            }
            return true;
        }

        private static ulong NextGeneration()
        {
            if (nextGeneration == ulong.MaxValue)
                throw new InvalidOperationException("Skeletal pose generation space is exhausted.");
            return ++nextGeneration;
        }

        private static void RequireMainThread()
        {
            if (mainThreadId == 0 || Thread.CurrentThread.ManagedThreadId != mainThreadId)
                throw new InvalidOperationException("Skeletal pose registration and capture must run on Unity's main thread.");
        }
    }
}
