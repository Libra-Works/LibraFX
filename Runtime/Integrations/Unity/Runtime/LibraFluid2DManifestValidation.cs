using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LiberaFx.Unity
{
    // JsonUtility silently accepts missing fields and duplicate keys. Version 2
    // must be checked before deserializing and before any GPU state is created.
    internal static class LibraFluid2DManifestValidation
    {
        private static readonly string[] IntegerFields =
            { "version", "width", "height", "pressureIterations", "maximumSubsteps" };
        private static readonly string[] FloatFields =
        {
            "domainWidth", "domainHeight", "maximumStep", "velocityDissipation", "densityDissipation",
            "sourceX", "sourceY", "sourceRadius", "sourceDensity", "sourceVelocityX", "sourceVelocityY",
            "buoyancy", "colorR", "colorG", "colorB", "colorA"
        };

        internal static void Validate(string json)
        {
            var root = Object(new Reader(json).Read(), "GPU manifest");
            if (!root.TryGetValue("version", out var version) || !(version is double n) || n != 2)
                throw new InvalidOperationException("Invalid GPU manifest version.");
            var programs = Array(root, "programs");
            var fluids = Array(root, "fluidPrograms");
            if (fluids.Count == 0 || programs.Count + fluids.Count > 16)
                throw new InvalidOperationException("GPU manifest program count is invalid.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in programs) ValidateId(Object(item, "particle program"), ids);
            foreach (var item in fluids)
            {
                var fluid = Object(item, "fluid program");
                ValidateId(fluid, ids);
                var settings = Object(Required(fluid, "settings"), "fluid settings");
                if (settings.Count != IntegerFields.Length + FloatFields.Length + 1)
                    throw new InvalidOperationException("Fluid settings have missing or unknown properties.");
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
                    material != "builtin:fluid-alpha" || !(Required(renderer, "sizeScale") is double scale) ||
                    double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale > 10000)
                    throw new InvalidOperationException("Unsupported fluid renderer.");
            }
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
