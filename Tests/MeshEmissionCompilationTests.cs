using System.Runtime.InteropServices;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;

internal static class MeshEmissionCompilationTests
{
    public static void Run()
    {
        var graph = ParticleSamples.Create("CpuMeshSurface");
        var system = graph.Nodes.Single(node => node.Kind.Value == "CpuParticle");
        var invalid = graph with { Connections = graph.Connections.Where(edge => edge.TargetPortId != "emitterMesh").ToArray() };
        try { SpawnableGroupPackage.Compile(invalid); throw new Exception("Missing emitter mesh was accepted."); }
        catch (InvalidDataException error) when (error.Message.Contains("Emitter Mesh", StringComparison.Ordinal)) { }
        var library = NativeLibrary.Load(EditorNativePreviewSession.ResolveLibraryPath());
        try
        {
            var create = Export<Create>("libra_spawnable_host_create");
            var advance = Export<Advance>("libra_spawnable_host_advance");
            var release = Export<Release>("libra_spawnable_host_release");
            var count = Export<Count>("libra_spawnable_host_cpu_particle_count");
            var particle = Export<Particle>("libra_spawnable_host_cpu_particle_at");
            var first = Evaluate(graph);
            var transformDefinition = GraphNodeDefinition.Find(new("Transform"))!;
            var transform = new GraphNodeSnapshot(Guid.NewGuid(), transformDefinition.Kind, "Emitter placement", 0, 0,
                transformDefinition.PortType, transformDefinition.PortCategory, Parameters:
                [new("position", "vector3", JsonSerializer.SerializeToElement(new[] { 10d, 2, -3 }))]);
            var moved = Evaluate(graph with { Nodes = [.. graph.Nodes, transform], Connections =
                [.. graph.Connections, new(transform.Id, system.Id, "emitterMatrix", "value")] });
            for (var lane = 0; lane < 3; ++lane)
                Require(Math.Abs(moved[lane] - first[lane] - new[] { 10d, 2, -3 }[lane]) < 1e-8,
                    "Emitter Matrix must transform a deterministic mesh sample exactly once.");
            Console.WriteLine("Mesh emission: source graph → compiled package → native CPU particles, outward motion and emitter matrix passed.");

            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            double[] Evaluate(GraphSnapshot source)
            {
                var bytes = SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(source));
                Check(create(bytes, (ulong)bytes.Length, 32, 64 * 1024 * 1024, 4096, 10000, out var host));
                var memory = Marshal.AllocHGlobal(80);
                try
                {
                    Check(advance(host, 0, 0));
                    var id = system.Id.ToByteArray(true);
                    Check(count(host, id, out var alive));
                    Require(alive == 256, "Mesh sample must spawn its complete burst.");
                    double[]? firstPosition = null;
                    for (uint index = 0; index < alive; ++index)
                    {
                        Check(particle(host, id, index, memory));
                        var position = Position();
                        if (ReferenceEquals(source, graph))
                        {
                            var radius = Math.Sqrt(position.Sum(value => value * value));
                            Require(radius > .96 && radius < 1.001, "Particles must originate on the triangulated sphere surface.");
                        }
                        firstPosition ??= position;
                    }
                    Check(particle(host, id, 0, memory));
                    var before = Position();
                    Check(advance(host, .1, .1));
                    Check(particle(host, id, 0, memory));
                    var after = Position();
                    Require(Math.Sqrt(after.Zip(before, (a, b) => (a-b)*(a-b)).Sum()) > .25,
                        "The mesh normal must produce outward particle velocity.");
                    return firstPosition!;
                    double[] Position() => Enumerable.Range(0, 3).Select(lane =>
                        BitConverter.Int64BitsToDouble(Marshal.ReadInt64(memory, 8 + lane * 8))).ToArray();
                }
                finally { Marshal.FreeHGlobal(memory); Check(release(host)); }
            }
        }
        finally { NativeLibrary.Free(library); }
    }
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Check(int result) => Require(result == 0, "Native mesh particle operation failed: " + result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create(byte[] data, ulong bytes, uint active, ulong budget, uint events, uint instructions, out ulong host);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Advance(ulong host, double time, double delta);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Release(ulong host);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(ulong host, byte[] id, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Particle(ulong host, byte[] id, uint index, nint output);
}
