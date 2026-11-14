using System.Globalization;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Models.Graph;

namespace LiberaFx.ViewModels;

public sealed class NodeParameterEditorViewModel : ObservableObject
{
    private readonly GraphWorkspaceViewModel _graph;
    private readonly GraphNodeViewModel _node;
    private readonly NodeParameterDefinition _definition;
    private string _status = string.Empty;

    public string Name => _definition.Name;
    public string Type => _definition.Type;
    public string Id => _definition.Id;
    public NodeParameterEditorDefinition? Editor => _definition.Editor;
    public bool IsEnabled
    {
        get
        {
            if (_definition.EnabledWhen is not { } condition) return true;
            var dependency = _node.Definition.Parameters.FirstOrDefault(item => item.Id == condition.Parameter);
            return dependency is not null && JsonElement.DeepEquals(_node.GetParameterValue(dependency), condition.Expected);
        }
    }
    public IReadOnlyList<GraphEnumValue> EnumValues
    {
        get
        {
            if (!GraphEnumDefinition.TryGetTypeId(Type, out var id) ||
                !GraphEnumDefinition.All.TryGetValue(id, out var definition)) return [];
            var current = Value.GetInt32();
            return definition.Values.Any(item => item.Value == current)
                ? definition.Values
                : definition.Values.Append(new GraphEnumValue(current, $"Unknown ({current})")).ToArray();
        }
    }
    public int EnumValue
    {
        get => Value.GetInt32();
        set => SetValue(JsonSerializer.SerializeToElement(value));
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string JsonValue => Value.GetRawText();
    public string DisplayValue => Type switch
    {
        "vector2" => $"({X}, {Y})",
        "vector3" => $"({X}, {Y}, {Z})",
        "vector4" => $"({X}, {Y}, {Z}, {W})",
        "color" => $"({X}, {Y}, {Z}, {W})",
        _ when GraphEnumDefinition.TryGetTypeId(Type, out _) =>
            EnumValues.FirstOrDefault(item => item.Value == EnumValue)?.Name ?? ValueText,
        _ => ValueText
    };
    private JsonElement Value => _node.GetParameterValue(_definition);

    public double NumericValue
    {
        get => Value.GetDouble();
        set
        {
            if (!double.IsFinite(value)) return;
            if (Type == "int")
            {
                if (value < int.MinValue || value > int.MaxValue) return;
                SetValue(JsonSerializer.SerializeToElement((int)Math.Round(value)));
            }
            else if (Type == "float")
                SetValue(JsonSerializer.SerializeToElement(value));
        }
    }

    public NodeParameterEditorViewModel(GraphWorkspaceViewModel graph, GraphNodeViewModel node, NodeParameterDefinition definition)
    {
        _graph = graph;
        _node = node;
        _definition = definition;
    }

    public string ValueText
    {
        get => Type switch
        {
            "float" => Value.GetDouble().ToString("G17", CultureInfo.CurrentCulture),
            "int" => Value.GetInt32().ToString(CultureInfo.CurrentCulture),
            "string" => Value.GetString() ?? string.Empty,
            _ => JsonValue
        };
        set
        {
            if (Type == "string")
                SetValue(JsonSerializer.SerializeToElement(value ?? string.Empty));
            else if (Type == "int" && int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var integer))
                SetValue(JsonSerializer.SerializeToElement(integer));
            else if (Type == "float" && double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) &&
                     double.IsFinite(number))
                SetValue(JsonSerializer.SerializeToElement(number));
            else if (Type is not ("float" or "int"))
                SetJsonValue(value);
            else
                Reject("Enter a valid number.");
        }
    }

    public bool BoolValue
    {
        get => Value.GetBoolean();
        set => SetValue(JsonSerializer.SerializeToElement(value));
    }

    public string X { get => Component(0); set => SetComponent(0, value); }
    public string Y { get => Component(1); set => SetComponent(1, value); }
    public string Z { get => Component(2); set => SetComponent(2, value); }
    public string W { get => Component(3); set => SetComponent(3, value); }

    public RgbaColor ColorComponents
    {
        get
        {
            var components = Value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
            return new RgbaColor(components[0], components[1], components[2], components[3]);
        }
    }
    public Color ColorValue => ColorComponents.Preview;

    public bool SetJsonValue(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            return SetValue(document.RootElement);
        }
        catch (JsonException)
        {
            Reject("Enter valid JSON.");
            return false;
        }
    }

    public void SetColor(Color color) => SetValue(JsonSerializer.SerializeToElement(
        new[] { color.R / 255.0, color.G / 255.0, color.B / 255.0, color.A / 255.0 }));
    public void SetColor(RgbaColor color) => SetValue(JsonSerializer.SerializeToElement(color.Components));

    public void Refresh()
    {
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(EnumValues));
        OnPropertyChanged(nameof(EnumValue));
        OnPropertyChanged(nameof(NumericValue));
        OnPropertyChanged(nameof(JsonValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Z));
        OnPropertyChanged(nameof(W));
        OnPropertyChanged(nameof(ColorValue));
        OnPropertyChanged(nameof(ColorComponents));
    }

    private string Component(int index)
    {
        if (Value.ValueKind != JsonValueKind.Array || index >= Value.GetArrayLength()) return string.Empty;
        return Value[index].GetDouble().ToString("G17", CultureInfo.CurrentCulture);
    }

    private void SetComponent(int index, string? text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) || !double.IsFinite(number))
        {
            Reject("Enter a valid number.");
            return;
        }
        var values = Value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
        if (index >= values.Length) return;
        values[index] = number;
        SetValue(JsonSerializer.SerializeToElement(values));
    }

    private bool SetValue(JsonElement value)
    {
        try
        {
            _graph.SetParameter(_node, Id, value);
            Status = string.Empty;
            Refresh();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Reject(ex.Message);
            return false;
        }
    }

    private void Reject(string message)
    {
        Status = message;
        Refresh();
    }

}
