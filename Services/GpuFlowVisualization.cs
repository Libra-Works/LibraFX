using LibraGpu.Core;

namespace LiberaFx.Services;

public enum GpuFlowPortDirection { Input, Output }

/// <summary>A port projected from a saved GPU flow/kernel, not from a runtime trace.</summary>
public sealed record GpuFlowPort(
    string Name,
    string TypeLabel,
    GpuFlowPortDirection Direction,
    string Detail,
    string? Attribute,
    string? WriteId,
    IReadOnlyList<string> NodeIds,
    bool IsTypeCompatible,
    bool IsReachableFromWrite,
    bool IsBoundToFlow);

public sealed record GpuFlowStepProjection(
    int Index,
    FlowStep Flow,
    KernelGraph? Kernel,
    IReadOnlyList<GpuFlowPort> Inputs,
    IReadOnlyList<GpuFlowPort> Outputs,
    IReadOnlyList<string> UnsupportedReasons,
    FlowMetadata? CompiledMetadata);

/// <summary>
/// A static relationship inferred from a flow's authored attribute write and a later
/// flow's authored attribute read. It does not assert that a dispatch or transfer ran.
/// </summary>
public sealed record GpuFlowParameterTransfer(
    int ProducerIndex,
    int ConsumerIndex,
    string ProducerFlowId,
    string ConsumerFlowId,
    string Attribute,
    string TypeLabel,
    IReadOnlyList<string> ProducerWriteIds,
    IReadOnlyList<string> ProducerValueNodeIds,
    IReadOnlyList<string> ConsumerIds,
    bool IsTypeCompatible,
    bool ProducerPrecedesConsumer);

public sealed record GpuFlowVisualization(
    bool SourceCompiled,
    IReadOnlyList<GpuDiagnostic> Diagnostics,
    IReadOnlyList<GpuFlowStepProjection> Steps,
    IReadOnlyList<GpuFlowParameterTransfer> Transfers)
{
    public string Summary => SourceCompiled
        ? "Authored flow order validated by source compilation · RuntimeExecuted=false · no live trace / 保存された順序はソース検証済み · 実行トレースなし"
        : "Authored flow order only · source compilation unavailable · runtime order not established / 保存された順序のみ · 実行順は未確定";

    public static GpuFlowVisualization Create(GpuEditorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var program = state.Program ?? throw new ArgumentException("GPU program is missing.", nameof(state));
        var authoredFlows = program.Flow ?? [];
        CompiledGpuProgram? compiled = null;
        IReadOnlyList<GpuDiagnostic> diagnostics = Array.Empty<GpuDiagnostic>();
        try { compiled = GpuParticleEditorService.Compile(state); }
        catch (GpuValidationException error) { diagnostics = error.Diagnostics; }

        var kernels = (program.Kernels ?? []).GroupBy(kernel => kernel.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var attributes = (program.Attributes ?? []).GroupBy(attribute => attribute.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var resources = (program.Resources ?? []).GroupBy(resource => resource.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var compiledFlows = compiled?.Flow.ToDictionary(flow => flow.Id, StringComparer.Ordinal) ?? [];
        var steps = new List<GpuFlowStepProjection>();

        for (var index = 0; index < authoredFlows.Count; index++)
        {
            var flow = authoredFlows[index];
            var kernel = flow.Kernel is { } kernelId ? kernels.GetValueOrDefault(kernelId) : null;
            var inputs = new List<GpuFlowPort>();
            var outputs = new List<GpuFlowPort>();
            var unsupported = new List<string>();

            if (flow.Kind is FlowKind.Spawn or FlowKind.Update)
            {
                if (flow.Kernel is null)
                    unsupported.Add("Compute flow has no authored kernel / カーネル未割り当て");
                else if (kernel is null)
                    unsupported.Add($"Kernel '{flow.Kernel}' is not present in the authored program / カーネル参照が不正");
                else
                {
                    var bindingSupported = kernel.Stage == (flow.Kind == FlowKind.Spawn
                        ? KernelStage.Spawn : KernelStage.Update);
                    AddKernelPorts(kernel, attributes, inputs, outputs, bindingSupported);
                    if (!bindingSupported)
                        unsupported.Add($"Kernel stage {kernel.Stage} does not match flow kind {flow.Kind} / フロー種別とカーネル段階が不一致");
                }
            }
            else if (flow.Kind == FlowKind.RenderBillboard)
            {
                if (flow.Kernel is not null)
                {
                    unsupported.Add("Billboard render does not consume an authored compute kernel / 描画カーネル接続は未対応");
                    if (kernel is not null) AddKernelPorts(kernel, attributes, inputs, outputs, false);
                }
                if (flow.Resource is { } resourceId && resources.TryGetValue(resourceId, out var resource))
                {
                    var validResource = resource.Kind == ResourceKind.Texture2D;
                    inputs.Add(new(resource.Id, resource.Kind.ToString(), GpuFlowPortDirection.Input,
                        "authored resource binding · engine-side rendering is pending",
                        null, null, [], validResource, false, validResource));
                    if (!validResource)
                        unsupported.Add($"Billboard render requires Texture2D, not {resource.Kind} / Texture2D以外のリソース種別");
                }
                else
                    unsupported.Add("Billboard render resource is missing or unresolved / 描画リソース未解決");
            }
            else
            {
                unsupported.Add($"{flow.Kind} is declared but not implemented by the GPU source compiler / GPUソースコンパイラー未対応");
                if (flow.Kernel is { } unsupportedKernel)
                {
                    unsupported.Add($"Authored kernel reference: {unsupportedKernel} / カーネル参照");
                    if (kernel is not null) AddKernelPorts(kernel, attributes, inputs, outputs, false);
                }
                if (flow.Resource is { } resourceId)
                {
                    if (resources.TryGetValue(resourceId, out var resource))
                        inputs.Add(new(resource.Id, resource.Kind.ToString(), GpuFlowPortDirection.Input,
                            "authored binding; relationship semantics are unsupported",
                            null, null, [], true, false, false));
                    else
                        unsupported.Add($"Resource '{resourceId}' is not present in the authored program / リソース参照が不正");
                }
            }

            steps.Add(new(index, flow, kernel, inputs, outputs, unsupported,
                compiledFlows.GetValueOrDefault(flow.Id)));
        }

        var transfers = new List<GpuFlowParameterTransfer>();
        foreach (var producer in steps)
        foreach (var output in producer.Outputs.Where(port =>
                     port.Attribute is not null && port.WriteId is not null && port.IsBoundToFlow))
        foreach (var consumer in steps.Where(step => step.Index != producer.Index))
        foreach (var input in consumer.Inputs.Where(port =>
                     port.Attribute == output.Attribute && port.IsReachableFromWrite && port.IsBoundToFlow))
        {
            var declared = attributes.GetValueOrDefault(output.Attribute!);
            transfers.Add(new(producer.Index, consumer.Index, producer.Flow.Id, consumer.Flow.Id, output.Attribute!,
                declared?.Type.ToString() ?? "unknown",
                [output.WriteId!], output.NodeIds, input.NodeIds,
                output.IsTypeCompatible && input.IsTypeCompatible &&
                    declared is not null && output.TypeLabel == input.TypeLabel,
                producer.Index < consumer.Index));
        }

        return new(compiled is not null, diagnostics, steps, transfers);
    }

    private static void AddKernelPorts(KernelGraph kernel,
        IReadOnlyDictionary<string, AttributeDeclaration> attributes,
        List<GpuFlowPort> inputs,
        List<GpuFlowPort> outputs,
        bool bindingSupported)
    {
        var kernelNodes = kernel.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var reachableFromWrite = new HashSet<string>(StringComparer.Ordinal);
        var pendingNodes = new Stack<string>(kernel.Writes.Select(write => write.Value));
        while (pendingNodes.TryPop(out var pendingId))
        {
            if (!reachableFromWrite.Add(pendingId) || !kernelNodes.TryGetValue(pendingId, out var pendingNode))
                continue;
            foreach (var inputId in pendingNode.Inputs)
                if (inputId is not null) pendingNodes.Push(inputId);
        }

        var bindingDetail = bindingSupported ? "" : " · referenced kernel only; flow binding unsupported / カーネル参照のみ・フロー接続未対応";
        foreach (var read in kernel.Nodes.Where(node => node.Op == GpuOp.ReadAttribute && node.Attribute is not null))
        {
            var declared = attributes.GetValueOrDefault(read.Attribute!);
            var reachable = reachableFromWrite.Contains(read.Id);
            inputs.Add(new(read.Attribute!, declared?.Type.ToString() ?? "unknown",
                GpuFlowPortDirection.Input,
                $"particle attribute read · node {read.Id}" +
                (reachable ? "" : " · no path to a kernel write / 出力への経路なし") + bindingDetail,
                read.Attribute, null, [read.Id], declared is not null && read.Type == declared.Type,
                reachable, bindingSupported));
        }

        foreach (var read in kernel.Nodes.Where(node => node.Op == GpuOp.ReadUniform && node.Uniform is not null))
        {
            var known = ParticleAbi.Uniforms.TryGetValue(read.Uniform!, out var type);
            var reachable = reachableFromWrite.Contains(read.Id);
            inputs.Add(new(read.Uniform!, known ? type.ToString() : "unknown",
                GpuFlowPortDirection.Input,
                $"external host uniform · not produced by another flow · node {read.Id}" +
                (reachable ? "" : " · no path to a kernel write / 出力への経路なし") + bindingDetail,
                null, null, [read.Id], known && read.Type == type, reachable, bindingSupported));
        }

        foreach (var write in kernel.Writes)
        {
            var declared = attributes.GetValueOrDefault(write.Attribute);
            var value = kernel.Nodes.FirstOrDefault(node => node.Id == write.Value);
            var compatible = declared is not null && value is not null && value.Type == declared.Type;
            outputs.Add(new(write.Attribute, declared?.Type.ToString() ?? "unknown",
                GpuFlowPortDirection.Output,
                $"attribute write {write.Id} · value node {write.Value}" +
                (value is null ? " (missing)" : $" ({value.Type})") + bindingDetail,
                write.Attribute, write.Id, value is null ? [] : [value.Id], compatible, true, bindingSupported));
        }
    }
}
