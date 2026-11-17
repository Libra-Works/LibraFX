using System.Globalization;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class NodeParameterEditorViewModel : ObservableObject
{
    private readonly GraphWorkspaceViewModel _graph;
    private readonly GraphNodeViewModel _node;
    private readonly NodeParameterDefinition _definition;
    private readonly GroupExposedItem? _exposed;
    private readonly Func<bool> _isCurrentInspectorTarget;
    private string _status = string.Empty;

    public string Name => _exposed is { Category.Length: > 0 } item
        ? $"{item.Category} / {_definition.Name}"
        : LocalizationService.Shared.GetOrDefault(_definition.DisplayNameKey, _definition.Name);
    public string Description => _exposed is { Description.Length: > 0 } item ? item.Description : Name;
    public string Type => _definition.Type;
    public string Id => _definition.Id;
    private GraphConnectionViewModel? InputConnection => _exposed is not null ? null : _graph.Connections.FirstOrDefault(connection =>
        connection.Target == _node && (connection.TargetPortId == Id ||
        connection.TargetPortId is null && _node.HasLegacyInput && _node.Definition.Parameters.Count == 1));
    public bool HasConnection => InputConnection is not null;
    public bool CanReset => (_exposed is null || IsCurrentInspectorTarget()) && (_graph.Nodes.Contains(_node) ||
        _exposed is not null && ReferenceEquals(_graph.ContainingSpawnable, _node)) && (_exposed is { } item
        ? _node.GroupOverrides?.Any(value => value.ItemId == item.Id) == true
        : !NodeParameter.ValuesEqual(Value, _definition.DefaultValue));
    public IRelayCommand DisconnectCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand PublishCommand { get; }
    public bool ShowPublish => _exposed is null && _graph.CanPublishGroupParameter(_node, Id);
    public string DisconnectHint => LocalizationService.Shared.Get("Panels.DisconnectParameter", Name);
    public string ResetHint => LocalizationService.Shared.Get("Panels.ResetParameter", Name);
    public string PublishHint => LocalizationService.Shared.Get("Panels.PublishGroupParameter", Name);
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
            var values = definition.Values.Select(item => item with
                { Name = LocalizationService.Shared.GetOrDefault(item.DisplayNameKey, item.Name) }).ToArray();
            return values.Any(item => item.Value == current)
                ? values
                : values.Append(new GraphEnumValue(current, LocalizationService.Shared.Get("Messages.UnknownEnumValue", current))).ToArray();
        }
    }
    public int EnumValue
    {
        get => Value.GetInt32();
        set => SetValue(JsonSerializer.SerializeToElement(value));
    }
    public string Status
    {
        get => _status.StartsWith("Messages.", StringComparison.Ordinal)
            ? LocalizationService.Shared.Get(_status) : _status;
        private set => SetProperty(ref _status, value);
    }
    public string JsonValue => Value.GetRawText();
    internal JsonElement InspectorValue => Value.Clone();
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
    private JsonElement Value => _exposed is { } item
        ? _node.GroupOverrides?.FirstOrDefault(value => value.ItemId == item.Id)?.Components[0].Value ?? _definition.DefaultValue
        : _node.GetParameterValue(_definition);

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

    public NodeParameterEditorViewModel(GraphWorkspaceViewModel graph, GraphNodeViewModel node,
        NodeParameterDefinition definition, GroupExposedItem? exposed = null, Func<bool>? isCurrentInspectorTarget = null)
    {
        _graph = graph;
        _node = node;
        _definition = definition;
        _exposed = exposed;
        _isCurrentInspectorTarget = isCurrentInspectorTarget ?? (() =>
            _graph.Nodes.Contains(_node) && ReferenceEquals(_graph.SelectedNode, _node));
        DisconnectCommand = new RelayCommand(() =>
        {
            if (InputConnection is { } connection) _graph.DisconnectConnection(connection);
            Refresh();
        }, () => HasConnection);
        ResetCommand = new RelayCommand(() =>
        {
            if (_exposed is not null && !IsCurrentInspectorTarget()) return;
            if (_exposed is { } item) _graph.ResetGroupInstanceValue(_node, item.Id);
            else SetValue(_definition.DefaultValue);
            Refresh();
        }, () => CanReset);
        PublishCommand = new RelayCommand(() =>
        {
            try { _graph.PublishGroupParameter(_node, Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or ArgumentException)
            { Reject(MessageLocalization.Error(ex.Message)); }
        }, () => ShowPublish);
        MessageLocalization.Observe(this, static editor => editor.Refresh());
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
                Reject("Messages.EnterValidNumber");
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
            Reject("Messages.EnterValidJson");
            return false;
        }
    }

    internal bool IsCurrentInspectorTarget() =>
        _isCurrentInspectorTarget() && _graph.IsCurrentInspectorTarget(_node);

    internal bool TrySetInspectorValue(JsonElement value) =>
        IsCurrentInspectorTarget() && SetValue(value);

    public void SetColor(Color color) => SetValue(JsonSerializer.SerializeToElement(
        new[] { color.R / 255.0, color.G / 255.0, color.B / 255.0, color.A / 255.0 }));
    public void SetColor(RgbaColor color) => SetValue(JsonSerializer.SerializeToElement(color.Components));

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(HasConnection));
        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(DisconnectHint));
        OnPropertyChanged(nameof(ResetHint));
        OnPropertyChanged(nameof(PublishHint));
        OnPropertyChanged(nameof(ShowPublish));
        DisconnectCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(Status));
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
            Reject("Messages.EnterValidNumber");
            return;
        }
        var values = Value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
        if (index >= values.Length) return;
        values[index] = number;
        SetValue(JsonSerializer.SerializeToElement(values));
    }

    private bool SetValue(JsonElement value)
    {
        if (_exposed is not null && !IsCurrentInspectorTarget()) return false;
        try
        {
            if (_exposed is { } item)
                _graph.SetGroupInstanceValue(_node, new(item.Id, [new(value.Clone())]));
            else _graph.SetParameter(_node, Id, value);
            Status = string.Empty;
            Refresh();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            Reject(MessageLocalization.Error(ex.Message));
            return false;
        }
    }

    private void Reject(string message)
    {
        Status = message;
        Refresh();
    }

}
