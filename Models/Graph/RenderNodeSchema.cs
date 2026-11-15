using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiberaFx.Services;

namespace LiberaFx.Models.Graph;

public sealed record RenderMaterialParameter(
    string Id,
    string Name,
    string Kind,
    int Slot,
    string MaterialPath,
    string MaterialName,
    int Association,
    int Index,
    JsonElement Value);

public static class RenderNodeSchema
{
    public const int MaximumParameters = 256;
    public const int MaximumSchemaCharacters = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsRenderNode(GraphNodeDefinition definition) =>
        definition.TypeName is "ModelDraw" or "SpriteDraw" or "SpriteAnimation";

    public static string Encode(EngineAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ValidateAssetPath(asset.Path);
        var parameters = new List<RenderMaterialParameter>();
        foreach (var material in asset.Materials ?? [])
        {
            if (material is null || material.Parameters is null)
                throw new InvalidDataException("A material descriptor is invalid.");
            if (material.Slot < 0 || material.Slot > 4096)
                throw new InvalidDataException("A material slot is invalid.");
            ValidateMaterialPath(material.Path);
            foreach (var parameter in material.Parameters)
            {
                if (parameters.Count >= MaximumParameters)
                    throw new InvalidDataException($"A render node supports at most {MaximumParameters} material parameters.");
                if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name) ||
                    parameter.Name.Length > 128 || parameter.Name.Any(char.IsControl) ||
                    parameter.Association < 0 || parameter.Association > 4096 ||
                    parameter.Index < -1 || parameter.Index > 65535)
                    throw new InvalidDataException("A material parameter descriptor is invalid.");

                var kind = NormalizeKind(parameter.Kind);
                var value = NormalizeValue(kind, parameter.Value);
                var id = CreateId(material.Slot, parameter.Name, kind, parameter.Association, parameter.Index);
                parameters.Add(new(id, parameter.Name, kind, material.Slot,
                    material.Path ?? string.Empty, material.Name ?? string.Empty,
                    parameter.Association, parameter.Index, value));
            }
        }

        var encoded = JsonSerializer.Serialize(parameters, JsonOptions);
        if (encoded.Length > MaximumSchemaCharacters)
            throw new InvalidDataException("The material schema is too large.");
        _ = Decode(encoded);
        return encoded;
    }

    public static GraphNodeDefinition Effective(
        GraphNodeDefinition baseDefinition,
        IReadOnlyList<NodeParameterSnapshot>? nodeParameters)
    {
        ArgumentNullException.ThrowIfNull(baseDefinition);
        if (!IsRenderNode(baseDefinition)) return baseDefinition;

        var schemaParameter = baseDefinition.Parameters.FirstOrDefault(parameter => parameter.Id == "materialSchema");
        if (schemaParameter is null) return baseDefinition;
        var storedSnapshot = nodeParameters?.FirstOrDefault(parameter => parameter.Id == schemaParameter.Id);
        if (storedSnapshot is not null && storedSnapshot.Type != schemaParameter.Type)
            throw new InvalidDataException("The render node material schema parameter has an invalid type.");
        var stored = storedSnapshot?.Value ?? schemaParameter.DefaultValue;
        if (stored.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The render node material schema must be a JSON string.");
        var schema = Decode(stored.GetString() ?? string.Empty);
        if (schema.Count == 0) return baseDefinition;

        var parameters = baseDefinition.Parameters.ToList();
        var ports = (baseDefinition.InputPorts ?? []).ToList();
        var existingIds = parameters.Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        var existingPortIds = ports.Select(port => port.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in schema)
        {
            if (!existingIds.Add(item.Id))
                throw new InvalidDataException("A material parameter conflicts with a render node parameter.");
            var valueType = item.Kind switch
            {
                "scalar" => "float",
                "vector" => "vector4",
                "texture" => "string",
                _ => throw new InvalidDataException("The material schema has an unsupported parameter type.")
            };
            parameters.Add(new NodeParameterDefinition(item.Id, item.Name, valueType, item.Value.Clone()));
            if (item.Kind is "scalar" or "vector")
            {
                if (!existingPortIds.Add(item.Id))
                    throw new InvalidDataException("A material parameter conflicts with a render node input.");
                ports.Add(new GraphInputPortDefinition(item.Id, item.Name,
                    item.Kind == "scalar" ? "Float" : "FloatX4",
                    GraphPortCategory.Parameter, item.Value.Clone()));
            }
        }

        return baseDefinition with
        {
            Parameters = parameters.AsReadOnly(),
            InputPorts = ports.AsReadOnly()
        };
    }

    public static IReadOnlyList<RenderMaterialParameter> Describe(string? schemaJson) =>
        Decode(schemaJson ?? "[]");

    public static IReadOnlyList<RenderMaterialParameter> Read(
        GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? nodeParameters)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var schemaParameter = definition.Parameters.FirstOrDefault(parameter => parameter.Id == "materialSchema");
        var stored = nodeParameters?.FirstOrDefault(parameter => parameter.Id == "materialSchema");
        if (stored is not null && (schemaParameter is null || stored.Type != schemaParameter.Type))
            throw new InvalidDataException("The render node material schema parameter has an invalid type.");
        var json = stored?.Value ?? schemaParameter?.DefaultValue;
        if (json is null) return [];
        if (json.Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The render node material schema must be a JSON string.");
        return Decode(json.Value.GetString() ?? string.Empty);
    }

    public static RenderMaterialParameter? DescribePin(string? schemaJson, string pinId) =>
        Decode(schemaJson ?? "[]").FirstOrDefault(item =>
            item.Id == pinId && item.Kind is "scalar" or "vector");

    public static bool TryDescribePin(string? schemaJson, string? pinId, out RenderMaterialParameter? parameter)
    {
        parameter = Decode(schemaJson ?? "[]").FirstOrDefault(item =>
            item.Id == pinId && item.Kind is "scalar" or "vector");
        return parameter is not null;
    }

    public static void ValidateSchemaParameter(
        GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        if (!IsRenderNode(definition)) return;
        _ = Effective(definition, parameters);
    }

    public static void ValidateNodeParameters(
        GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        if (!IsRenderNode(definition)) return;
        var pathDefinition = definition.Parameters.FirstOrDefault(parameter => parameter.Id == "assetPath");
        var storedPath = parameters?.FirstOrDefault(parameter => parameter.Id == "assetPath");
        if (storedPath is not null && (pathDefinition is null || storedPath.Type != pathDefinition.Type))
            throw new InvalidDataException("The render node asset path parameter has an invalid type.");
        var assetPath = storedPath?.Value ?? pathDefinition?.DefaultValue;
        if (assetPath?.ValueKind == JsonValueKind.String &&
            assetPath.Value.GetString() is { Length: > 0 } path &&
            !IsSafeAssetPath(path))
            throw new InvalidDataException("The render node asset path is invalid.");
        var schema = Read(definition, parameters);
        foreach (var descriptor in schema)
        {
            var value = parameters?.FirstOrDefault(parameter => parameter.Id == descriptor.Id);
            if (value is null) continue;
            var expectedType = descriptor.Kind switch
            {
                "scalar" => "float",
                "vector" => "vector4",
                "texture" => "string",
                _ => throw new InvalidDataException("The material schema has an unsupported parameter type.")
            };
            if (value.Type != expectedType || !NodeParameter.IsValidValue(expectedType, value.Value))
                throw new InvalidDataException("A material parameter value does not match its schema.");
            if (descriptor.Kind == "texture") ValidateOptionalAssetPath(value.Value.GetString());
        }
    }

    public static bool IsValidParameterValue(GraphNodeDefinition definition, NodeParameterSnapshot parameter)
    {
        if (IsRenderNode(definition) && parameter.Id == "materialSchema")
        {
            if (parameter.Type != "string" || parameter.Value.ValueKind != JsonValueKind.String) return false;
            try
            {
                _ = Decode(parameter.Value.GetString() ?? string.Empty);
                return true;
            }
            catch (InvalidDataException) { return false; }
        }
        return NodeParameter.IsValidValue(parameter.Type, parameter.Value);
    }

    public static bool IsSafeAssetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path[0] != '/' ||
            path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\') ||
            !(path.StartsWith("/Game/", StringComparison.Ordinal) ||
              path.StartsWith("/Engine/", StringComparison.Ordinal)) ||
            path.Any(character => char.IsControl(character) || !(char.IsAsciiLetterOrDigit(character) ||
                character is '/' or '_' or '-' or '.' or ' ')))
            return false;
        return path.Split('/').All(segment => segment is not ("." or ".."));
    }

    private static void ValidateAssetPath(string? path)
    {
        if (!IsSafeAssetPath(path))
            throw new InvalidDataException("The engine asset path is invalid.");
    }

    private static void ValidateOptionalAssetPath(string? path)
    {
        if (!string.IsNullOrEmpty(path) && !IsSafeAssetPath(path))
            throw new InvalidDataException("A material asset path is invalid.");
    }

    private static void ValidateMaterialPath(string? path)
    {
        if (path == "builtin:unlit-color") return;
        ValidateOptionalAssetPath(path);
    }

    private static string NormalizeKind(string kind) =>
        (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "scalar" or "float" or "scalarparameter" => "scalar",
            "vector" or "vector4" or "vectorparameter" => "vector",
            "texture" or "texture2d" or "textureparameter" => "texture",
            _ => throw new InvalidDataException($"Unsupported material parameter kind '{kind}'.")
        };

    private static JsonElement NormalizeValue(string kind, JsonElement value)
    {
        var type = kind switch { "scalar" => "float", "vector" => "vector4", _ => "string" };
        if (kind == "texture" && value.ValueKind == JsonValueKind.Null)
            value = JsonSerializer.SerializeToElement(string.Empty);
        if (!NodeParameter.IsValidValue(type, value))
            throw new InvalidDataException($"The default value for a {kind} material parameter is invalid.");
        if (kind == "texture") ValidateOptionalAssetPath(value.GetString());
        return value.Clone();
    }

    private static string CreateId(int slot, string name, string kind, int association, int index)
    {
        var input = Encoding.UTF8.GetBytes($"{slot}\0{name}\0{kind}\0{association}\0{index}");
        var hash = Convert.ToHexString(SHA256.HashData(input).AsSpan(0, 12)).ToLowerInvariant();
        return $"material_{hash}";
    }

    private static IReadOnlyList<RenderMaterialParameter> Decode(string json)
    {
        if (json.Length > MaximumSchemaCharacters)
            throw new InvalidDataException("The material schema is too large.");
        try
        {
            var parameters = JsonSerializer.Deserialize<List<RenderMaterialParameter>>(json, JsonOptions)
                ?? throw new InvalidDataException("The material schema is empty.");
            if (parameters.Count > MaximumParameters)
                throw new InvalidDataException("The material schema contains too many parameters.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                if (parameter is null || string.IsNullOrWhiteSpace(parameter.Id) ||
                    parameter.Id.Length > 64 || string.IsNullOrWhiteSpace(parameter.Name) ||
                    parameter.Name.Length > 128 || parameter.Name.Any(char.IsControl) ||
                    string.IsNullOrWhiteSpace(parameter.Kind) ||
                    parameter.MaterialName is null || parameter.MaterialName.Length > 256 ||
                    parameter.Slot < 0 || parameter.Slot > 4096 ||
                    parameter.Association < 0 || parameter.Association > 4096 ||
                    parameter.Index < -1 || parameter.Index > 65535 ||
                    parameter.Kind is not ("scalar" or "vector" or "texture"))
                    throw new InvalidDataException("The material schema contains an invalid parameter.");
                if (!string.Equals(parameter.Id, CreateId(parameter.Slot, parameter.Name,
                        parameter.Kind, parameter.Association, parameter.Index), StringComparison.Ordinal) ||
                    !ids.Add(parameter.Id))
                    throw new InvalidDataException("The material schema contains duplicate or mismatched IDs.");
                ValidateMaterialPath(parameter.MaterialPath);
                var kind = parameter.Kind;
                var expectedType = kind == "scalar" ? "float" : kind == "vector" ? "vector4" : "string";
                if (!NodeParameter.IsValidValue(expectedType, parameter.Value))
                    throw new InvalidDataException("The material schema contains an invalid default value.");
                if (kind == "texture") ValidateOptionalAssetPath(parameter.Value.GetString());
            }
            return parameters.AsReadOnly();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The material schema is not valid JSON.", exception);
        }
    }
}
