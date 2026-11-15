using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LibraGpu.Core;

internal static partial class ShaderEmitter
{
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static CompiledGpuProgram Compile(GpuProgram p, string json) => new(p, json);
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    internal static ShaderSource Emit(GpuProgram p, IReadOnlyList<KernelMetadata> kernels, ShaderFormat format, ShaderBindings b)
    {
        // Custom names are deliberately namespaced to prevent keyword/local/function collisions.
        if (!Identifier().IsMatch(b.ParticleBuffer ?? "") || !Identifier().IsMatch(b.UniformBuffer ?? "") ||
            b.ParticleBuffer == b.UniformBuffer ||
            b.ParticleBuffer != "ParticleBuffer" && !b.ParticleBuffer!.StartsWith("User_", StringComparison.Ordinal) ||
            b.UniformBuffer != "LibraGpuParameters" && !b.UniformBuffer!.StartsWith("User_", StringComparison.Ordinal) ||
            b.ParticleRegister is < 0 or > 127 || b.UniformRegister is < 0 or > 13 ||
            b.TextureRegisterBase < 0 || b.TextureRegisterBase > 128 - p.Resources.Count ||
            b.SamplerRegisterBase is < 0 or > 15)
            throw new ArgumentException("Bindings require unique, non-reserved ASCII identifiers and bounded registers.", nameof(b));
        var s = new StringBuilder();
        var mappings = new List<SourceLineMapping>();
        int line = 1;
        void Write(string text = "") { s.Append(text).Append('\n'); line++; }
        void Mapped(string text, KernelGraph k, string id, string? pin)
        { mappings.Add(new(line, k.Id, id, pin, k.Nodes.FirstOrDefault(n => n.Id == id)?.SourceSpan ?? k.Writes.FirstOrDefault(w => w.Id == id)?.SourceSpan)); Write(text); }
        if (format == ShaderFormat.UnityCompute)
            foreach (var k in kernels) Write($"#pragma kernel {k.EntryPoint}");
        if (format == ShaderFormat.HlslInclude) { Write("#ifndef LIBRA_GPU_GENERATED_INCLUDED"); Write("#define LIBRA_GPU_GENERATED_INCLUDED"); }
        Write("// Generated engine-neutral particle compute source; dispatch in declared flow order.");
        Write("struct LibraGpuParticle { float4 positionAge; float4 velocityLifetime; float4 color; float4 sizeRotation; uint4 seedAlive; };");
        Write($"RWStructuredBuffer<LibraGpuParticle> {b.ParticleBuffer} : register(u{b.ParticleRegister});");
        Write($"cbuffer {b.UniformBuffer} : register(b{b.UniformRegister})");
        Write("{");
        Write("    uint LibraGpuCapacity; uint LibraGpuCount; uint LibraGpuSpawnStart; uint LibraGpuSpawnCount;");
        Write("    float LibraGpuDeltaTime; float LibraGpuTime; uint LibraGpuEmitterSeed; uint LibraGpuFrameIndex;");
        Write("    float3 LibraGpuEmitterPosition; uint LibraGpuPadding;");
        Write("};");
        for (int i = 0; i < p.Resources.Count; i++)
        {
            Write($"Texture2D<float4> LibraGpuTexture_{i:D4} : register(t{b.TextureRegisterBase + i});");
        }
        if (p.Resources.Count > 0) Write($"SamplerState LibraGpuSampler : register(s{b.SamplerRegisterBase});");
        Write("uint LibraGpuHash(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; return x ^ (x >> 16); }");
        Write("float LibraGpuRandom(uint x) { return float(LibraGpuHash(x) >> 8) * (1.0f / 16777216.0f); }");
        Write("int LibraGpuDiv(int a, int b) { if (b == 0) return 0; if (a == (-2147483647 - 1) && b == -1) return (-2147483647 - 1); return a / b; }");
        Write("uint LibraGpuDiv(uint a, uint b) { return b == 0u ? 0u : a / b; }");
        foreach (var t in new[] { GpuType.Float, GpuType.Float2, GpuType.Float3, GpuType.Float4 })
        {
            var name = HlslType(t);
            // Unity's fast math may fold isfinite(); integer IEEE exponent tests remain observable.
            var finiteBits = "(asuint(a) & 0x7f800000u) != 0x7f800000u";
            Write($"bool LibraGpuIsFinite({name} a) {{ return {(t == GpuType.Float ? finiteBits : $"all({finiteBits})")}; }}");
            if (t == GpuType.Float)
                Write("float LibraGpuDiv(float a, float b) { return b == 0.0f ? 0.0f : a / b; }");
            else
            {
                var components = Enumerable.Range(0, GpuOperationRegistry.Components(t)).Select(i => "xyzw"[i]);
                Write($"{name} LibraGpuDiv({name} a, {name} b) {{ return {name}({string.Join(", ", components.Select(c => $"LibraGpuDiv(a.{c}, b.{c})"))}); }}");
                Write($"{name} LibraGpuNormalize({name} a) {{ if (!LibraGpuIsFinite(a)) return ({name})0; float m = {Enumerable.Range(0, GpuOperationRegistry.Components(t)).Select(i => $"abs(a.{"xyzw"[i]})").Aggregate((a, b) => $"max({a}, {b})")}; if (!(m > 0.0f)) return ({name})0; {name} scaled = a / m; return scaled * rsqrt(dot(scaled, scaled)); }}");
            }
        }
        Write("bool LibraGpuFinite(LibraGpuParticle p) { return LibraGpuIsFinite(p.positionAge) && LibraGpuIsFinite(p.velocityLifetime) && LibraGpuIsFinite(p.color) && LibraGpuIsFinite(p.sizeRotation); }");
        for (int kernelIndex = 0; kernelIndex < p.Kernels.Count; kernelIndex++)
        {
            var k = p.Kernels[kernelIndex];
            var meta = kernels[kernelIndex];
            var nodeLookup = k.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var order = GpuCompiler.Topological(nodeLookup);
            var registers = order.Select((n, i) => (n.Id, Name: $"r{i:D4}")).ToDictionary(x => x.Id, x => x.Name, StringComparer.Ordinal);
            Write($"void LibraGpuEvaluate_{kernelIndex:D4}(inout LibraGpuParticle p)");
            Write("{");
            foreach (var n in order)
            {
                var args = n.Inputs.Select(id => registers[id]).ToArray();
                var types = n.Inputs.Select(id => nodeLookup[id].Type).ToArray();
                var expression = Expression(n, args, types);
                Mapped($"    {HlslType(n.Type)} {registers[n.Id]} = {expression};", k, n.Id, "out");
            }
            // SSA reads the incoming particle snapshot; attribute stores happen only after evaluation.
            foreach (var w in k.Writes)
                Mapped($"    {Attribute(w.Attribute)} = {(w.Attribute == "Alive" ? $"({registers[w.Value]} ? 1u : 0u)" : registers[w.Value])};", k, w.Id, "value");
            Write("}");
            Write("[numthreads(64, 1, 1)]");
            Write($"void {meta.EntryPoint}(uint3 dispatchThreadId : SV_DispatchThreadID)");
            Write("{");
            Write("    uint i = dispatchThreadId.x;");
            Write($"    uint actualCapacity, actualStride; {b.ParticleBuffer}.GetDimensions(actualCapacity, actualStride);");
            Write("    if (i >= min(LibraGpuCapacity, actualCapacity) || actualStride != 80u) return;");
            if (k.Stage == KernelStage.Spawn)
            {
                Write("    if (i < LibraGpuSpawnStart || i - LibraGpuSpawnStart >= LibraGpuSpawnCount) return;");
                Write($"    if ({b.ParticleBuffer}[i].seedAlive.y != 0u) return;");
                Write("    LibraGpuParticle p;");
                Write("    p.positionAge = float4(LibraGpuEmitterPosition, 0.0f);");
                Write("    p.velocityLifetime = float4(0.0f, 0.0f, 0.0f, 1.0f);");
                Write("    p.color = float4(1.0f, 1.0f, 1.0f, 1.0f);");
                Write("    p.sizeRotation = float4(1.0f, 1.0f, 0.0f, 0.0f);");
                Write("    p.seedAlive = uint4(LibraGpuHash(i ^ LibraGpuEmitterSeed ^ LibraGpuHash(LibraGpuFrameIndex)), 1u, 0u, 0u);");
            }
            else
            {
                Write("    if (i >= LibraGpuCount) return;");
                Write($"    LibraGpuParticle p = {b.ParticleBuffer}[i];");
                Write("    if (p.seedAlive.y == 0u) return;");
                Write("    if (!LibraGpuFinite(p) || !(p.velocityLifetime.w > 0.0f)) { p.seedAlive.y = 0u;");
                Write($"        {b.ParticleBuffer}[i] = p; return; }}");
                if (!k.Writes.Any(w => w.Attribute == "Age"))
                    Write("    p.positionAge.w += LibraGpuIsFinite(LibraGpuDeltaTime) ? max(LibraGpuDeltaTime, 0.0f) : 0.0f;");
            }
            Write($"    LibraGpuEvaluate_{kernelIndex:D4}(p);");
            Write("    p.positionAge.w = max(p.positionAge.w, 0.0f);");
            Write("    if (!LibraGpuFinite(p) || !(p.velocityLifetime.w > 0.0f) || p.positionAge.w >= p.velocityLifetime.w) p.seedAlive.y = 0u;");
            Write("    p.seedAlive.y = p.seedAlive.y != 0u ? 1u : 0u;");
            Write($"    {b.ParticleBuffer}[i] = p;");
            Write("}");
        }
        if (format == ShaderFormat.HlslInclude) Write("#endif");
        return new(format, s.ToString(), mappings, b, p.Resources.Select((r, i) =>
            new ResourceMetadata(r.Id, r.Kind, $"LibraGpuTexture_{i:D4}", b.TextureRegisterBase + i, r.Width, r.Height)));
    }
    internal static string HlslType(GpuType t) => t switch
    { GpuType.Float => "float", GpuType.Int => "int", GpuType.UInt => "uint", GpuType.Bool => "bool", GpuType.Float2 => "float2", GpuType.Float3 => "float3", GpuType.Float4 => "float4", _ => throw new InvalidOperationException() };
    private static string Attribute(string id) => id switch
    {
        "Position" => "p.positionAge.xyz",
        "Age" => "p.positionAge.w",
        "Velocity" => "p.velocityLifetime.xyz",
        "Lifetime" => "p.velocityLifetime.w",
        "Color" => "p.color",
        "Size" => "p.sizeRotation.xy",
        "Rotation" => "p.sizeRotation.z",
        "Seed" => "p.seedAlive.x",
        "Alive" => "p.seedAlive.y",
        _ => throw new InvalidOperationException()
    };
    private static string Expression(GpuNode n, string[] a, GpuType[] types)
    {
        string Cast(int i) => types[i] == n.Type ? a[i] :
            $"{HlslType(n.Type)}({string.Join(", ", Enumerable.Repeat(a[i], GpuOperationRegistry.Components(n.Type)))})";
        string Call(string f) => $"{f}({string.Join(", ", Enumerable.Range(0, a.Length).Select(Cast))})";
        return n.Op switch
        {
            GpuOp.Constant => Literal(n),
            GpuOp.ReadAttribute => n.Attribute == "Alive" ? "(p.seedAlive.y != 0u)" : Attribute(n.Attribute!),
            GpuOp.ReadUniform => n.Uniform switch { "deltaTime" => "LibraGpuDeltaTime", "time" => "LibraGpuTime", "emitterPosition" => "LibraGpuEmitterPosition", _ => throw new InvalidOperationException() },
            GpuOp.Add => $"({Cast(0)} + {Cast(1)})",
            GpuOp.Sub => $"({Cast(0)} - {Cast(1)})",
            GpuOp.Mul => $"({Cast(0)} * {Cast(1)})",
            GpuOp.Div => Call("LibraGpuDiv"),
            GpuOp.Min => Call("min"),
            GpuOp.Max => Call("max"),
            GpuOp.Clamp => $"min(max({Cast(0)}, {Cast(1)}), {Cast(2)})",
            GpuOp.Sin => Call("sin"),
            GpuOp.Cos => Call("cos"),
            GpuOp.Normalize => Call("LibraGpuNormalize"),
            GpuOp.Length => $"length({a[0]})",
            GpuOp.Compose => $"{HlslType(n.Type)}({string.Join(", ", a)})",
            GpuOp.Select => $"({a[0]} ? {a[1]} : {a[2]})",
            GpuOp.Random => $"LibraGpuRandom({a[0]})",
            GpuOp.Component => $"{a[0]}.{"xyzw"[n.ComponentIndex!.Value]}",
            _ => throw new InvalidOperationException()
        };
    }
    private static string Literal(GpuNode n)
    {
        string Scalar(double v) => n.Type switch
        {
            GpuType.Bool => v == 0 ? "false" : "true",
            GpuType.Int => v == int.MinValue ? "(-2147483647 - 1)" : ((int)v).ToString(CultureInfo.InvariantCulture),
            GpuType.UInt => ((uint)v).ToString(CultureInfo.InvariantCulture) + "u",
            _ => ((float)v).ToString("R", CultureInfo.InvariantCulture) + (Math.Abs(v) < 1e20 && !((float)v).ToString("R", CultureInfo.InvariantCulture).Contains('.') && !((float)v).ToString("R", CultureInfo.InvariantCulture).Contains('E') ? ".0f" : "f")
        };
        return n.Value.Count == 1 ? Scalar(n.Value[0]) : $"{HlslType(n.Type)}({string.Join(", ", n.Value.Select(Scalar))})";
    }
}
