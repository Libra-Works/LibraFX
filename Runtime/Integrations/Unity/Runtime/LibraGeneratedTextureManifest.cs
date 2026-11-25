using System;
using System.Collections.Generic;

namespace LiberaFx.Unity
{
    internal static partial class LibraFluid2DManifestValidation
    {
        private static LiberaFxGeneratedRenderTexturePayload[] ReadDrawBindings(Dictionary<string, object> root,
            HashSet<string> fluidIds)
        {
            if (!root.ContainsKey("generatedDrawBindings")) return System.Array.Empty<LiberaFxGeneratedRenderTexturePayload>();
            var values = Array(root, "generatedDrawBindings");
            if (values.Count > 64) throw new InvalidOperationException("Too many generated draw texture bindings.");
            var result = new List<LiberaFxGeneratedRenderTexturePayload>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in values)
            {
                var value = Object(item, "generated draw texture binding");
                if (value.Count != 8 || !(Required(value, "producerNodeId") is string producer) ||
                    !LiberaFxUnityBindingLookup.TryCanonicalGuid(producer, out var producerId) || !fluidIds.Contains(producerId) ||
                    !(Required(value, "outputId") is string output) || output != "DyeOutput" && output != "ColorOutput" && output != "FieldOutput" ||
                    !(Required(value, "name") is string name) || name.Length > 256 || !(Required(value, "mainTexture") is bool main))
                    throw new InvalidOperationException("Invalid generated draw texture source or target.");
                var target = CanonicalNodeId(value);
                if (!Guid.TryParse(target, out var targetGuid) || targetGuid == Guid.Empty)
                    throw new InvalidOperationException("Empty generated draw target.");
                var slot = DrawInteger(value, "slot", 0, 255);
                var association = DrawInteger(value, "association", 0, 2);
                var index = DrawInteger(value, "index", -1, 65535);
                if (main && (slot != 0 || name.Length != 0 || association != 2 || index != -1 || output != "ColorOutput") || !main && string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("Invalid generated texture parameter identity.");
                var key = target + "/" + slot + "/" + name + "/" + association + "/" + index + "/" + main;
                if (!unique.Add(key)) throw new InvalidOperationException("Duplicate generated texture target.");
                result.Add(new LiberaFxGeneratedRenderTexturePayload { nodeId = target, producerNodeId = producerId,
                    outputId = output, slot = slot, name = name, association = association, index = index, mainTexture = main });
            }
            return result.ToArray();
        }

        private static int DrawInteger(Dictionary<string, object> value, string name, int minimum, int maximum)
        {
            if (!(Required(value, name) is double number) || number != Math.Truncate(number) || number < minimum || number > maximum)
                throw new InvalidOperationException("Invalid generated draw integer: " + name);
            return (int)number;
        }
    }
}
