using CommunityToolkit.Mvvm.ComponentModel;
using LibraGpu.Core;
using LiberaFx.Services;

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
    public IReadOnlyList<GpuDiagnostic> Diagnostics => GpuParticleEditorService.Diagnostics(State);
    public string Status { get; private set; } = "Source export only · GPU runtime execution unavailable";
    public event EventHandler? Refreshed;
    public GpuWorkspaceViewModel(GraphWorkspaceViewModel graph, Guid container)
    {
        Graph = graph; ContainerId = container;
        State = GpuParticleEditorService.ReadState(graph.GetNode(container));
        graph.History.StateChanged += OnHistoryChanged;
    }
    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        var node = Graph.Nodes.FirstOrDefault(n => n.Id == ContainerId);
        if (node is null) { Refreshed?.Invoke(this, EventArgs.Empty); return; }
        State = GpuParticleEditorService.ReadState(node);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }
    public void EnterKernel(string id)
    {
        if (!State.Program.Kernels.Any(k => k.Id == id)) throw new ArgumentException("Kernel not found.");
        KernelId = id; Refreshed?.Invoke(this, EventArgs.Empty);
    }
    public void Back() { KernelId = null; Refreshed?.Invoke(this, EventArgs.Empty); }
    public void Edit(GpuEdit first, params GpuEdit[] remaining)
    {
        State = GpuParticleEditorService.Apply(Graph, ContainerId, [first, .. remaining]);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }
    public string Text
    {
        get
        {
            if (Kernel is not { } k) return "";
            if (State.Drafts.TryGetValue(k.Id, out var draft)) return draft.Text;
            try { return GpuDsl.Emit(k).Text; }
            catch (GpuValidationException) { return ""; }
        }
    }
    public GpuNode Add(GpuOp op, GpuType type)
    {
        if (Kernel is not { } k) throw new InvalidOperationException("Open a kernel.");
        var components = type switch { GpuType.Float2 => 2, GpuType.Float3 => 3, GpuType.Float4 => 4, _ => 1 };
        var n = new GpuNode
        {
            Id = "n_" + Guid.NewGuid().ToString("N"), Op = op, Type = type,
            Value = op == GpuOp.Constant ? Enumerable.Repeat(0d, components).ToList() : [],
            Attribute = op == GpuOp.ReadAttribute ? ParticleAbi.Attributes.First(a => a.Type == type).Id : null,
            Uniform = op == GpuOp.ReadUniform ? ParticleAbi.Uniforms.First(u => u.Value == type).Key : null,
            ComponentIndex = op == GpuOp.Component ? 0 : null,
            Inputs = Enumerable.Repeat("", GpuOperationRegistry.Operations[op].MinInputs).ToList()
        };
        Edit(new() { Action = "add", KernelId = k.Id, Node = n });
        return n;
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
            Refreshed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (GpuValidationException e)
        {
            compiled = null; Status = string.Join("\n", e.Diagnostics.Select(d => d.Code + ": " + d.Message));
            Refreshed?.Invoke(this, EventArgs.Empty); return false;
        }
    }
    public void Dispose() => Graph.History.StateChanged -= OnHistoryChanged;
}
