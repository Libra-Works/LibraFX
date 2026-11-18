using System.Collections.ObjectModel;

namespace LibraGpu.Core;

public enum TypePolicy
{
    Literal, Attribute, Uniform, ArithmeticBroadcast, FloatUnary, VectorUnary, Length, Compose, Select, Random, Component,
    NumericUnary, FloatBroadcast, Dot, Cross, Distance, Remap, GradientNoise, FractalNoise, FlowMapSample
}
public sealed record GpuInputDescriptor(string Id, GpuType DefaultType, IReadOnlyList<double> DefaultValue,
    bool OptionalInDsl = false, double? Minimum = null, double? Maximum = null);
public sealed record GpuOperationDescriptor(GpuOp Op, int MinInputs, int MaxInputs, TypePolicy Policy)
{
    public string DisplayName { get; init; } = "";
    public string DslName { get; init; } = "";
    public IReadOnlyList<GpuInputDescriptor> Inputs { get; init; } = [];
}
public static class GpuOperationRegistry
{
    private static GpuInputDescriptor Input(string id, GpuType t, double value = 0, bool optional = false, double? min = null, double? max = null)
        => new(id, t, Array.AsReadOnly(Enumerable.Repeat(value, Components(t)).ToArray()), optional, min, max);
    private static GpuOperationDescriptor Describe(GpuOperationDescriptor d)
    {
        var inputs = d.Policy switch
        {
            TypePolicy.GradientNoise => new[] { Input("position", GpuType.Float3), Input("seed", GpuType.UInt, optional: true) },
            TypePolicy.FractalNoise => new[] { Input("position",GpuType.Float3),Input("seed",GpuType.UInt,optional:true),
                Input("octaves",GpuType.Int,4,true,1,8),Input("gain",GpuType.Float,0.5,true,0,1) },
            TypePolicy.FlowMapSample => new[] { Input("coordinates", GpuType.Float2) },
            TypePolicy.ArithmeticBroadcast => new[] { Input("a", GpuType.Float), Input("b", GpuType.Float, 1) },
            TypePolicy.Remap => new[] { Input("value",GpuType.Float),new GpuInputDescriptor("sourceRange",GpuType.Float2,Array.AsReadOnly(new double[] {0,1})),
                new GpuInputDescriptor("targetRange",GpuType.Float2,Array.AsReadOnly(new double[] {0,1})) },
            TypePolicy.Cross => new[] { Input("a", GpuType.Float3), Input("b", GpuType.Float3) },
            TypePolicy.Dot or TypePolicy.Distance => new[] { Input("a", GpuType.Float3), Input("b", GpuType.Float3) },
            TypePolicy.VectorUnary or TypePolicy.Length or TypePolicy.Component => new[] { Input("value", GpuType.Float3) },
            TypePolicy.Random => new[] { Input("seed", GpuType.UInt) },
            TypePolicy.Select => new[] { Input("condition", GpuType.Bool), Input("trueValue", GpuType.Float), Input("falseValue", GpuType.Float) },
            TypePolicy.Compose => Enumerable.Range(0, d.MaxInputs)
                .Select(i => Input(new[] { "x", "y", "z", "w" }[i], GpuType.Float)).ToArray(),
            TypePolicy.FloatUnary or TypePolicy.NumericUnary => new[] { Input("value", GpuType.Float) },
            _ => Enumerable.Range(0, d.MaxInputs).Select(i => Input($"in{i}", GpuType.Float)).ToArray()
        };
        if (d.Op == GpuOp.Clamp) inputs = [Input("value", GpuType.Float), Input("minimum", GpuType.Float), Input("maximum", GpuType.Float, 1)];
        if (d.Op == GpuOp.Lerp) inputs = [Input("a", GpuType.Float), Input("b", GpuType.Float, 1), Input("amount", GpuType.Float, 0.5)];
        if (d.Op == GpuOp.Smoothstep) inputs = [Input("edge0", GpuType.Float), Input("edge1", GpuType.Float, 1), Input("value", GpuType.Float, 0.5)];
        if (d.Op is GpuOp.Div or GpuOp.Mod or GpuOp.Pow) inputs = [Input("a", GpuType.Float), Input("b", GpuType.Float, 1)];
        return d with
        {
            DisplayName = d.Op switch { GpuOp.GradientNoise3D => "Gradient Noise 3D", GpuOp.Fbm => "FBM", GpuOp.CurlNoise => "Curl Noise", GpuOp.SampleFlowMap => "Flow Map", _ => d.Op.ToString() },
            DslName = d.Op == GpuOp.SampleFlowMap ? "sampleFlowMap" : d.Op.ToString().ToLowerInvariant(),
            Inputs = Array.AsReadOnly(inputs)
        };
    }
    public static IReadOnlyDictionary<GpuOp, GpuOperationDescriptor> Operations { get; } =
        new ReadOnlyDictionary<GpuOp, GpuOperationDescriptor>(new[]
        {
            new GpuOperationDescriptor(GpuOp.Constant,0,0,TypePolicy.Literal),
            new(GpuOp.ReadAttribute,0,0,TypePolicy.Attribute),new(GpuOp.ReadUniform,0,0,TypePolicy.Uniform),
            new(GpuOp.Add,2,2,TypePolicy.ArithmeticBroadcast),new(GpuOp.Sub,2,2,TypePolicy.ArithmeticBroadcast),
            new(GpuOp.Mul,2,2,TypePolicy.ArithmeticBroadcast),new(GpuOp.Div,2,2,TypePolicy.ArithmeticBroadcast),
            new(GpuOp.Min,2,2,TypePolicy.ArithmeticBroadcast),new(GpuOp.Max,2,2,TypePolicy.ArithmeticBroadcast),
            new(GpuOp.Clamp,3,3,TypePolicy.ArithmeticBroadcast),
            new(GpuOp.Sin,1,1,TypePolicy.FloatUnary),new(GpuOp.Cos,1,1,TypePolicy.FloatUnary),
            new(GpuOp.Normalize,1,1,TypePolicy.VectorUnary),new(GpuOp.Length,1,1,TypePolicy.Length),
            new(GpuOp.Compose,2,4,TypePolicy.Compose),new(GpuOp.Select,3,3,TypePolicy.Select),
            new(GpuOp.Random,1,1,TypePolicy.Random),new(GpuOp.Component,1,1,TypePolicy.Component),
            new(GpuOp.Abs,1,1,TypePolicy.NumericUnary),new(GpuOp.Floor,1,1,TypePolicy.FloatUnary),
            new(GpuOp.Ceil,1,1,TypePolicy.FloatUnary),new(GpuOp.Round,1,1,TypePolicy.FloatUnary),
            new(GpuOp.Frac,1,1,TypePolicy.FloatUnary),new(GpuOp.Mod,2,2,TypePolicy.ArithmeticBroadcast),
            new(GpuOp.Sign,1,1,TypePolicy.NumericUnary),new(GpuOp.Pow,2,2,TypePolicy.FloatBroadcast),
            new(GpuOp.Sqrt,1,1,TypePolicy.FloatUnary),new(GpuOp.Exp,1,1,TypePolicy.FloatUnary),
            new(GpuOp.Log,1,1,TypePolicy.FloatUnary),new(GpuOp.Lerp,3,3,TypePolicy.FloatBroadcast),
            new(GpuOp.Smoothstep,3,3,TypePolicy.FloatBroadcast),new(GpuOp.Remap,3,3,TypePolicy.Remap),
            new(GpuOp.Dot,2,2,TypePolicy.Dot),new(GpuOp.Cross,2,2,TypePolicy.Cross),
            new(GpuOp.Distance,2,2,TypePolicy.Distance),new(GpuOp.Saturate,1,1,TypePolicy.FloatUnary),
            new(GpuOp.GradientNoise3D,2,2,TypePolicy.GradientNoise),new(GpuOp.Fbm,4,4,TypePolicy.FractalNoise),
            new(GpuOp.Turbulence,4,4,TypePolicy.FractalNoise),new(GpuOp.CurlNoise,4,4,TypePolicy.FractalNoise),
            new(GpuOp.SampleFlowMap,1,1,TypePolicy.FlowMapSample)
        }.Select(Describe).ToDictionary(x => x.Op));

    public static bool TryInferType(GpuOp op, IReadOnlyList<GpuType> inputs, out GpuType result)
    {
        result = GpuType.Float;
        if (!Operations.TryGetValue(op, out var d) || inputs is null || inputs.Count < d.MinInputs || inputs.Count > d.MaxInputs ||
            inputs.Any(t => !Enum.IsDefined(t) || t == GpuType.Quaternion)) return false;
        if (d.Policy is TypePolicy.Literal or TypePolicy.Attribute or TypePolicy.Uniform) return false;
        result = d.Policy switch
        {
            TypePolicy.Length or TypePolicy.Random or TypePolicy.Component or TypePolicy.Dot or TypePolicy.Distance or TypePolicy.GradientNoise => GpuType.Float,
            TypePolicy.Cross => GpuType.Float3,
            TypePolicy.FractalNoise => op == GpuOp.CurlNoise ? GpuType.Float3 : GpuType.Float,
            TypePolicy.FlowMapSample => inputs[0],
            TypePolicy.Compose => inputs.Count switch { 2 => GpuType.Float2, 3 => GpuType.Float3, 4 => GpuType.Float4, _ => GpuType.Float },
            TypePolicy.Select => inputs[1],
            TypePolicy.ArithmeticBroadcast or TypePolicy.FloatBroadcast => inputs.OrderByDescending(Components).First(),
            _ => inputs[0]
        };
        return TypesMatch(op, result, inputs);
    }
    public static bool TypesMatch(GpuOp op, GpuType result, IReadOnlyList<GpuType> inputs)
    {
        if (!Operations.TryGetValue(op, out var d) || inputs.Count < d.MinInputs || inputs.Count > d.MaxInputs ||
            !Enum.IsDefined(result) || result == GpuType.Quaternion || inputs.Any(t => !Enum.IsDefined(t) || t == GpuType.Quaternion)) return false;
        return d.Policy switch
        {
            TypePolicy.ArithmeticBroadcast => (Float(result) || result is GpuType.Int or GpuType.UInt) &&
                inputs.All(t => Broadcast(t, result)) && inputs.Contains(result),
            TypePolicy.FloatBroadcast => Float(result) && inputs.All(t => Broadcast(t, result)) && inputs.Contains(result),
            TypePolicy.FloatUnary => Float(result) && inputs[0] == result,
            TypePolicy.NumericUnary => (Float(result) || result is GpuType.Int or GpuType.UInt) && inputs[0] == result,
            TypePolicy.VectorUnary => Float(result) && Components(result) > 1 && inputs[0] == result,
            TypePolicy.Length => result == GpuType.Float && Float(inputs[0]) && Components(inputs[0]) > 1,
            TypePolicy.Compose => Float(result) && Components(result) == inputs.Count && inputs.All(t => t == GpuType.Float),
            TypePolicy.Select => inputs[0] == GpuType.Bool && inputs[1] == result && inputs[2] == result,
            TypePolicy.Random => inputs[0] == GpuType.UInt && result == GpuType.Float,
            TypePolicy.Component => inputs[0] is GpuType.Float2 or GpuType.Float3 or GpuType.Float4 && result == GpuType.Float,
            TypePolicy.Dot => result == GpuType.Float && inputs[0] == inputs[1] && Float(inputs[0]) && Components(inputs[0]) > 1,
            TypePolicy.Cross => result == GpuType.Float3 && inputs.All(t => t == GpuType.Float3),
            TypePolicy.Distance => result == GpuType.Float && inputs[0] == inputs[1] && Float(inputs[0]),
            TypePolicy.Remap => Float(result) && inputs[0] == result && inputs[1] == GpuType.Float2 && inputs[2] == GpuType.Float2,
            TypePolicy.GradientNoise => result == GpuType.Float && inputs[0] == GpuType.Float3 && inputs[1] == GpuType.UInt,
            TypePolicy.FractalNoise => result == (op == GpuOp.CurlNoise ? GpuType.Float3 : GpuType.Float) &&
                inputs[0] == GpuType.Float3 && inputs[1] == GpuType.UInt && inputs[2] == GpuType.Int && inputs[3] == GpuType.Float,
            TypePolicy.FlowMapSample => result == inputs[0] && (inputs[0] is GpuType.Float2 or GpuType.Float3),
            _ => false
        };
    }
    public static IReadOnlyList<GpuInputDescriptor> GetDefaultInputs(GpuOp op, GpuType preferredResult = GpuType.Float)
    {
        var d = Operations[op];
        if (d.Policy == TypePolicy.FlowMapSample)
        {
            var coordinates = preferredResult == GpuType.Float3 ? GpuType.Float3 : GpuType.Float2;
            return [Input("coordinates", coordinates)];
        }
        if (d.Policy is not (TypePolicy.ArithmeticBroadcast or TypePolicy.FloatBroadcast or TypePolicy.FloatUnary or TypePolicy.NumericUnary or TypePolicy.Select or TypePolicy.VectorUnary or TypePolicy.Remap))
            return d.Inputs;
        if (!Float(preferredResult) && preferredResult is not (GpuType.Int or GpuType.UInt or GpuType.Bool))
            throw new ArgumentOutOfRangeException(nameof(preferredResult));
        var defaults = Array.AsReadOnly(d.Inputs.Select((input, i) =>
        {
            if (d.Policy == TypePolicy.Remap && i > 0) return input;
            var t = d.Policy == TypePolicy.Select && i == 0 ? GpuType.Bool : preferredResult;
            return input with { DefaultType = t, DefaultValue = Array.AsReadOnly(Enumerable.Repeat(input.DefaultValue[0], Components(t)).ToArray()) };
        }).ToArray());
        return TryInferType(op, defaults.Select(x => x.DefaultType).ToArray(), out _) ? defaults : d.Inputs;
    }
    public static bool TryGetDslOperation(string name, out GpuOp op)
    {
        foreach (var d in Operations.Values)
            if (d.DslName == name && d.Policy is not (TypePolicy.Literal or TypePolicy.Attribute or TypePolicy.Uniform or TypePolicy.Component))
            { op = d.Op; return true; }
        op = default; return false;
    }
    internal static int Components(GpuType t) => t switch { GpuType.Float2 => 2, GpuType.Float3 => 3, GpuType.Float4 => 4, _ => 1 };
    internal static bool Float(GpuType t) => t is GpuType.Float or GpuType.Float2 or GpuType.Float3 or GpuType.Float4;
    internal static bool Broadcast(GpuType a, GpuType result) => a == result || a == GpuType.Float && Float(result);
}
