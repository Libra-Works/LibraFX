using CommunityToolkit.Mvvm.ComponentModel;
using LibraGpu.Core;
using LiberaFx.Services;
using System.Text.Json;

namespace LiberaFx.ViewModels;

public sealed class GpuWorkspaceViewModel : ObservableObject, IDisposable
{
    public GraphWorkspaceViewModel Graph { get; }
    public Guid ContainerId { get; }
    public GpuEditorState State { get; private set; }
    private string? _kernelId;
    public string? KernelId { get => _kernelId; private set => SetProperty(ref _kernelId, value); }
    public KernelGraph? Kernel => State.Program.Kernels.SingleOrDefault(k => k.Id == KernelId);
    public string Breadcrumb => "CPU > " + Graph.Nodes.FirstOrDefault(n => n.Id == ContainerId)?.Name +
        (Kernel is { } k ? " > " + k.Id : "");
    public IReadOnlyList<GpuOperationDescriptor> Palette => GpuOperationRegistry.Operations.Values.ToArray();
    private IReadOnlyList<GpuDiagnostic>? _diagnostics;
    private string? _text;
    private JsonElement? _stateValue;
    private Dictionary<string, GpuPosition>? _defaultPositions;
    public IReadOnlyList<GpuDiagnostic> Diagnostics => _diagnostics ??= GpuParticleEditorService.Diagnostics(State);
    public string Status { get; private set; } = "Source export only · GPU runtime execution unavailable";
    public event EventHandler? Refreshed;
    public event EventHandler? LayoutChanged;
    public event EventHandler? StatusChanged;
    public GpuWorkspaceViewModel(GraphWorkspaceViewModel graph, Guid container)
    {
        Graph = graph; ContainerId = container;
        State = GpuParticleEditorService.ReadState(graph.GetNode(container));
        _stateValue = StateValue(graph.GetNode(container));
        graph.History.StateChanged += OnHistoryChanged;
    }
    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        var node = Graph.Nodes.FirstOrDefault(n => n.Id == ContainerId);
        if (node is null) { Refreshed?.Invoke(this, EventArgs.Empty); return; }
        var value = StateValue(node);
        if (Same(_stateValue, value)) return;
        var layoutOnly = SameProperty(_stateValue, value, "program") &&
            SameProperty(_stateValue, value, "drafts") && SameProperty(_stateValue, value, "resourceAssetPaths") &&
            SameProperty(_stateValue, value, "rendererSettings") &&
            SameOptionalProperty(_stateValue, value, "fluid2D") &&
            SameOptionalProperty(_stateValue, value, "fluidRenderer") &&
            SameOptionalProperty(_stateValue, value, "generatedResources") &&
            SameOptionalProperty(_stateValue, value, "generatedConsumers") &&
            SameOptionalProperty(_stateValue, value, "fluidAutomaticPlane") &&
            SameOptionalProperty(_stateValue, value, "resourcePlanDraft");
        State = GpuParticleEditorService.ReadState(node);
        _stateValue = value;
        if (layoutOnly) LayoutChanged?.Invoke(this, EventArgs.Empty);
        else
        {
            _defaultPositions = null;
            _diagnostics = null;
            _text = null;
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
    }
    private static JsonElement? StateValue(GraphNodeViewModel node) =>
        node.Parameters?.FirstOrDefault(p => p.Id == GpuParticleEditorService.ParameterId)?.Value;
    private static bool Same(JsonElement? left, JsonElement? right) =>
        left is null && right is null || left is { } a && right is { } b && JsonElement.DeepEquals(a, b);
    private static bool SameProperty(JsonElement? left, JsonElement? right, string property) =>
        left is { ValueKind: JsonValueKind.Object } a && right is { ValueKind: JsonValueKind.Object } b &&
        a.TryGetProperty(property, out var av) && b.TryGetProperty(property, out var bv) &&
        JsonElement.DeepEquals(av, bv);
    private static bool SameOptionalProperty(JsonElement? left, JsonElement? right, string property)
    {
        if (left is not { ValueKind: JsonValueKind.Object } a || right is not { ValueKind: JsonValueKind.Object } b)
        {
            return false;
        }
        var hasLeft = a.TryGetProperty(property, out var av);
        var hasRight = b.TryGetProperty(property, out var bv);
        return hasLeft == hasRight && (!hasLeft || JsonElement.DeepEquals(av, bv));
    }
    public void EnterKernel(string id)
    {
        if (State.Fluid2D is not null)
        {
            throw new InvalidOperationException("2D fluid uses fixed resource passes, not particle kernels.");
        }
        if (!State.Program.Kernels.Any(k => k.Id == id)) throw new ArgumentException("Kernel not found.");
        KernelId = id; _text = null; _defaultPositions = null; Refreshed?.Invoke(this, EventArgs.Empty);
    }
    public void Back() { KernelId = null; _text = null; Refreshed?.Invoke(this, EventArgs.Empty); }
    public IReadOnlySet<(string Node, int Input)> CompatibleInputs(string sourceId)
    {
        var valid = new HashSet<(string, int)>();
        if (Kernel is not { } kernel || Graph.IsReadOnly) return valid;
        var nodes = kernel.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (!nodes.TryGetValue(sourceId, out var source)) return valid;
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(sourceId);
        while (pending.TryPop(out var id))
            if (ancestors.Add(id) && nodes.TryGetValue(id, out var ancestor))
                foreach (var input in ancestor.Inputs) pending.Push(input);
        foreach (var target in kernel.Nodes.Where(node => !ancestors.Contains(node.Id)))
        {
            var defaults = GpuOperationRegistry.GetDefaultInputs(target.Op, target.Type);
            var count = Math.Max(target.Inputs.Count, GpuOperationRegistry.Operations[target.Op].MinInputs);
            for (var input = 0; input < count; input++)
            {
                var types = Enumerable.Range(0, count).Select(index => index == input ? source.Type :
                    index < target.Inputs.Count && nodes.TryGetValue(target.Inputs[index], out var connected) ? connected.Type :
                    index < defaults.Count ? defaults[index].DefaultType : GpuType.Float).ToArray();
                var result = target.Op == GpuOp.SampleFlowMap ? source.Type : target.Type;
                if (GpuOperationRegistry.TypesMatch(target.Op, result, types)) valid.Add((target.Id, input));
            }
        }
        return valid;
    }
    public void Edit(GpuEdit first, params GpuEdit[] remaining)
    {
        GpuParticleEditorService.Apply(Graph, ContainerId, [first, .. remaining]);
    }
    public string Text
    {
        get
        {
            if (_text is not null) return _text;
            if (Kernel is not { } k) return "";
            if (State.Drafts.TryGetValue(k.Id, out var draft)) return _text = draft.Text;
            try { return _text = GpuDsl.Emit(k, resources: State.Program.Resources).Text; }
            catch (GpuValidationException) { return ""; }
        }
    }
    public GpuNode Add(GpuOp op, GpuType type, GpuPosition? position = null)
    {
        if (Kernel is not { } k) throw new InvalidOperationException("Open a kernel.");
        var components = type switch { GpuType.Float2 => 2, GpuType.Float3 => 3, GpuType.Float4 => 4, _ => 1 };
        var descriptors = GpuOperationRegistry.GetDefaultInputs(op, type);
        if (op == GpuOp.Compose) descriptors = descriptors.Take(Math.Max(2, components)).ToArray();
        if (GpuOperationRegistry.TryInferType(op, descriptors.Select(d => d.DefaultType).ToArray(), out var inferred))
            type = inferred;
        var defaults = descriptors.Select(d => new GpuNode { Id = "n_" + Guid.NewGuid().ToString("N"),
            Op = GpuOp.Constant, Type = d.DefaultType, Value = d.DefaultValue.ToList() }).ToArray();
        var n = new GpuNode
        {
            Id = "n_" + Guid.NewGuid().ToString("N"), Op = op, Type = type,
            Value = op == GpuOp.Constant ? Enumerable.Repeat(0d, components).ToList() : [],
            Attribute = op == GpuOp.ReadAttribute ? ParticleAbi.Attributes.First(a => a.Type == type).Id : null,
            Uniform = op == GpuOp.ReadUniform ? ParticleAbi.Uniforms.First(u => u.Value == type).Key : null,
            ComponentIndex = op == GpuOp.Component ? 0 : null,
            Resource = op == GpuOp.SampleFlowMap
                ? State.Program.Resources.FirstOrDefault(resource => resource.Kind == ResourceKind.FlowMap2D)?.Id
                : null,
            Curve = op == GpuOp.FCurve ? GpuCurveData.DefaultLinear() : null,
            Inputs = defaults.Select(d => d.Id).ToList()
        };
        position ??= new(350, k.Nodes.Select(node => GetPosition(node).Y + GpuGraphLayout.NodeHeight(node))
            .DefaultIfEmpty(0).Max() + 60);
        var edits = new List<GpuEdit>();
        for (var i = 0; i < defaults.Length; i++)
        {
            edits.Add(new() { Action = "add", KernelId = k.Id, Node = defaults[i] });
            edits.Add(new() { Action = "move", KernelId = k.Id, NodeId = defaults[i].Id,
                Position = new(position.X - 280, position.Y + i * 105) });
        }
        edits.Add(new() { Action = "add", KernelId = k.Id, Node = n });
        edits.Add(new() { Action = "move", KernelId = k.Id, NodeId = n.Id, Position = position });
        Edit(edits[0], edits.Skip(1).ToArray());
        return n;
    }
    public GpuPosition GetPosition(GpuNode node)
    {
        if (Kernel is not { } kernel) throw new InvalidOperationException("Open a kernel.");
        var key = GpuParticleEditorService.Key(kernel.Id, node.Id);
        if (State.Positions.TryGetValue(key, out var saved)) return saved;
        return (_defaultPositions ??= GpuGraphLayout.CreatePositions(kernel))[key];
    }
    public void ArrangeNodes()
    {
        if (Kernel is not { } kernel) throw new InvalidOperationException("Open a kernel.");
        var positions = GpuGraphLayout.CreatePositions(kernel);
        var edits = kernel.Nodes.Where(node => GetPosition(node) != positions[GpuParticleEditorService.Key(kernel.Id, node.Id)])
            .Select(node => new GpuEdit { Action = "move", KernelId = kernel.Id, NodeId = node.Id,
                Position = positions[GpuParticleEditorService.Key(kernel.Id, node.Id)] }).ToArray();
        if (edits.Length > 0) Edit(edits[0], edits.Skip(1).ToArray());
    }
    private const string ClipboardPrefix = "LIBERA-FX-GPU-GRAPH-1\n";
    private sealed record ClipboardNode(GpuNode Node, GpuPosition Position);
    public string CopyNodes(IEnumerable<string> selection)
    {
        if (Kernel is not { } kernel) throw new InvalidOperationException("Open a kernel.");
        var ids = selection.ToHashSet();
        var nodes = kernel.Nodes.Where(n => ids.Contains(n.Id)).Select(n => new ClipboardNode(
            n with { Inputs = n.Inputs.Select(input => ids.Contains(input) ? input : "").ToList(), SourceSpan = null },
            GetPosition(n))).ToArray();
        var text = ClipboardPrefix + JsonSerializer.Serialize(nodes);
        if (nodes.Length == 0 || text.Length > 1_000_000) throw new ArgumentException("GPU clipboard selection is empty or too large.");
        return text;
    }
    public IReadOnlyList<string> PasteNodes(string text, GpuPosition position)
    {
        if (Kernel is not { } kernel) throw new InvalidOperationException("Open a kernel.");
        if (text.Length > 1_000_000 || !text.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Clipboard does not contain GPU graph nodes.");
        var nodes = JsonSerializer.Deserialize<ClipboardNode[]>(text[ClipboardPrefix.Length..]);
        if (nodes is null || nodes.Length is 0 or > 4096 || nodes.Any(n => n is null || n.Node is null ||
            n.Position is null || !double.IsFinite(n.Position.X) || !double.IsFinite(n.Position.Y) ||
            string.IsNullOrEmpty(n.Node.Id) || n.Node.Inputs is null ||
            !Enum.IsDefined(n.Node.Op) || !Enum.IsDefined(n.Node.Type)))
            throw new ArgumentException("Invalid GPU clipboard nodes.");
        if (nodes.Select(n => n.Node.Id).Distinct().Count() != nodes.Length)
            throw new ArgumentException("Duplicate GPU clipboard node IDs.");
        var ids = nodes.ToDictionary(n => n.Node.Id, _ => "n_" + Guid.NewGuid().ToString("N"));
        var left = nodes.Min(n => n.Position.X); var top = nodes.Min(n => n.Position.Y);
        var edits = new List<GpuEdit>();
        foreach (var source in nodes)
        {
            var node = source.Node with { Id = ids[source.Node.Id],
                Inputs = source.Node.Inputs.Select(input => ids.GetValueOrDefault(input) ?? "").ToList(), SourceSpan = null };
            edits.Add(new() { Action = "add", KernelId = kernel.Id, Node = node });
            edits.Add(new() { Action = "move", KernelId = kernel.Id, NodeId = node.Id,
                Position = new(position.X + source.Position.X - left, position.Y + source.Position.Y - top) });
        }
        Edit(edits[0], edits.Skip(1).ToArray());
        return ids.Values.ToArray();
    }
    public void AddFlow(FlowKind kind)
    {
        var kernel = kind is FlowKind.Spawn or FlowKind.Update
            ? State.Program.Kernels.FirstOrDefault(k => k.Stage.ToString() == kind.ToString()) : null;
        Edit(new()
        {
            Action = "flow-add",
            Flow = new() { Id = "pass_" + Guid.NewGuid().ToString("N"), Kind = kind, Kernel = kernel?.Id,
                Resource = kind == FlowKind.RenderBillboard ? State.Program.Resources.FirstOrDefault(r => r.Kind == ResourceKind.Texture2D)?.Id : null }
        });
    }
    public bool TryCompile(out CompiledGpuProgram? compiled)
    {
        try
        {
            compiled = GpuParticleEditorService.Compile(State);
            Status = $"SourceExport · {compiled.ContentHash} · RuntimeExecuted=false";
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (GpuValidationException e)
        {
            compiled = null; Status = string.Join("\n", e.Diagnostics.Select(d => d.Code + ": " + d.Message));
            StatusChanged?.Invoke(this, EventArgs.Empty); return false;
        }
    }
    public bool ValidateSources()
    {
        if (State.Fluid2D is null)
        {
            return TryCompile(out _);
        }
        var errors = Diagnostics;
        Status = errors.Count == 0 ? LocalizationService.Shared.Get("Fluid.Validated")
            : string.Join("\n", errors.Select(error => error.Message));
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return errors.Count == 0;
    }
    public void Dispose() => Graph.History.StateChanged -= OnHistoryChanged;
}
