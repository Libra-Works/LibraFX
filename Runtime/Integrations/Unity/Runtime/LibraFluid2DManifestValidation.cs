using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LiberaFx.Unity
{
    internal sealed class LiberaFxGeneratedGpuResource
    {
        internal string resourceId, producerNodeId, outputId;
    }

    internal sealed class LiberaFxGpuResourceManifest
    {
        internal readonly Dictionary<string, LiberaFxGeneratedGpuResource[]> generated = new Dictionary<string, LiberaFxGeneratedGpuResource[]>(StringComparer.Ordinal);
        internal readonly Dictionary<string, bool> automaticPlanes = new Dictionary<string, bool>(StringComparer.Ordinal);
        internal readonly Dictionary<string, LibraFluid2DModelParameters> models = new Dictionary<string, LibraFluid2DModelParameters>(StringComparer.Ordinal);
        internal LiberaFxGeneratedRenderTexturePayload[] drawBindings = System.Array.Empty<LiberaFxGeneratedRenderTexturePayload>();
    }
    internal sealed class LibraFluid2DModelParameters
    {
        internal string model = "Smoke";
        internal float gravity = 9.81f, initialWaterDepth = .25f, maximumWaterSpeed = 4, fuelBurnRate = 4;
        internal float ignitionTemperature = .5f, heatYield = 2, coolingRate = 1, sourceTemperature = 1;
        internal LibraFluid2DForceNode[] forces = System.Array.Empty<LibraFluid2DForceNode>();
    }
    // JsonUtility silently accepts missing fields and duplicate keys. The current
    // manifest schema is checked before any GPU state is created.
    internal static partial class LibraFluid2DManifestValidation
    {
        private static readonly string[] IntegerFields =
            { "version", "width", "height", "pressureIterations", "maximumSubsteps" };
        private static readonly string[] FloatFields =
        {
            "domainWidth", "domainHeight", "maximumStep", "velocityDissipation", "densityDissipation",
            "sourceX", "sourceY", "sourceRadius", "sourceDensity", "sourceVelocityX", "sourceVelocityY",
            "buoyancy", "colorR", "colorG", "colorB", "colorA"
        };

        internal static void Validate(string json) => Read(json);

        internal static LiberaFxGpuResourceManifest Read(string json)
        {
            var result = new LiberaFxGpuResourceManifest();
            var root = Object(new Reader(json).Read(), "GPU manifest");
            if (!root.TryGetValue("version", out var version) || !(version is double n) || n != 4)
                throw new InvalidOperationException("GPU manifest must use the current schema.");
            var programs = Array(root, "programs");
            var fluids = OptionalArray(root, "fluidPrograms");
            if (programs.Count + fluids.Count > 16)
                throw new InvalidOperationException("GPU manifest program count is invalid.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in programs)
            {
                var program = Object(item, "particle program");
                ValidateId(program, ids);
                if (program.TryGetValue("unreal", out var deliveryValue))
                {
                    var delivery = Object(deliveryValue, "particle delivery");
                    if (delivery.TryGetValue("generatedResources", out var generatedValue))
                    {
                        var generated = Object(generatedValue, "generated resources");
                        if (generated.Count > 64) throw new InvalidOperationException("Too many generated resources.");
                        var bindings = new List<LiberaFxGeneratedGpuResource>();
                        foreach (var pair in generated)
                        {
                            var reference = Object(pair.Value, "generated reference");
                            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || reference.Count != 2 ||
                                !(Required(reference, "producerNodeId") is string producer) ||
                                !LiberaFxUnityBindingLookup.TryCanonicalGuid(producer, out var producerId) ||
                                !Guid.TryParse(producerId, out var guid) || guid == Guid.Empty ||
                                !(Required(reference, "outputId") is string output) ||
                                output != "DyeOutput" && output != "ColorOutput" && output != "FieldOutput" && output != "VelocityOutput" && output != "DensityOutput")
                                throw new InvalidOperationException("Invalid generated texture reference.");
                            bindings.Add(new LiberaFxGeneratedGpuResource { resourceId = pair.Key, producerNodeId = producerId, outputId = output });
                        }
                        result.generated.Add(CanonicalNodeId(program), bindings.ToArray());
                    }
                }
            }
            var fluidIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in fluids)
            {
                var fluid = Object(item, "fluid program");
                ValidateId(fluid, ids);
                var fluidId = CanonicalNodeId(fluid);
                fluidIds.Add(fluidId);
                var automatic = true;
                if (fluid.TryGetValue("automaticPlane", out var planeValue))
                {
                    if (!(planeValue is bool plane))
                        throw new InvalidOperationException("Automatic plane selection must be a boolean.");
                    automatic = plane;
                }
                result.automaticPlanes.Add(fluidId, automatic);
                var settings = Object(Required(fluid, "settings"), "fluid settings");
                var hasVorticity = settings.TryGetValue("vorticity", out var vorticity);
                var parameters = new LibraFluid2DModelParameters();
                var extraFields = 0;
                if (settings.TryGetValue("model", out var modelValue))
                {
                    if (!(modelValue is string model) || model != "Smoke" && model != "FreeSurface" && model != "ShallowWater" && model != "Combustion")
                        throw new InvalidOperationException("Invalid fluid simulation model.");
                    parameters.model = model; extraFields++;
                }
                parameters.gravity = OptionalFloat(settings, "gravity", parameters.gravity, ref extraFields);
                parameters.initialWaterDepth = OptionalFloat(settings, "initialWaterDepth", parameters.initialWaterDepth, ref extraFields);
                parameters.maximumWaterSpeed = OptionalFloat(settings, "maximumWaterSpeed", parameters.maximumWaterSpeed, ref extraFields);
                parameters.fuelBurnRate = OptionalFloat(settings, "fuelBurnRate", parameters.fuelBurnRate, ref extraFields);
                parameters.ignitionTemperature = OptionalFloat(settings, "ignitionTemperature", parameters.ignitionTemperature, ref extraFields);
                parameters.heatYield = OptionalFloat(settings, "heatYield", parameters.heatYield, ref extraFields);
                parameters.coolingRate = OptionalFloat(settings, "coolingRate", parameters.coolingRate, ref extraFields);
                parameters.sourceTemperature = OptionalFloat(settings, "sourceTemperature", parameters.sourceTemperature, ref extraFields);
                if (settings.TryGetValue("forces", out var forceValue))
                {
                    extraFields++;
                    if (!(forceValue is List<object> forceList) || forceList.Count > 8)
                        throw new InvalidOperationException("Invalid cell-force node list.");
                    var forces = new List<LibraFluid2DForceNode>();
                    foreach (var forceItem in forceList)
                    {
                        var force = Object(forceItem, "cell-force node");
                        var references = (force.ContainsKey("inputA") ? 1 : 0) + (force.ContainsKey("inputB") ? 1 : 0);
                        if (force.Count != 10 + references || !(Required(force, "id") is string id) || !(Required(force, "kind") is string kind))
                            throw new InvalidOperationException("Invalid cell-force node properties.");
                        var count = 0;
                        var seed = OptionalFloat(force, "seed", -1, ref count);
                        var octaves = OptionalFloat(force, "octaves", -1, ref count);
                        if (seed != Math.Truncate(seed) || seed < 0 || seed > 16777215 || octaves != Math.Truncate(octaves))
                            throw new InvalidOperationException("Invalid cell-force integer.");
                        forces.Add(new LibraFluid2DForceNode
                        {
                            id = id, kind = kind, seed = (int)seed, octaves = (int)octaves,
                            inputA = ForceInput(force, "inputA", forces), inputB = ForceInput(force, "inputB", forces),
                            x = OptionalFloat(force, "x", float.NaN, ref count), y = OptionalFloat(force, "y", float.NaN, ref count),
                            strength = OptionalFloat(force, "strength", float.NaN, ref count), frequency = OptionalFloat(force, "frequency", float.NaN, ref count),
                            roughness = OptionalFloat(force, "roughness", float.NaN, ref count), animationSpeed = OptionalFloat(force, "animationSpeed", float.NaN, ref count)
                        });
                        if (count != 8) throw new InvalidOperationException("Cell-force node has missing values.");
                    }
                    parameters.forces = forces.ToArray();
                }
                result.models.Add(fluidId, parameters);
                if (settings.Count != IntegerFields.Length + FloatFields.Length + 1 + (hasVorticity ? 1 : 0) + extraFields)
                    throw new InvalidOperationException("Fluid settings have missing or unknown properties.");
                if (hasVorticity && (!(vorticity is double strength) || double.IsNaN(strength) ||
                    double.IsInfinity(strength) || strength < 0 || strength > 100))
                    throw new InvalidOperationException("Invalid fluid vorticity.");
                foreach (var name in IntegerFields)
                {
                    if (!(Required(settings, name) is double value) || value != Math.Truncate(value) ||
                        value < int.MinValue || value > int.MaxValue)
                        throw new InvalidOperationException("Invalid fluid integer: " + name);
                }
                foreach (var name in FloatFields)
                    if (!(Required(settings, name) is double value) ||
                        double.IsInfinity(value) || double.IsNaN(value) ||
                        value < -float.MaxValue || value > float.MaxValue)
                        throw new InvalidOperationException("Invalid fluid number: " + name);
                if (!(Required(settings, "sourceEnabled") is bool))
                    throw new InvalidOperationException("Invalid fluid sourceEnabled.");
                var renderer = Object(Required(fluid, "renderer"), "fluid renderer");
                if (renderer.Count != 2 || !(Required(renderer, "material") is string material) ||
                    material != LibraFluid2DSettings.MaterialFor(parameters.model == "FreeSurface" ? 1 : parameters.model == "ShallowWater" ? 2 : parameters.model == "Combustion" ? 3 : 0) ||
                    !(Required(renderer, "sizeScale") is double scale) ||
                    double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale > 10000)
                    throw new InvalidOperationException("Unsupported fluid renderer.");
            }
            foreach (var references in result.generated.Values)
                foreach (var reference in references)
                    if (!fluidIds.Contains(reference.producerNodeId))
                        throw new InvalidOperationException("Generated texture producer is not a fluid in this execution scope.");
            result.drawBindings = ReadDrawBindings(root, fluidIds);
            return result;
        }

        private static string ForceInput(Dictionary<string, object> force, string name, List<LibraFluid2DForceNode> earlier)
        {
            if (!force.TryGetValue(name, out var raw)) return null;
            if (!(raw is string text) || !Guid.TryParse(text, out var id) || id == Guid.Empty)
                throw new InvalidOperationException("Invalid cell-force input identity.");
            var canonical = id.ToString("D");
            if (!earlier.Exists(node => Guid.Parse(node.id) == id))
                throw new InvalidOperationException("Cell-force inputs must reference earlier nodes; cycles are not supported.");
            return canonical;
        }

        private static float OptionalFloat(Dictionary<string, object> values, string name, float fallback, ref int count)
        {
            if (!values.TryGetValue(name, out var raw)) return fallback;
            count++;
            if (!(raw is double value) || double.IsNaN(value) || double.IsInfinity(value) || value < -float.MaxValue || value > float.MaxValue)
                throw new InvalidOperationException("Invalid fluid model number: " + name);
            return (float)value;
        }

        private static string CanonicalNodeId(Dictionary<string, object> value)
        {
            if (!(Required(value, "nodeId") is string text) || !LiberaFxUnityBindingLookup.TryCanonicalGuid(text, out var id))
                throw new InvalidOperationException("Invalid node identity.");
            return id;
        }

        private static void ValidateId(Dictionary<string, object> obj, HashSet<string> ids)
        {
            if (!(Required(obj, "nodeId") is string value) ||
                !Guid.TryParse(value, out var guid) || guid == Guid.Empty ||
                !LiberaFxUnityBindingLookup.TryCanonicalGuid(value, out var id) ||
                !ids.Add(id))
                throw new InvalidOperationException("Invalid, duplicate or mixed GPU node ID.");
        }

        private static object Required(Dictionary<string, object> obj, string name) =>
            obj.TryGetValue(name, out var value) ? value :
                throw new InvalidOperationException("Missing GPU manifest property: " + name);
        private static Dictionary<string, object> Object(object value, string name) =>
            value as Dictionary<string, object> ?? throw new InvalidOperationException("Expected object: " + name);
        private static List<object> Array(Dictionary<string, object> obj, string name) =>
            Required(obj, name) as List<object> ?? throw new InvalidOperationException("Expected array: " + name);

        private static List<object> OptionalArray(Dictionary<string, object> obj, string name)
        {
            if (!obj.TryGetValue(name, out var value)) return new List<object>();
            return value as List<object> ?? throw new InvalidOperationException("Expected array: " + name);
        }

        private sealed class Reader
        {
            private readonly string text;
            private int index;
            internal Reader(string text) { this.text = text; }
            internal object Read()
            {
                var value = Value(0);
                Space();
                if (index != text.Length) Fail();
                return value;
            }
            private object Value(int depth)
            {
                if (depth > 64) Fail();
                Space();
                if (index >= text.Length) Fail();
                var c = text[index];
                if (c == '{')
                {
                    index++;
                    var result = new Dictionary<string, object>(StringComparer.Ordinal);
                    Space();
                    if (Take('}')) return result;
                    do
                    {
                        Space();
                        if (index >= text.Length || text[index] != '"') Fail();
                        var key = String();
                        Space(); Expect(':');
                        var value = Value(depth + 1);
                        if (!result.TryAdd(key, value)) throw new InvalidOperationException("Duplicate JSON property: " + key);
                        Space();
                        if (Take('}')) return result;
                        Expect(',');
                    } while (true);
                }
                if (c == '[')
                {
                    index++;
                    var result = new List<object>();
                    Space();
                    if (Take(']')) return result;
                    do
                    {
                        result.Add(Value(depth + 1)); Space();
                        if (Take(']')) return result;
                        Expect(',');
                    } while (true);
                }
                if (c == '"') return String();
                if (c == 't') { Literal("true"); return true; }
                if (c == 'f') { Literal("false"); return false; }
                if (c == 'n') { Literal("null"); return null; }
                var start = index;
                Take('-');
                if (Take('0')) { }
                else { Digit19(); while (Digit()) index++; }
                if (Take('.')) { if (!Digit()) Fail(); while (Digit()) index++; }
                if (Take('e') || Take('E'))
                {
                    if (!Take('+')) Take('-');
                    if (!Digit()) Fail();
                    while (Digit()) index++;
                }
                if (!double.TryParse(text.Substring(start, index - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var number) || double.IsInfinity(number)) Fail();
                return number;
            }
            private string String()
            {
                Expect('"');
                var result = new StringBuilder();
                while (index < text.Length)
                {
                    var c = text[index++];
                    if (c == '"') return result.ToString();
                    if (c < 32) Fail();
                    if (c != '\\') { result.Append(c); continue; }
                    if (index >= text.Length) Fail();
                    c = text[index++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': result.Append(c); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            if (index + 4 > text.Length) Fail();
                            if (!ushort.TryParse(text.Substring(index, 4),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) Fail();
                            result.Append((char)code); index += 4; break;
                        default: Fail(); break;
                    }
                }
                Fail(); return null;
            }
            private void Space() { while (index < text.Length && (text[index] == ' ' || text[index] == '\n' || text[index] == '\r' || text[index] == '\t')) index++; }
            private bool Take(char c) { if (index < text.Length && text[index] == c) { index++; return true; } return false; }
            private void Expect(char c) { if (!Take(c)) Fail(); }
            private bool Digit() => index < text.Length && text[index] >= '0' && text[index] <= '9';
            private void Digit19() { if (index >= text.Length || text[index] < '1' || text[index] > '9') Fail(); index++; }
            private void Literal(string value) { foreach (var c in value) Expect(c); }
            private static void Fail() => throw new InvalidOperationException("Malformed GPU manifest JSON.");
        }
    }
}
