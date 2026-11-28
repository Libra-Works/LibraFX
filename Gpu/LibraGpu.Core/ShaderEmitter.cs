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
        var generatedFieldBuffer = format == ShaderFormat.UnityCompute && p.Resources.Any(resource => resource.FlowMap?.Coordinates == FlowMapCoordinates.GeneratedPlane);
        // Custom names are deliberately namespaced to prevent keyword/local/function collisions.
        if (!Identifier().IsMatch(b.ParticleBuffer ?? "") || !Identifier().IsMatch(b.UniformBuffer ?? "") ||
            b.ParticleBuffer == b.UniformBuffer ||
            b.ParticleBuffer != "ParticleBuffer" && !b.ParticleBuffer!.StartsWith("User_", StringComparison.Ordinal) ||
            b.UniformBuffer != "LibraGpuParameters" && !b.UniformBuffer!.StartsWith("User_", StringComparison.Ordinal) ||
            b.ParticleRegister is < 0 or > 127 || b.UniformRegister is < 0 or > 13 ||
            b.TextureRegisterBase < 0 || b.TextureRegisterBase > 128 - p.Resources.Count - (generatedFieldBuffer ? 1 : 0) ||
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
        var (customFloatStride, extendedAttributes) = ParticleAbi.ComputeExtendedLayout(p.Attributes);
        if (customFloatStride > 0)
        {
            Write($"RWStructuredBuffer<float> LibraGpuCustomBuffer : register(u{b.ParticleRegister + 1});");
            Write($"static const uint LibraGpuCustomStride = {customFloatStride}u;");
        }
        Write($"cbuffer {b.UniformBuffer} : register(b{b.UniformRegister})");
        Write("{");
        Write("    uint LibraGpuCapacity; uint LibraGpuCount; uint LibraGpuSpawnStart; uint LibraGpuSpawnCount;");
        Write("    float LibraGpuDeltaTime; float LibraGpuTime; uint LibraGpuEmitterSeed; uint LibraGpuFrameIndex;");
        Write("    float3 LibraGpuEmitterPosition; uint LibraGpuPadding;");
        Write("};");
        if (p.Kernels.Any(k => k.Nodes.Any(n => n.Uniform?.StartsWith("emitterRow", StringComparison.Ordinal) == true)))
            Write("float4 LibraGpuEmitterRows[4]; // Optional shape transform, independent of the 48-byte base frame ABI.");
        for (int i = 0; i < p.Resources.Count; i++)
        {
            Write($"Texture2D<float4> LibraGpuTexture_{i:D4} : register(t{b.TextureRegisterBase + i});");
        }
        if (p.Resources.Count > 0 && format != ShaderFormat.UnityCompute)
            Write($"SamplerState LibraGpuSampler : register(s{b.SamplerRegisterBase});");
        const string unityFlowMapSampler = "LibraGpu_linear_clamp_sampler";
        if (format == ShaderFormat.UnityCompute && p.Resources.Any(resource => resource.Kind == ResourceKind.FlowMap2D))
        {
            Write($"SamplerState {unityFlowMapSampler};");
        }
        var resourceIndices = p.Resources.Select((resource, index) => (resource.Id, Index: index))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);
        if (p.Resources.Any(resource => resource.FlowMap?.Coordinates == FlowMapCoordinates.GeneratedPlane))
            Write(generatedFieldBuffer
                ? $"StructuredBuffer<float4> LibraGpuFieldMappings : register(t{b.TextureRegisterBase + p.Resources.Count}); // Four vectors per resource; instance-owned mapping data."
                : "float4 LibraGpuFieldMappings[256]; // Four rows/basis vectors per resource; supplied by the live producer scope.");
        for (int i = 0; i < p.Resources.Count; i++)
        {
            var resource = p.Resources[i];
            if (resource.Kind != ResourceKind.FlowMap2D) continue;
            var settings = resource.FlowMap!;
            var samplerName = format == ShaderFormat.UnityCompute ? unityFlowMapSampler : "LibraGpuSampler";
            var u = "xyz"[(int)settings.AxisU].ToString();
            var v = "xyz"[(int)settings.AxisV].ToString();
            var minimumU = ((float)settings.WorldMinimumU).ToString("R", CultureInfo.InvariantCulture);
            var minimumV = ((float)settings.WorldMinimumV).ToString("R", CultureInfo.InvariantCulture);
            var rangeU = ((float)(settings.WorldMaximumU - settings.WorldMinimumU)).ToString("R", CultureInfo.InvariantCulture);
            var rangeV = ((float)(settings.WorldMaximumV - settings.WorldMinimumV)).ToString("R", CultureInfo.InvariantCulture);
            var decode = settings.Encoding switch
            {
                FlowMapEncoding.FloatVelocity => "sampled.rg",
                FlowMapEncoding.FloatDensity => "sampled.ba",
                _ => "sampled.rg * 2.0f - 1.0f"
            };
            Write($"float2 LibraGpuSampleFlowMapUv_{i:D4}(float2 uv) {{ float4 sampled = LibraGpuTexture_{i:D4}.SampleLevel({samplerName}, saturate(uv), 0); return {decode}; }}");
            Write($"float3 LibraGpuSampleFlowMapWorld_{i:D4}(float3 position) {{");
            if (settings.Coordinates == FlowMapCoordinates.GeneratedPlane)
                Write($"    float2 uv = saturate(float2(dot(LibraGpuFieldMappings[{i * 4}], float4(position,1)), dot(LibraGpuFieldMappings[{i * 4 + 1}], float4(position,1))));");
            else
                Write($"    float2 uv = saturate((float2(position.{u}, position.{v}) - float2({minimumU}, {minimumV})) / float2({rangeU}, {rangeV}));");
            Write($"    float2 flow = LibraGpuSampleFlowMapUv_{i:D4}(uv);");
            if (settings.Encoding == FlowMapEncoding.FloatDensity)
                Write("    return float3(flow, 0);");
            else if (settings.Coordinates == FlowMapCoordinates.GeneratedPlane)
                Write($"    return LibraGpuFieldMappings[{i * 4 + 2}].xyz * flow.x + LibraGpuFieldMappings[{i * 4 + 3}].xyz * flow.y;");
            else
            {
                Write("    float3 result = 0.0f;");
                Write($"    result.{u} = flow.x; result.{v} = flow.y;");
                Write("    return result;");
            }
            Write("}");
        }
        foreach (var supportLine in GpuShaderSupport.EmitHlslInclude().Split('\n')) Write(supportLine);
        Write("bool LibraGpuFinite(LibraGpuParticle p) { return LibraGpuIsFinite(p.positionAge) && LibraGpuIsFinite(p.velocityLifetime) && LibraGpuIsFinite(p.color) && LibraGpuIsFinite(p.sizeRotation); }");
        for (int kernelIndex = 0; kernelIndex < p.Kernels.Count; kernelIndex++)
        {
            var k = p.Kernels[kernelIndex];
            var meta = kernels[kernelIndex];
            var nodeLookup = k.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var order = GpuCompiler.Topological(nodeLookup);
            var registers = order.Select((n, i) => (n.Id, Name: $"r{i:D4}")).ToDictionary(x => x.Id, x => x.Name, StringComparer.Ordinal);
            var curveNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in order.Where(n => n.Op == GpuOp.FCurve))
            {
                var fnName = $"LibraGpuEvalCurve_{kernelIndex:D4}_{curveNames.Count:D4}";
                curveNames[n.Id] = fnName;
                var (minT, maxT, samples) = (n.Curve ?? GpuCurveData.DefaultLinear()).SampleTable(65);
                var arrName = $"LibraGpuCurveLut_{kernelIndex:D4}_{curveNames.Count - 1:D4}";
                var invSpan = (64.0 / (maxT - minT)).ToString("R", CultureInfo.InvariantCulture);
                var minStr = minT.ToString("R", CultureInfo.InvariantCulture);
                var literals = string.Join(", ", samples.Select(s => s.ToString("R", CultureInfo.InvariantCulture) + "f"));
                Write($"static const float {arrName}[65] = {{ {literals} }};");
                Write($"float {fnName}(float t) {{ float s = clamp((t - {minStr}f) * {invSpan}f, 0.0f, 64.0f); uint idx = min((uint)s, 63u); return lerp({arrName}[idx], {arrName}[idx + 1u], s - (float)idx); }}");
            }
            Write(customFloatStride > 0
                ? $"void LibraGpuEvaluate_{kernelIndex:D4}(uint particleIndex, inout LibraGpuParticle p)"
                : $"void LibraGpuEvaluate_{kernelIndex:D4}(inout LibraGpuParticle p)");
            Write("{");
            foreach (var n in order)
            {
                var args = n.Inputs.Select(id => registers[id]).ToArray();
                var types = n.Inputs.Select(id => nodeLookup[id].Type).ToArray();
                var expression = n.Op == GpuOp.FCurve
                    ? $"{curveNames[n.Id]}({args[0]})"
                    : Expression(n, args, types, resourceIndices, extendedAttributes);
                Mapped($"    {HlslType(n.Type)} {registers[n.Id]} = {expression};", k, n.Id, "out");
            }
            // SSA reads the incoming particle snapshot; attribute stores happen only after evaluation.
            foreach (var w in k.Writes)
            {
                if (extendedAttributes.TryGetValue(w.Attribute, out var ext))
                {
                    var reg = registers[w.Value];
                    var count = ParticleAbi.FloatComponentCount(ext.Type);
                    if (count == 1)
                    {
                        var storeVal = ext.Type switch
                        {
                            GpuType.Bool => $"asfloat({reg} ? 1u : 0u)",
                            GpuType.Int or GpuType.UInt => $"asfloat(uint({reg}))",
                            _ => reg
                        };
                        Mapped($"    LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {ext.FloatOffset}u] = {storeVal};", k, w.Id, "value");
                    }
                    else
                    {
                        for (var c = 0; c < count; c++)
                        {
                            var comp = "xyzw"[c];
                            Mapped($"    LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {ext.FloatOffset + c}u] = {reg}.{comp};", k, w.Id, "value");
                        }
                    }
                    continue;
                }
                var rhs = w.Attribute switch
                {
                    "Alive" => $"({registers[w.Value]} ? 1u : 0u)",
                    "Custom1" or "Custom2" => $"asuint({registers[w.Value]})",
                    _ => registers[w.Value]
                };
                Mapped($"    {WriteAttributeTarget(w.Attribute)} = {rhs};", k, w.Id, "value");
            }
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
                if (customFloatStride > 0)
                    Write("    for (uint ca = 0u; ca < LibraGpuCustomStride; ++ca) { LibraGpuCustomBuffer[i * LibraGpuCustomStride + ca] = 0.0f; }");
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
            Write(customFloatStride > 0
                ? $"    LibraGpuEvaluate_{kernelIndex:D4}(i, p);"
                : $"    LibraGpuEvaluate_{kernelIndex:D4}(p);");
            Write("    p.positionAge.w = max(p.positionAge.w, 0.0f);");
            Write("    if (!LibraGpuFinite(p) || !(p.velocityLifetime.w > 0.0f) || p.positionAge.w >= p.velocityLifetime.w) p.seedAlive.y = 0u;");
            Write("    p.seedAlive.y = p.seedAlive.y != 0u ? 1u : 0u;");
            Write($"    {b.ParticleBuffer}[i] = p;");
            Write("}");
        }
        if (format == ShaderFormat.HlslInclude) Write("#endif");
        return new(format, s.ToString(), mappings, b, p.Resources.Select((r, i) =>
            new ResourceMetadata(r.Id, r.Kind, $"LibraGpuTexture_{i:D4}", b.TextureRegisterBase + i, r.Width, r.Height, r.FlowMap)));
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
        "Custom0" => "p.sizeRotation.w",
        "Custom1" => "asfloat(p.seedAlive.z)",
        "Custom2" => "asfloat(p.seedAlive.w)",
        _ => throw new InvalidOperationException()
    };

    private static string WriteAttributeTarget(string id) => id switch
    {
        "Custom1" => "p.seedAlive.z",
        "Custom2" => "p.seedAlive.w",
        _ => Attribute(id)
    };
    private static string ReadExtendedAttribute(int offset, GpuType type) => type switch
    {
        GpuType.Float => $"LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset}u]",
        GpuType.Float2 => $"float2(LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset}u], LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset + 1}u])",
        GpuType.Float3 => $"float3(LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset}u], LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset + 1}u], LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset + 2}u])",
        GpuType.Float4 => $"float4(LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset}u], LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset + 1}u], LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset + 2}u], LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset + 3}u])",
        GpuType.Int => $"asint(asuint(LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset}u]))",
        GpuType.UInt => $"asuint(LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset}u])",
        GpuType.Bool => $"(asuint(LibraGpuCustomBuffer[particleIndex * LibraGpuCustomStride + {offset}u]) != 0u)",
        _ => throw new InvalidOperationException()
    };

    private static string Expression(GpuNode n, string[] a, GpuType[] types, IReadOnlyDictionary<string, int> resourceIndices,
        IReadOnlyDictionary<string, (int FloatOffset, GpuType Type)> extendedAttributes)
    {
        string Cast(int i) => types[i] == n.Type ? a[i] :
            $"{HlslType(n.Type)}({string.Join(", ", Enumerable.Repeat(a[i], GpuOperationRegistry.Components(n.Type)))})";
        string Call(string f) => $"{f}({string.Join(", ", Enumerable.Range(0, a.Length).Select(Cast))})";
        string RawCall(string f) => $"{f}({string.Join(", ", a)})";
        string PerLane(string f, bool ranges = false)
        {
            string Scalar(char? component)
            {
                var args = Enumerable.Range(0, a.Length).Select(i =>
                    component is not null && !(ranges && i > 0) && GpuOperationRegistry.Components(types[i]) > 1 ? $"{a[i]}.{component}" : a[i]);
                return $"{f}({string.Join(", ", args)})";
            }
            return GpuOperationRegistry.Components(n.Type) == 1 ? Scalar(null) :
                $"{HlslType(n.Type)}({string.Join(", ", Enumerable.Range(0, GpuOperationRegistry.Components(n.Type)).Select(i => Scalar("xyzw"[i])))})";
        }
        return n.Op switch
        {
            GpuOp.Constant => Literal(n),
            GpuOp.ReadAttribute => n.Attribute == "Alive" ? "(p.seedAlive.y != 0u)" :
                extendedAttributes.TryGetValue(n.Attribute!, out var ext) ? ReadExtendedAttribute(ext.FloatOffset, ext.Type) :
                Attribute(n.Attribute!),
            GpuOp.ReadUniform => n.Uniform switch { "deltaTime" => "LibraGpuDeltaTime", "time" => "LibraGpuTime", "emitterPosition" => "LibraGpuEmitterPosition",
                "emitterRow0" => "LibraGpuEmitterRows[0]", "emitterRow1" => "LibraGpuEmitterRows[1]",
                "emitterRow2" => "LibraGpuEmitterRows[2]", "emitterRow3" => "LibraGpuEmitterRows[3]", _ => throw new InvalidOperationException() },
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
            GpuOp.Abs => PerLane("LibraGpuAbs"),
            GpuOp.Floor => PerLane("LibraGpuFloor"),
            GpuOp.Ceil => PerLane("LibraGpuCeil"),
            GpuOp.Round => PerLane("LibraGpuRound"),
            GpuOp.Frac => PerLane("LibraGpuFrac"),
            GpuOp.Mod => PerLane("LibraGpuMod"),
            GpuOp.Sign => PerLane("LibraGpuSign"),
            GpuOp.Pow => PerLane("LibraGpuPow"),
            GpuOp.Sqrt => PerLane("LibraGpuSqrt"),
            GpuOp.Exp => PerLane("LibraGpuExp"),
            GpuOp.Log => PerLane("LibraGpuLog"),
            GpuOp.Lerp => PerLane("LibraGpuLerp"),
            GpuOp.Smoothstep => PerLane("LibraGpuSmoothstep"),
            GpuOp.Saturate => PerLane("LibraGpuSaturate"),
            GpuOp.Remap => PerLane("LibraGpuRemap", true),
            GpuOp.Dot => RawCall("LibraGpuDot"),
            GpuOp.Cross => RawCall("LibraGpuCross"),
            GpuOp.Distance => RawCall("LibraGpuDistance"),
            GpuOp.GradientNoise3D => RawCall("LibraGpuGradientNoise3D"),
            GpuOp.Fbm => RawCall("LibraGpuFbm"),
            GpuOp.Turbulence => RawCall("LibraGpuTurbulence"),
            GpuOp.CurlNoise => RawCall("LibraGpuCurlNoise"),
            GpuOp.SampleFlowMap => $"LibraGpuSampleFlowMap{(n.Type == GpuType.Float2 ? "Uv" : "World")}_{resourceIndices[n.Resource!]:D4}({a[0]})",
            GpuOp.GreaterThan => $"({a[0]} > {a[1]})",
            GpuOp.LessThan => $"({a[0]} < {a[1]})",
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
