using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiberaFx.Models.Graph;

public enum SkeletalPoseTarget : byte
{
    Owner = 0,
    RegisteredKey = 1
}

public enum SkeletalPoseSpace : byte
{
    BoneLocal = 0,
    Component = 1,
    World = 2
}

public sealed record SkeletalPoseBinding(Guid SourceNodeId, SkeletalPoseTarget Target,
    string Key, string BonePath, SkeletalPoseSpace Space);

/// <summary>
/// Lowers the author-facing SOURCE node to ordinary named Matrix and Float
/// inputs. Their names carry a bounded, versioned binding descriptor so hosts
/// can acquire the same source explicitly without changing runtime bytecode.
/// </summary>
public static class SkeletalPoseSchema
{
    private const string SourceKind = "Source";
    private const string MatrixPrefix = "__LFXS1M_";
    private const string ValidityPrefix = "__LFXS1V_";
    private const int MaxEncodedPortNameBytes = 128;
    private const int MaxCombinedTargetAndBoneBytes = 63;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsSource(GraphNodeKind kind) => kind.Value == SourceKind;

    public static bool ContainsSource(GraphSnapshot graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Nodes.Any(node => IsSource(node.Kind) || HasSourceDescriptor(node) ||
            node.ChildGraph is { } child && ContainsSource(child));
    }

    public static bool ContainsAuthoredSource(GraphSnapshot graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Nodes.Any(node => IsSource(node.Kind) ||
            node.ChildGraph is { } child && ContainsAuthoredSource(child));
    }

    public static GraphSnapshot Lower(GraphSnapshot graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ValidateSourceLocations(graph, true);
        var sourceNodes = graph.Nodes.Where(node => IsSource(node.Kind)).ToArray();
        if (sourceNodes.Length == 0) return graph;

        var definitions = GraphNodeDefinition.All.ToDictionary(item => item.Kind);
        if (!definitions.TryGetValue(GraphNodeKind.InputProperty, out var inputDefinition))
            throw new InvalidDataException("SOURCE requires the built-in Input Property node.");
        var sourceDefinition = definitions.GetValueOrDefault(new GraphNodeKind(SourceKind))
            ?? throw new InvalidDataException("The SOURCE node definition is unavailable.");
        var existingIds = new HashSet<Guid>();
        CollectIds(graph, existingIds);
        var existingNames = graph.Nodes.Where(node => node.Kind == GraphNodeKind.InputProperty)
            .Select(node => GetParameterString(inputDefinition, node, "name"))
            .ToHashSet(StringComparer.Ordinal);
        var replacements = new Dictionary<Guid, (GraphNodeSnapshot Matrix, GraphNodeSnapshot Validity)>();

        foreach (var source in sourceNodes)
        {
            var binding = ReadBinding(sourceDefinition, source);
            var descriptor = Encode(binding);
            var matrixName = MatrixPrefix + descriptor;
            var validityName = ValidityPrefix + descriptor;
            ValidatePortName(matrixName);
            ValidatePortName(validityName);
            if (!existingNames.Add(matrixName) || !existingNames.Add(validityName))
                throw new InvalidDataException("SOURCE generated duplicate hidden Game I/O input names.");

            var matrixId = DerivedId(source.Id, "matrix");
            var validityId = DerivedId(source.Id, "validity");
            if (matrixId == validityId || !existingIds.Add(matrixId) || !existingIds.Add(validityId))
                throw new InvalidDataException($"SOURCE expansion IDs conflict at node {source.Id}.");

            replacements.Add(source.Id, (
                CreateInput(inputDefinition, source, matrixId, matrixName, 5, "Matrix"),
                CreateInput(inputDefinition, source, validityId, validityName, 0, "Valid")));
        }

        var nodes = graph.Nodes.Where(node => !IsSource(node.Kind)).ToList();
        foreach (var replacement in replacements.Values)
        {
            nodes.Add(replacement.Matrix);
            nodes.Add(replacement.Validity);
        }
        var edges = graph.Connections.Select(edge =>
        {
            if (!replacements.TryGetValue(edge.SourceNodeId, out var replacement)) return edge;
            return edge.SourcePortId switch
            {
                "value" => edge with { SourceNodeId = replacement.Matrix.Id, SourcePortId = "value" },
                "valid" => edge with { SourceNodeId = replacement.Validity.Id, SourcePortId = "value" },
                _ => throw new InvalidDataException("SOURCE connections must use the Matrix or Valid output.")
            };
        }).ToArray();

        return new GraphSnapshot(nodes, edges)
        {
            AutoDestroyWhenIdle = graph.AutoDestroyWhenIdle,
            ExposedItems = graph.ExposedItems
        };
    }

    public static bool TryDecodePortName(string? name, out SkeletalPoseBinding? binding,
        out bool isValidity)
    {
        binding = null;
        isValidity = false;
        if (name is null) return false;
        var prefix = name.StartsWith(MatrixPrefix, StringComparison.Ordinal) ? MatrixPrefix :
            name.StartsWith(ValidityPrefix, StringComparison.Ordinal) ? ValidityPrefix : null;
        if (prefix is null) return false;
        isValidity = prefix == ValidityPrefix;
        try
        {
            var token = name[prefix.Length..];
            var bytes = DecodeBase64Url(token);
            if (bytes.Length < 20 || bytes[0] != 1) return false;
            var sourceId = new Guid(bytes.AsSpan(1, 16), bigEndian: true);
            var target = (SkeletalPoseTarget)bytes[17];
            var space = (SkeletalPoseSpace)bytes[18];
            if (target is not (SkeletalPoseTarget.Owner or SkeletalPoseTarget.RegisteredKey) ||
                space is not (SkeletalPoseSpace.BoneLocal or SkeletalPoseSpace.Component or SkeletalPoseSpace.World))
                return false;
            var keyLength = bytes[19];
            if (bytes.Length < 21 + keyLength) return false;
            var boneLength = bytes[20 + keyLength];
            if (bytes.Length != 21 + keyLength + boneLength) return false;
            var key = StrictUtf8.GetString(bytes, 20, keyLength);
            var bonePath = StrictUtf8.GetString(bytes, 21 + keyLength, boneLength);
            var decoded = new SkeletalPoseBinding(sourceId, target, key, bonePath, space);
            ValidateBinding(decoded);
            binding = decoded;
            return Encode(decoded) == token;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException)
        {
            binding = null;
            return false;
        }
    }

    private static SkeletalPoseBinding ReadBinding(GraphNodeDefinition definition, GraphNodeSnapshot node)
    {
        var targetValue = GetParameter(definition, node, "target");
        var spaceValue = GetParameter(definition, node, "space");
        if (!targetValue.TryGetInt32(out var targetNumber) || targetNumber is < 0 or > 1)
            throw new InvalidDataException("SOURCE target must be Owner or Registered Key.");
        if (!spaceValue.TryGetInt32(out var spaceNumber) || spaceNumber is < 0 or > 2)
            throw new InvalidDataException("SOURCE space must be Bone Local, Component, or World.");
        var binding = new SkeletalPoseBinding(node.Id, (SkeletalPoseTarget)targetNumber,
            GetParameterString(definition, node, "key"), GetParameterString(definition, node, "bone"),
            (SkeletalPoseSpace)spaceNumber);
        ValidateBinding(binding);
        return binding;
    }

    private static void ValidateBinding(SkeletalPoseBinding binding)
    {
        int keyBytes;
        int boneBytes;
        try
        {
            keyBytes = StrictUtf8.GetByteCount(binding.Key);
            boneBytes = StrictUtf8.GetByteCount(binding.BonePath);
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("SOURCE target and bone names must be valid UTF-8.", error);
        }
        if (binding.SourceNodeId == Guid.Empty || binding.Target is not
            (SkeletalPoseTarget.Owner or SkeletalPoseTarget.RegisteredKey) ||
            binding.Space is not (SkeletalPoseSpace.BoneLocal or SkeletalPoseSpace.Component or SkeletalPoseSpace.World))
            throw new InvalidDataException("SOURCE has an invalid source ID, target, or space.");
        if (binding.Target == SkeletalPoseTarget.Owner && binding.Key.Length != 0)
            throw new InvalidDataException("SOURCE Owner target must not specify a registered key.");
        if (binding.Target == SkeletalPoseTarget.RegisteredKey &&
            (string.IsNullOrWhiteSpace(binding.Key) || binding.Key.Trim() != binding.Key ||
             binding.Key.Any(char.IsControl)))
            throw new InvalidDataException("SOURCE Registered Key requires a nonempty key without edge whitespace or control characters.");
        if (string.IsNullOrWhiteSpace(binding.BonePath) || binding.BonePath[0] == '/' ||
            binding.BonePath[^1] == '/' || binding.BonePath.Split('/').Any(part =>
                part.Length == 0 || part is "." or ".." || part.Any(char.IsControl)))
            throw new InvalidDataException("SOURCE requires an exact relative bone path (no empty, '.' or '..' segments).");
        if (keyBytes > byte.MaxValue || boneBytes > byte.MaxValue ||
            keyBytes + boneBytes > MaxCombinedTargetAndBoneBytes)
            throw new InvalidDataException(
                $"SOURCE key and bone path must total at most {MaxCombinedTargetAndBoneBytes} UTF-8 bytes.");
    }

    private static string Encode(SkeletalPoseBinding binding)
    {
        ValidateBinding(binding);
        var key = StrictUtf8.GetBytes(binding.Key);
        var bone = StrictUtf8.GetBytes(binding.BonePath);
        var bytes = new byte[21 + key.Length + bone.Length];
        bytes[0] = 1;
        if (!binding.SourceNodeId.TryWriteBytes(bytes.AsSpan(1, 16), bigEndian: true, out var written) || written != 16)
            throw new InvalidDataException("Could not encode the SOURCE node ID.");
        bytes[17] = (byte)binding.Target;
        bytes[18] = (byte)binding.Space;
        bytes[19] = (byte)key.Length;
        key.CopyTo(bytes, 20);
        bytes[20 + key.Length] = (byte)bone.Length;
        bone.CopyTo(bytes, 21 + key.Length);
        return Base64Url(bytes);
    }

    private static byte[] DecodeBase64Url(string token)
    {
        if (token.Length == 0 || token.Any(character =>
            !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new InvalidDataException("Invalid SOURCE descriptor encoding.");
        var base64 = token.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static GraphNodeSnapshot CreateInput(GraphNodeDefinition definition, GraphNodeSnapshot source,
        Guid id, string name, int valueType, string label)
    {
        var parameters = definition.Parameters.Select(parameter =>
        {
            var value = parameter.Id switch
            {
                "name" => JsonSerializer.SerializeToElement(name),
                "valueType" => JsonSerializer.SerializeToElement(valueType),
                _ => parameter.DefaultValue.Clone()
            };
            return new NodeParameterSnapshot(parameter.Id, parameter.Type, value);
        }).ToArray();
        var effective = GameIoSchema.Effective(definition, parameters);
        return new(id, definition.Kind, $"[SOURCE {label}] {source.Name}", source.X, source.Y,
            effective.PortType, effective.PortCategory, Parameters: parameters);
    }

    private static JsonElement GetParameter(GraphNodeDefinition definition, GraphNodeSnapshot node, string id)
    {
        var parameter = definition.Parameters.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidDataException($"SOURCE definition is missing parameter '{id}'.");
        var saved = node.Parameters?.SingleOrDefault(item => item.Id == id);
        if (saved is not null && saved.Type != parameter.Type)
            throw new InvalidDataException($"SOURCE parameter '{id}' has an invalid type.");
        var value = saved?.Value ?? parameter.DefaultValue;
        if (!NodeParameter.IsValidValue(parameter.Type, value))
            throw new InvalidDataException($"SOURCE parameter '{id}' has an invalid value.");
        return value;
    }

    private static string GetParameterString(GraphNodeDefinition definition, GraphNodeSnapshot node, string id)
    {
        var value = GetParameter(definition, node, id);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"SOURCE parameter '{id}' must be a string.");
        return value.GetString()!;
    }

    private static void ValidatePortName(string name)
    {
        try
        {
            if (StrictUtf8.GetByteCount(name) > MaxEncodedPortNameBytes)
                throw new InvalidDataException("SOURCE binding metadata exceeds the runtime input-name limit.");
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("SOURCE binding metadata is not valid UTF-8.", error);
        }
    }

    private static Guid DerivedId(Guid sourceNodeId, string role)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.SOURCE.v1:{sourceNodeId:D}:{role}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void CollectIds(GraphSnapshot graph, HashSet<Guid> ids)
    {
        foreach (var node in graph.Nodes)
        {
            if (!ids.Add(node.Id)) throw new InvalidDataException("Duplicate graph node ID.");
            if (node.ChildGraph is not null) CollectIds(node.ChildGraph, ids);
        }
    }

    private static bool HasSourceDescriptor(GraphNodeSnapshot node)
    {
        if (GameIoSchema.SharedKind(node.Kind) != GraphNodeKind.InputProperty) return false;
        var parameter = node.Parameters?.FirstOrDefault(item => item.Id == "name");
        if (parameter is null || parameter.Value.ValueKind != JsonValueKind.String) return false;
        var name = parameter.Value.GetString()!;
        return name.StartsWith(MatrixPrefix, StringComparison.Ordinal) ||
            name.StartsWith(ValidityPrefix, StringComparison.Ordinal);
    }

    private static void ValidateSourceLocations(GraphSnapshot graph, bool isRoot)
    {
        foreach (var node in graph.Nodes)
        {
            if (!isRoot && IsSource(node.Kind))
                throw new InvalidDataException("SOURCE nodes must be in the root graph.");
            if (node.ChildGraph is not null) ValidateSourceLocations(node.ChildGraph, false);
        }
    }
}
