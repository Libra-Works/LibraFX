namespace LibraGpu.Core;

public static partial class Fluid2DShaders
{
    private const string WaterCommon = """
int LibraFluidModelId() { return int(LibraFluidModel.x + 0.5); }
float2 LibraFluidBoundWaterSpeed(float2 velocity)
{
    int model = LibraFluidModelId();
    if (model != 1 && model != 2) return velocity;
    return velocity * min(1.0, LibraFluidModel.w / max(length(velocity), 0.000001));
}
bool LibraFluidInside(int2 cell)
{
    return cell.x >= 0 && cell.y >= 0 && cell.x < int(LibraFluidGrid.x) && cell.y < int(LibraFluidGrid.y);
}
float LibraFluidMacRead0(int2 cell, int axis)
{
    if (axis == 0 && (cell.x < 0 || cell.x >= int(LibraFluidGrid.x) - 1)) return 0.0;
    if (axis == 1 && (cell.y < 0 || cell.y >= int(LibraFluidGrid.y) - 1)) return 0.0;
    return axis == 0 ? LibraFluidLoad0(cell).x : LibraFluidLoad0(cell).y;
}
float LibraFluidMacSample0(float2 position, int axis)
{
    float2 bounded = clamp(position, float2(-1, -1), LibraFluidGrid.xy);
    int2 lower = int2(floor(bounded));
    float2 t = bounded - float2(lower);
    return lerp(lerp(LibraFluidMacRead0(lower, axis), LibraFluidMacRead0(lower + int2(1, 0), axis), t.x),
        lerp(LibraFluidMacRead0(lower + int2(0, 1), axis), LibraFluidMacRead0(lower + int2(1, 1), axis), t.x), t.y);
}
float2 LibraFluidMacSampleVelocity0(float2 position)
{
    return float2(LibraFluidMacSample0(position - float2(.5, 0), 0), LibraFluidMacSample0(position - float2(0, .5), 1));
}
float4 LibraFluidWaterOutflow(int2 cell)
{
    float2 center = LibraFluidLoad1(cell).xy;
    float2 inverseCell = LibraFluidGrid.z / LibraFluidCellSize();
    float4 rate = float4(
        max(-LibraFluidLoad1(cell + int2(-1, 0)).x, 0.0) * inverseCell.x,
        max(center.x, 0.0) * inverseCell.x,
        max(-LibraFluidLoad1(cell + int2(0, -1)).y, 0.0) * inverseCell.y,
        max(center.y, 0.0) * inverseCell.y);
    if (cell.x == 0) rate.x = 0.0;
    if (cell.x == int(LibraFluidGrid.x) - 1) rate.y = 0.0;
    if (cell.y == 0) rate.z = 0.0;
    if (cell.y == int(LibraFluidGrid.y) - 1) rate.w = 0.0;
    return rate / max(1.0, rate.x + rate.y + rate.z + rate.w);
}
float LibraFluidWaterNetFlux(int2 cell)
{
    float4 outgoing = LibraFluidWaterOutflow(cell);
    float volume = max(LibraFluidLoad0(cell).w, 0.0);
    float net = -volume * (outgoing.x + outgoing.y + outgoing.z + outgoing.w);
    int2 left = cell + int2(-1, 0), right = cell + int2(1, 0);
    int2 down = cell + int2(0, -1), up = cell + int2(0, 1);
    if (LibraFluidInside(left)) net += LibraFluidWaterOutflow(left).y * max(LibraFluidLoad0(left).w, 0.0);
    if (LibraFluidInside(right)) net += LibraFluidWaterOutflow(right).x * max(LibraFluidLoad0(right).w, 0.0);
    if (LibraFluidInside(down)) net += LibraFluidWaterOutflow(down).w * max(LibraFluidLoad0(down).w, 0.0);
    if (LibraFluidInside(up)) net += LibraFluidWaterOutflow(up).z * max(LibraFluidLoad0(up).w, 0.0);
    return net;
}
float3 LibraShallowLoad(int2 cell)
{
    float3 q = LibraFluidLoad0(cell).xyz;
    q.x = max(q.x, 0.0);
    if (cell.x < 0 || cell.x >= int(LibraFluidGrid.x)) q.y = -q.y;
    if (cell.y < 0 || cell.y >= int(LibraFluidGrid.y)) q.z = -q.z;
    return q;
}
float3 LibraShallowFlux(float3 q, int axis)
{
    float2 velocity = q.yz / max(q.x, 0.000001);
    float momentum = axis == 0 ? q.y : q.z;
    float3 flux = float3(momentum, momentum * velocity);
    float hydrostatic = 0.5 * LibraFluidModel.y * q.x * q.x;
    if (axis == 0) flux.y += hydrostatic;
    else flux.z += hydrostatic;
    return flux;
}
float3 LibraShallowHll(float3 left, float3 right, int axis)
{
    float vl = (axis == 0 ? left.y : left.z) / max(left.x, 0.000001);
    float vr = (axis == 0 ? right.y : right.z) / max(right.x, 0.000001);
    float cl = sqrt(max(LibraFluidModel.y * left.x, 0.0));
    float cr = sqrt(max(LibraFluidModel.y * right.x, 0.0));
    float plus = max(0.0, max(vl + cl, vr + cr));
    float minus = min(0.0, min(vl - cl, vr - cr));
    return (plus * LibraShallowFlux(left, axis) - minus * LibraShallowFlux(right, axis) +
        plus * minus * (right - left)) / max(plus - minus, 0.000001);
}
""";

    private const string InitializeWaterBody = """
    float depth = LibraFluidModel.z;
    if (LibraFluidModelId() == 1)
    {
        float fraction = clamp((depth - float(cell.y) * LibraFluidCellSize().y) / LibraFluidCellSize().y, 0.0, 1.0);
        LibraFluidWrite0[index] = float4(LibraFluidColor.xyz * fraction, fraction);
    }
    else if (LibraFluidModelId() == 2)
        LibraFluidWrite0[index] = float4(depth, 0, 0, 0);
    else
        LibraFluidWrite0[index] = float4(0, 0, 0, 0);
""";

    private const string ShallowWaterBody = """
    float3 center = LibraShallowLoad(cell);
    float2 inverseCell = LibraFluidGrid.z / LibraFluidCellSize();
    float3 q = center - inverseCell.x *
        (LibraShallowHll(center, LibraShallowLoad(cell + int2(1, 0)), 0) -
         LibraShallowHll(LibraShallowLoad(cell + int2(-1, 0)), center, 0)) - inverseCell.y *
        (LibraShallowHll(center, LibraShallowLoad(cell + int2(0, 1)), 1) -
         LibraShallowHll(LibraShallowLoad(cell + int2(0, -1)), center, 1));
    float maximumDepth = LibraFluidModel.z * 16.0;
    float guarded = LibraFluidLoad0(cell).w;
    if (q.x < -0.000001 || q.x > maximumDepth) guarded = 1.0;
    q.x = clamp(q.x, 0.0, maximumDepth);
    float source = LibraFluidSourceWeight(cell);
    q.x += min(maximumDepth - q.x, LibraFluidGrid.z * LibraFluidSource.w * source);
    q.yz += q.x * LibraFluidGrid.z * LibraFluidForce.xy * source;
    q.yz += q.x * LibraFluidGrid.z * LibraFluidAuthoredForce(cell, center.x, 0.0);
    q.yz *= exp(-LibraFluidDomain.z * LibraFluidGrid.z);
    if (q.x <= 0.000001) q.yz = float2(0, 0);
    else q.yz = q.x * LibraFluidBoundWaterSpeed(q.yz / q.x);
    LibraFluidWrite0[index] = float4(q, guarded);
""";
}
