using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;

namespace LiberaFx.Services.Compilation;

/// <summary>Metadata lowering only: none of these nodes is a numeric VM instruction.</summary>
public static class FlexiblePlaneDrawSchema
{
    public const string DrawId = "864fea49-8719-4c36-a7d1-1f3000000001";
    public const string ListId = "864fea49-8719-4c36-a7d1-1f3000000002";
    public const string HistoryId = "864fea49-8719-4c36-a7d1-1f3000000003";

    private static bool Is(GraphNodeKind kind, string id, string name) =>
        kind.Value == name || string.Equals(kind.Value, id, StringComparison.OrdinalIgnoreCase);

    public static bool IsNode(GraphNodeKind kind) => Is(kind, DrawId, "FlexiblePlaneDraw");
    public static bool IsHelper(GraphNodeKind kind) =>
        Is(kind, ListId, "FlexiblePlanePointList") || Is(kind, HistoryId, "FlexiblePlanePointHistory");
    public static bool IsMetadata(GraphNodeKind kind) => IsNode(kind) || IsHelper(kind);

    public sealed record Point(double[] Position, double Width, double[] Color);

    private static JsonElement Parameter(GraphNodeSnapshot node, string id, GraphNodeDefinition? definition = null)
    {
        definition ??= GraphNodeDefinition.All.Single(item => item.Kind == node.Kind);
        var declared = definition.Parameters.Single(item => item.Id == id);
        var saved = node.Parameters?.SingleOrDefault(item => item.Id == id);
        if (saved is not null && saved.Type != declared.Type)
        {
            throw new InvalidDataException($"Invalid Flexible Plane parameter '{id}' type.");
        }
        var value = saved?.Value ?? declared.DefaultValue;
        if (!NodeParameter.IsValidValue(declared.Type, value))
        {
            throw new InvalidDataException($"Invalid Flexible Plane parameter '{id}'.");
        }
        return value;
    }

    private static double Number(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || Math.Abs(number) > float.MaxValue)
        {
            throw new InvalidDataException($"Flexible Plane {label} must be a finite float.");
        }
        return number;
    }

    private static double[] Vector(JsonElement value, int width, string label)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != width)
        {
            throw new InvalidDataException($"Flexible Plane {label} must have {width} components.");
        }
        return value.EnumerateArray().Select(item => Number(item, label)).ToArray();
    }

    public static Point[] ParsePoints(string json)
    {
        if (json.Length > 4096)
        {
            throw new InvalidDataException("Flexible Plane points JSON exceeds 4096 characters.");
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 2 or > 64)
            {
                throw new InvalidDataException("Flexible Plane point list must contain 2 to 64 points.");
            }
            return root.EnumerateArray().Select(item =>
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("position", out var position) ||
                    !item.TryGetProperty("width", out var width) ||
                    !item.TryGetProperty("color", out var color) ||
                    item.EnumerateObject().Count() != 3)
                {
                    throw new InvalidDataException("Each point requires only position, width and color.");
                }
                var w = Number(width, "point width");
                if (w < 0)
                {
                    throw new InvalidDataException("Point width must be nonnegative.");
                }
                return new Point(Vector(position, 3, "position"), w, Vector(color, 4, "color"));
            }).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid Flexible Plane points JSON.", exception);
        }
    }

    private static Point[] ResolveListPoints(GraphNodeSnapshot source)
    {
        var parsed = ParsePoints(Parameter(source, "pointData").GetString()!);
        var storedCount = source.Parameters?.FirstOrDefault(parameter => parameter.Id == "pointCount");
        if (storedCount is null) return parsed;
        if (storedCount.Type != "int" || storedCount.Value.ValueKind != JsonValueKind.Number ||
            !storedCount.Value.TryGetInt32(out var count) || count is < 2 or > 64)
            throw new InvalidDataException("Flexible Plane point count must be between 2 and 64.");
        if (count == parsed.Length) return parsed;
        if (count < parsed.Length) return parsed[..count];
        var extended = new Point[count];
        Array.Copy(parsed, extended, parsed.Length);
        for (var index = parsed.Length; index < count; index++)
            extended[index] = new([index, 0, 0], 1, [1, 1, 1, 1]);
        return extended;
    }

    /// <summary>
    /// Call after graph/group lowering, with nodes and target-indexed connections from
    /// the same instance scope. The caller adds the returned object to render_config.nodes.
    /// </summary>
    public static object CreateDraw(GraphNodeSnapshot node,
        IReadOnlyDictionary<Guid, GraphNodeSnapshot> nodes,
        ILookup<Guid, GraphConnectionSnapshot> connections,
        IRenderResourceBindingContext? resources = null, IReadOnlyList<Guid>? instancePath = null, bool generatedTexture = false,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? instanceScopes = null)
    {
        if (!IsNode(node.Kind))
        {
            throw new InvalidDataException("Expected Flexible Plane Draw.");
        }
        var definition = RenderNodeSchema.Effective(GraphNodeDefinition.All.Single(item => item.Kind == node.Kind), node.Parameters);
        var scopes = instanceScopes ?? new Dictionary<Guid, IReadOnlyList<Guid>>();
        var proceduralTexture = ProceduralProgramBindings.Source(node, "proceduralTexture", "ProceduralTexture", nodes, connections, scopes);
        var proceduralMaterial = ProceduralProgramBindings.Source(node, "proceduralMaterial", "ProceduralMaterial", nodes, connections, scopes);
        var sources = connections[node.Id].Where(edge => edge.TargetPortId == "points").ToArray();
        if (sources.Length != 1 || sources[0].SourcePortId is not null ||
            !nodes.TryGetValue(sources[0].SourceNodeId, out var source) || !IsHelper(source.Kind))
        {
            throw new InvalidDataException("Flexible Plane Draw requires one connected point source.");
        }
        var isList = Is(source.Kind, ListId, "FlexiblePlanePointList");
        var points = isList ? ResolveListPoints(source) : Array.Empty<Point>();
        var inputs = new List<object>();
        void Input(GraphNodeSnapshot target, string port, string id, double[] defaults, object? materialParameter = null)
        {
            var matches = connections[target.Id].Where(edge => edge.TargetPortId == port).ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidDataException($"Multiple sources on '{port}'.");
            }
            var edge = matches.SingleOrDefault();
            var component = 0;
            if (edge is not null)
            {
                if (!nodes.TryGetValue(edge.SourceNodeId, out var valueNode) || IsMetadata(valueNode.Kind))
                {
                    throw new InvalidDataException($"Flexible Plane '{port}' requires a numeric source.");
                }
                var valueDefinition = GraphNodeDefinition.All.Single(item => item.Kind == valueNode.Kind);
                var outputType = valueDefinition.GetOutputPortType(edge.SourcePortId);
                var expected = defaults.Length switch { 1 => "Float", 3 => "FloatX3", _ => "FloatX4" };
                if (outputType != expected)
                {
                    throw new InvalidDataException($"Flexible Plane '{port}' expects {expected}.");
                }
                if (valueDefinition.TypeName.StartsWith("BreakVector", StringComparison.Ordinal))
                {
                    component = valueDefinition.OutputPorts!.ToList().FindIndex(output => output.Id == edge.SourcePortId);
                    if (component < 0)
                    {
                        throw new InvalidDataException("Invalid source component.");
                    }
                }
            }
            inputs.Add(new { id, width = defaults.Length, sourceNodeId = edge?.SourceNodeId,
                sourceComponent = component, defaults, materialParameter });
        }

        var width = Number(Parameter(node, "width", definition), "width multiplier");
        if (width < 0)
        {
            throw new InvalidDataException("Width multiplier must be nonnegative.");
        }
        Input(node, "width", "width", [width]);
        Input(node, "tint", "tint", Vector(Parameter(node, "tint", definition), 4, "tint"));
        var capacity = 0;
        var maxAge = 0d;
        if (isList)
        {
            for (var index = 0; index < points.Length; index++)
                Input(source, $"position{index}", $"position{index}", points[index].Position);
            foreach (var edge in connections[source.Id])
            {
                if (edge.TargetPortId is { } pin && pin.StartsWith("position", StringComparison.Ordinal) &&
                    int.TryParse(pin["position".Length..], out var index) && index >= points.Length)
                    throw new InvalidDataException($"{pin} has no corresponding list point.");
            }
        }
        else
        {
            capacity = Parameter(source, "capacity").GetInt32();
            maxAge = Number(Parameter(source, "maxAge"), "maximum age");
            var pointWidth = Number(Parameter(source, "width"), "point width");
            if (capacity is < 2 or > 4096 || maxAge <= 0 || pointWidth < 0)
            {
                throw new InvalidDataException("History capacity must be 2..4096, age positive and width nonnegative.");
            }
            Input(source, "position", "position", Vector(Parameter(source, "position"), 3, "position"));
            Input(source, "width", "pointWidth", [pointWidth]);
            Input(source, "color", "pointColor", Vector(Parameter(source, "color"), 4, "color"));
        }
        var repeatLength = Number(Parameter(node, "repeatLength", definition), "repeat length");
        if (repeatLength <= 0)
        {
            throw new InvalidDataException("Repeat length must be positive.");
        }
        var materialEntries = RenderNodeSchema.Read(definition, node.Parameters);
        var materialSlots = RenderNodeSchema.ReadMaterials(definition, node.Parameters);
        resources?.ValidateRenderNode(node.Id,
            materialEntries.Select(entry => (entry.Id, entry.Kind, entry.Slot)),
            instancePath ?? [], materialSlots.Select(slot => slot.Slot).ToArray());
        var mappedParameters = materialEntries.ToDictionary(entry => entry.Id, entry =>
            resources?.ResolveParameter(node.Id, entry.Id, entry.Slot, entry.Kind, instancePath ?? []));
        foreach (var entry in materialEntries.Where(item => item.Kind is "scalar" or "vector"))
        {
            var mapped = mappedParameters[entry.Id];
            var materialParameter = new
            {
                name = mapped?.Name ?? entry.Name,
                kind = entry.Kind,
                slot = entry.Slot,
                association = mapped?.Association ?? RenderNodeSchema.MaterialAssociation(entry),
                index = mapped?.Index ?? entry.Index
            };
            var value = Parameter(node, entry.Id, definition);
            var defaults = entry.Kind == "scalar"
                ? [Number(value, entry.Name)]
                : Vector(value, 4, entry.Name);
            Input(node, entry.Id, entry.Id, defaults, materialParameter);
        }
        var rawPath = Parameter(node, "assetPath", definition).GetString() ?? "";
        var path = generatedTexture
            ? "builtin:generated-texture"
            : resources?.ResolvePath(node.Id, "assetPath", "texture", rawPath, instancePath ?? []) ?? rawPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            if (proceduralMaterial is not null) path = "builtin:procedural-material";
            else if (proceduralTexture is not null) path = "builtin:procedural-texture";
            else throw new InvalidDataException("Flexible Plane Draw requires a texture.");
        }
        if (!generatedTexture &&
            path is not ("builtin:procedural-material" or "builtin:procedural-texture") &&
            resources?.HasResourceUse(node.Id, "assetPath", instancePath ?? []) != true)
        {
            EngineAssetPath.Validate(path);
        }
        var textures = materialEntries.Where(entry => entry.Kind == "texture")
            .Select(entry => new
            {
                name = mappedParameters[entry.Id]?.Name ?? entry.Name,
                slot = entry.Slot,
                path = resources?.ResolvePath(node.Id, "texture:" + entry.Id, "texture",
                    Parameter(node, entry.Id, definition).GetString() ?? "", instancePath ?? []) ??
                    Parameter(node, entry.Id, definition).GetString(),
                association = mappedParameters[entry.Id]?.Association ?? RenderNodeSchema.MaterialAssociation(entry),
                index = mappedParameters[entry.Id]?.Index ?? entry.Index
            }).ToArray();
        var materials = materialSlots
            .Where(slot => !string.IsNullOrEmpty(slot.Path) ||
                resources?.HasResourceUse(node.Id, $"material:{slot.Slot}", instancePath ?? []) == true)
            .Select(slot => new
            {
                slot = slot.Slot,
                path = resources?.ResolvePath(node.Id, $"material:{slot.Slot}", "material",
                    slot.Path, instancePath ?? []) ?? slot.Path
            }).ToArray();
        var plane = new
        {
            version = 1, sourceKind = isList ? "list" : "history", sourceNodeId = source.Id,
            points, capacity, maxAge,
            facing = Parameter(node, "cameraFacing", definition).GetBoolean() ? "camera" : "fixedUp",
            up = Vector(Parameter(node, "up", definition), 3, "up"),
            uvMode = Parameter(node, "repeat", definition).GetBoolean() ? "repeat" : "stretch",
            repeatLength
        };
        return new { nodeId = node.Id, kind = "flexiblePlane", assetPath = path,
            proceduralTexture, proceduralMaterial,
            inputs, flexiblePlane = plane, materials, textures,
            billboard = false };
    }
}
