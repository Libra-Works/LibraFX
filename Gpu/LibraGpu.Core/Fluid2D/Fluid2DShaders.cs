using System.Text;

namespace LibraGpu.Core;

public static partial class Fluid2DShaders
{
    private const string Common = """
float2 LibraFluidCellSize()
{
    return LibraFluidDomain.xy / LibraFluidGrid.xy;
}
uint LibraFluidIndex(int2 cell)
{
    int2 p = clamp(cell, int2(0, 0), int2(LibraFluidGrid.xy) - int2(1, 1));
    return uint(p.y) * uint(LibraFluidGrid.x) + uint(p.x);
}
float4 LibraFluidLoad0(int2 cell)
{
    return LibraFluidRead0[LibraFluidIndex(cell)];
}
float4 LibraFluidLoad1(int2 cell)
{
    return LibraFluidRead1[LibraFluidIndex(cell)];
}
float4 LibraFluidSample0(float2 position)
{
    float2 p = clamp(position, float2(0, 0), LibraFluidGrid.xy - float2(1, 1));
    int2 lower = int2(floor(p));
    float2 fraction = p - float2(lower);
    return lerp(lerp(LibraFluidLoad0(lower), LibraFluidLoad0(lower + int2(1, 0)), fraction.x),
        lerp(LibraFluidLoad0(lower + int2(0, 1)), LibraFluidLoad0(lower + int2(1, 1)), fraction.x), fraction.y);
}
float2 LibraFluidVelocity0(int2 cell)
{
    float2 velocity = LibraFluidLoad0(cell).xy;
    if (cell.x < 0 || cell.x >= int(LibraFluidGrid.x))
    {
        velocity.x = -velocity.x;
    }
    if (cell.y < 0 || cell.y >= int(LibraFluidGrid.y))
    {
        velocity.y = -velocity.y;
    }
    return velocity;
}
float2 LibraFluidBoundary(float2 velocity, int2 cell)
{
    if (int(LibraFluidModel.x + 0.5) == 1)
    {
        if (cell.x == int(LibraFluidGrid.x) - 1) velocity.x = 0.0;
        if (cell.y == int(LibraFluidGrid.y) - 1) velocity.y = 0.0;
        return velocity;
    }
    if (cell.x == 0 || cell.x == int(LibraFluidGrid.x) - 1)
    {
        velocity.x = 0;
    }
    if (cell.y == 0 || cell.y == int(LibraFluidGrid.y) - 1)
    {
        velocity.y = 0;
    }
    return velocity;
}
float LibraFluidCurl0(int2 cell)
{
    float2 inverseCell = float2(1, 1) / LibraFluidCellSize();
    return 0.5 * ((LibraFluidVelocity0(cell + int2(1, 0)).y -
        LibraFluidVelocity0(cell - int2(1, 0)).y) * inverseCell.x -
        (LibraFluidVelocity0(cell + int2(0, 1)).x -
        LibraFluidVelocity0(cell - int2(0, 1)).x) * inverseCell.y);
}
float2 LibraFluidConfinement0(int2 cell)
{
    if (LibraFluidGrid.w <= 0.0) return float2(0, 0);
    float2 cellSize = LibraFluidCellSize();
    float2 gradient = 0.5 * float2(
        abs(LibraFluidCurl0(cell + int2(1, 0))) - abs(LibraFluidCurl0(cell - int2(1, 0))),
        abs(LibraFluidCurl0(cell + int2(0, 1))) - abs(LibraFluidCurl0(cell - int2(0, 1)))) / cellSize;
    float magnitude = length(gradient);
    float2 direction = gradient / max(magnitude, 0.000001);
    // N cross (0,0,curl). Cell-size scaling keeps the user coefficient independent of grid resolution.
    return LibraFluidGrid.w * min(cellSize.x, cellSize.y) *
        LibraFluidCurl0(cell) * float2(direction.y, -direction.x);
}
float LibraFluidSourceWeight(int2 cell)
{
    float2 uv = (float2(cell) + float2(0.5, 0.5)) / LibraFluidGrid.xy;
    float distanceFromSource = length((uv - LibraFluidSource.xy) * LibraFluidDomain.xy);
    float weight = clamp(1.0 - distanceFromSource / LibraFluidSource.z, 0.0, 1.0);
    return weight * weight * LibraFluidForce.w;
}
""" + WaterCommon + ForceCommon;

    private static string Body(Fluid2DPass pass) => pass switch
    {
        Fluid2DPass.Clear => """
    LibraFluidWrite0[index] = float4(0, 0, 0, 0);
""",
        Fluid2DPass.AdvectVelocity => """
    float2 velocity = LibraFluidLoad0(cell).xy;
    float2 previous = float2(cell) - LibraFluidGrid.z * velocity / LibraFluidCellSize();
    float2 advected;
    if (LibraFluidModelId() == 1)
    {
        float2 uFace = float2(cell) + float2(0.5, 0);
        float2 vFace = float2(cell) + float2(0, 0.5);
        float2 previousU = uFace - LibraFluidGrid.z * LibraFluidMacSampleVelocity0(uFace) / LibraFluidCellSize();
        float2 previousV = vFace - LibraFluidGrid.z * LibraFluidMacSampleVelocity0(vFace) / LibraFluidCellSize();
        advected = float2(LibraFluidMacSample0(previousU - float2(0.5, 0), 0), LibraFluidMacSample0(previousV - float2(0, 0.5), 1));
    }
    else advected = LibraFluidSample0(previous).xy;
    advected *= exp(-LibraFluidDomain.z * LibraFluidGrid.z);
    LibraFluidWrite0[index] = float4(LibraFluidBoundary(advected, cell), 0, 0);
""",
        Fluid2DPass.ApplyForces => """
    float2 velocity = LibraFluidLoad0(cell).xy;
    float density = max(LibraFluidLoad1(cell).w, 0.0);
    velocity += LibraFluidGrid.z * LibraFluidForce.xy * LibraFluidSourceWeight(cell);
    if (LibraFluidModelId() == 1)
        velocity.y -= LibraFluidGrid.z * LibraFluidModel.y * (max(density, LibraFluidLoad1(cell + int2(0, 1)).w) > 0.01 ? 1.0 : 0.0);
    else if (LibraFluidModelId() == 3)
        velocity.y += LibraFluidGrid.z * LibraFluidForce.z * max(LibraFluidLoad1(cell).y - 0.1 * LibraFluidLoad1(cell).z, 0.0);
    else
        velocity.y += LibraFluidGrid.z * LibraFluidForce.z * density;
    velocity += LibraFluidGrid.z * LibraFluidConfinement0(cell);
    velocity += LibraFluidGrid.z * LibraFluidAuthoredForce(cell, LibraFluidModelId() == 3 ? LibraFluidLoad1(cell).z : density,
        LibraFluidModelId() == 3 ? LibraFluidLoad1(cell).y : 0.0);
    LibraFluidWrite0[index] = float4(LibraFluidBoundary(LibraFluidBoundWaterSpeed(velocity), cell), 0, 0);
""",
        Fluid2DPass.Divergence => """
    float2 inverseCell = float2(1, 1) / LibraFluidCellSize();
    float divergence = 0.5 * ((LibraFluidVelocity0(cell + int2(1, 0)).x -
        LibraFluidVelocity0(cell - int2(1, 0)).x) * inverseCell.x +
        (LibraFluidVelocity0(cell + int2(0, 1)).y -
        LibraFluidVelocity0(cell - int2(0, 1)).y) * inverseCell.y);
    if (LibraFluidModelId() == 1)
        divergence = (LibraFluidMacRead0(cell, 0) - LibraFluidMacRead0(cell + int2(-1, 0), 0)) * inverseCell.x +
            (LibraFluidMacRead0(cell, 1) - LibraFluidMacRead0(cell + int2(0, -1), 1)) * inverseCell.y;
    float wet = LibraFluidModelId() == 1 ? max(LibraFluidLoad1(cell).w, 0.0) : 1.0;
    LibraFluidWrite0[index] = float4(divergence, wet, 0, 0);
""",
        Fluid2DPass.Jacobi => """
    float2 inverseCell = float2(1, 1) / LibraFluidCellSize();
    float2 weight = inverseCell * inverseCell;
    float horizontal = LibraFluidLoad0(cell + int2(1, 0)).x + LibraFluidLoad0(cell - int2(1, 0)).x;
    float vertical = LibraFluidLoad0(cell + int2(0, 1)).x + LibraFluidLoad0(cell - int2(0, 1)).x;
    float pressure = (horizontal * weight.x + vertical * weight.y - LibraFluidLoad1(cell).x) /
        (2.0 * (weight.x + weight.y));
    if (LibraFluidModelId() == 1 && LibraFluidLoad1(cell).y <= 0.01) pressure = 0.0;
    LibraFluidWrite0[index] = float4(pressure, 0, 0, 0);
""",
        Fluid2DPass.ProjectVelocity => """
    float2 inverseCell = float2(1, 1) / LibraFluidCellSize();
    float2 gradient = 0.5 * float2(
        LibraFluidLoad1(cell + int2(1, 0)).x - LibraFluidLoad1(cell - int2(1, 0)).x,
        LibraFluidLoad1(cell + int2(0, 1)).x - LibraFluidLoad1(cell - int2(0, 1)).x) * inverseCell;
    float2 velocity = LibraFluidLoad0(cell).xy - gradient;
    if (LibraFluidModelId() == 1)
    {
        gradient = float2(LibraFluidLoad1(cell + int2(1, 0)).x - LibraFluidLoad1(cell).x,
            LibraFluidLoad1(cell + int2(0, 1)).x - LibraFluidLoad1(cell).x) * inverseCell;
        velocity = LibraFluidLoad0(cell).xy - gradient;
    }
    LibraFluidWrite0[index] = float4(LibraFluidBoundary(LibraFluidBoundWaterSpeed(velocity), cell), 0, 0);
""",
        Fluid2DPass.AdvectDye => """
    float2 velocity = LibraFluidLoad1(cell).xy;
    float2 previous = float2(cell) - LibraFluidGrid.z * velocity / LibraFluidCellSize();
    float4 nextDye;
    float injection = LibraFluidSource.w * LibraFluidSourceWeight(cell) * LibraFluidGrid.z;
    float4 sampledDye = max(LibraFluidSample0(previous), float4(0, 0, 0, 0));
    float densityDecay = exp(-LibraFluidDomain.w * LibraFluidGrid.z);
    if (LibraFluidModelId() == 1)
    {
        float volume = max(LibraFluidLoad0(cell).w, 0.0);
        volume += LibraFluidWaterNetFlux(cell);
        volume += min(max(1.0 - volume, 0.0), injection);
        volume = max(volume, 0.0);
        nextDye = float4(LibraFluidColor.xyz * volume, volume);
    }
    else if (LibraFluidModelId() == 3)
    {
        float4 chemistry = sampledDye;
        chemistry.y = lerp(chemistry.y, LibraFluidModel.w, clamp(injection, 0.0, 1.0));
        chemistry.x += injection;
        float ignition = clamp((chemistry.y - LibraFluidReaction.y) / max(LibraFluidReaction.y, 0.000001), 0.0, 1.0);
        float burnt = chemistry.x * (1.0 - exp(-LibraFluidReaction.x * ignition * LibraFluidGrid.z));
        chemistry.x -= burnt;
        chemistry.y = (chemistry.y + burnt * LibraFluidReaction.z) * exp(-LibraFluidReaction.w * LibraFluidGrid.z);
        chemistry.z = (chemistry.z + burnt) * densityDecay;
        chemistry.w = burnt / max(LibraFluidGrid.z, 0.000001);
        nextDye = chemistry;
    }
    else
    {
        float4 dye = sampledDye * densityDecay;
        float smokeInjection = injection * LibraFluidColor.w;
        dye += float4(LibraFluidColor.xyz * smokeInjection, smokeInjection);
        nextDye = max(dye, float4(0, 0, 0, 0));
    }
    LibraFluidWrite0[index] = nextDye;
""",
        Fluid2DPass.Publish => """
    if (LibraFluidModelId() == 2)
    {
        float4 water = LibraFluidLoad1(cell);
        LibraFluidWrite0[index] = float4(water.yz / max(water.x, 0.000001), 0, 0);
        LibraFluidWrite1[index] = water;
        return;
    }
    LibraFluidWrite0[index] = LibraFluidLoad0(cell);
    LibraFluidWrite1[index] = LibraFluidLoad1(cell);
""",
        Fluid2DPass.RenderDye => """
    float4 dye = LibraFluidLoad0(cell);
    float density = max(dye.w, 0.0);
    float alpha = 1.0 - exp(-density);
    float3 color = clamp(dye.xyz / max(density, 0.000001), float3(0, 0, 0), float3(1, 1, 1));
    float4 outputColor = float4(color * alpha, alpha);
    if (LibraFluidModelId() == 1)
    {
        alpha = clamp(density, 0.0, 1.0) * LibraFluidColor.w;
        outputColor = float4(LibraFluidColor.xyz * alpha, alpha);
    }
    else if (LibraFluidModelId() == 2)
    {
        float height = max(dye.x, 0.0);
        float shade = clamp(0.65 + (height - LibraFluidModel.z) / max(LibraFluidModel.z, 0.0001), 0.0, 1.0);
        alpha = (1.0 - exp(-height * 4.0)) * LibraFluidColor.w;
        outputColor = float4(LibraFluidColor.xyz * shade * alpha, alpha);
    }
    else if (LibraFluidModelId() == 3)
    {
        float heat = max(dye.y - LibraFluidReaction.y, 0.0);
        float emission = clamp(heat, 0.0, 4.0);
        color = clamp(float3(emission * 2.0, emission * 0.7, emission * 0.2), float3(0, 0, 0), float3(1, 1, 1));
        color = max(color, float3(0.12, 0.12, 0.12));
        alpha = 1.0 - exp(-(max(dye.z, 0.0) + heat * 0.25));
        outputColor = float4(color * alpha, alpha);
    }
""",
        Fluid2DPass.InitializeWater => InitializeWaterBody,
        Fluid2DPass.RenderColor => Body(Fluid2DPass.RenderDye) + "\n    outputColor.rgb /= max(outputColor.a, 0.000001);\n",
        Fluid2DPass.ShallowWater => ShallowWaterBody,
        Fluid2DPass.RenderFields => """
    float2 velocity = LibraFluidLoad0(cell).xy;
    if (LibraFluidModelId() == 1)
        velocity = 0.5 * float2(LibraFluidMacRead0(cell, 0) + LibraFluidMacRead0(cell + int2(-1, 0), 0),
            LibraFluidMacRead0(cell, 1) + LibraFluidMacRead0(cell + int2(0, -1), 1));
    float4 state = LibraFluidLoad1(cell);
    float density = max(state.w, 0.0), temperature = 0.0;
    if (LibraFluidModelId() == 2) density = max(state.x, 0.0);
    else if (LibraFluidModelId() == 3) { density = max(state.z, 0.0); temperature = max(state.y, 0.0); }
    float4 outputColor = float4(velocity, density, temperature);
""",
        _ => throw new ArgumentOutOfRangeException(nameof(pass))
    };

    public static string EmitHlsl(ShaderFormat format)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
        var source = new StringBuilder();
        if (format == ShaderFormat.UnityCompute)
        {
            foreach (var pass in Enum.GetValues<Fluid2DPass>())
            {
                source.AppendLine("#pragma kernel LibraFluid" + pass);
            }
        }
        else if (format == ShaderFormat.Usf)
        {
            source.AppendLine("#include \"/Engine/Public/Platform.ush\"");
        }
        source.AppendLine("""
// Generated by Fluid2DShaders. Each pass reads immutable inputs and writes distinct resources.
StructuredBuffer<float4> LibraFluidRead0 : register(t0);
StructuredBuffer<float4> LibraFluidRead1 : register(t1);
RWStructuredBuffer<float4> LibraFluidWrite0 : register(u0);
RWStructuredBuffer<float4> LibraFluidWrite1 : register(u1);
RWTexture2D<float4> LibraFluidOutput : register(u2);
float4 LibraFluidGrid;
float4 LibraFluidDomain;
float4 LibraFluidSource;
float4 LibraFluidForce;
float4 LibraFluidColor;
float4 LibraFluidModel;
float4 LibraFluidReaction;
float4 LibraFluidForceNodes[25];
""");
        source.AppendLine(Common);
        foreach (var pass in Enum.GetValues<Fluid2DPass>())
        {
            source.AppendLine("[numthreads(8, 8, 1)]");
            source.AppendLine($"void LibraFluid{pass}(uint3 threadId : SV_DispatchThreadID)");
            source.AppendLine("{");
            source.AppendLine("    if (threadId.x >= uint(LibraFluidGrid.x) || threadId.y >= uint(LibraFluidGrid.y))");
            source.AppendLine("    {");
            source.AppendLine("        return;");
            source.AppendLine("    }");
            source.AppendLine("    int2 cell = int2(threadId.xy);");
            source.AppendLine("    uint index = LibraFluidIndex(cell);");
            source.AppendLine(Body(pass));
            if (pass is Fluid2DPass.RenderDye or Fluid2DPass.RenderFields or Fluid2DPass.RenderColor)
            {
                source.AppendLine(pass == Fluid2DPass.RenderColor && format == ShaderFormat.Usf
                    ? "    LibraFluidOutput[int2(cell.x, int(LibraFluidGrid.y) - 1 - cell.y)] = outputColor;"
                    : "    LibraFluidOutput[cell] = outputColor;");
            }
            source.AppendLine("}");
        }
        return source.ToString().Replace("\r\n", "\n");
    }

    public static string EmitGlsl(Fluid2DPass pass)
    {
        var body = Body(pass);
        return ("""
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
#define float2 vec2
#define float3 vec3
#define float4 vec4
#define int2 ivec2
#define uint2 uvec2
#define lerp mix
layout(set = 0, binding = 0, std430) readonly buffer Read0Block { vec4 LibraFluidRead0[]; };
layout(set = 0, binding = 1, std430) readonly buffer Read1Block { vec4 LibraFluidRead1[]; };
layout(set = 0, binding = 2, std430) writeonly buffer Write0Block { vec4 LibraFluidWrite0[]; };
layout(set = 0, binding = 3, std430) writeonly buffer Write1Block { vec4 LibraFluidWrite1[]; };
layout(set = 0, binding = 4, std140) uniform Parameters
{
    vec4 LibraFluidGrid;
    vec4 LibraFluidDomain;
    vec4 LibraFluidSource;
    vec4 LibraFluidForce;
    vec4 LibraFluidColor;
    vec4 LibraFluidModel;
    vec4 LibraFluidReaction;
    vec4 LibraFluidForceNodes[25];
};
layout(set = 0, binding = 5, rgba16f) uniform writeonly image2D LibraFluidOutput;
""".Replace("rgba16f", pass == Fluid2DPass.RenderFields ? "rgba32f" : "rgba16f") + "\n" + Common + "\n" + """
void main()
{
    uint2 threadId = gl_GlobalInvocationID.xy;
    if (threadId.x >= uint(LibraFluidGrid.x) || threadId.y >= uint(LibraFluidGrid.y))
    {
        return;
    }
    int2 cell = int2(threadId);
    uint index = LibraFluidIndex(cell);
""" + "\n" + body + (pass is Fluid2DPass.RenderDye or Fluid2DPass.RenderFields or Fluid2DPass.RenderColor
            ? pass == Fluid2DPass.RenderColor
                ? "\n    imageStore(LibraFluidOutput, int2(cell.x, int(LibraFluidGrid.y) - 1 - cell.y), outputColor);"
                : "\n    imageStore(LibraFluidOutput, cell, outputColor);" : "") + "\n}\n").Replace("\r\n", "\n");
    }

    public static string EmitGodotHeader()
    {
        var source = new StringBuilder("""
#pragma once
// Generated by Fluid2DShaders; edit the portable generator, not this file.
#include "liberafx/gpu/LbFluid2D.h"

namespace liberafx::gpu {
inline auto fluid2DComputeSource(Fluid2DPass pass) noexcept -> const char*
{
    switch (pass)
    {
""");
        foreach (var pass in Enum.GetValues<Fluid2DPass>())
        {
            source.AppendLine();
            source.AppendLine($"    case Fluid2DPass::{pass}:");
            source.Append("        return R\"lf(").Append(EmitGlsl(pass)).AppendLine(")lf\";");
        }
        source.AppendLine("""
    }
    return "";
}
}
""");
        return source.ToString().Replace("\r\n", "\n");
    }
}
