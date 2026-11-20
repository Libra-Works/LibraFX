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
        ? Text("Gpu.FlowSummaryCompiled", "Authored flow order validated by source compilation · RuntimeExecuted=false · no live trace")
        : Text("Gpu.FlowSummaryUncompiled", "Authored flow order only · source compilation unavailable · runtime order not established");

    private static string Text(string key, string fallback, params object?[] args) =>
        LocalizationService.Shared is { } loc
            ? loc.Get(key, args)
            : (args.Length == 0 ? fallback : string.Format(System.Globalization.CultureInfo.InvariantCulture, fallback, args));

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
                    unsupported.Add(Text("Gpu.Unsupported.NoKernel", "Compute flow has no authored kernel"));
                else if (kernel is null)
                    unsupported.Add(Text("Gpu.Unsupported.MissingKernel", "Kernel '{0}' is not present in the authored program", flow.Kernel));
                else
                {
                    var bindingSupported = kernel.Stage == (flow.Kind == FlowKind.Spawn
                        ? KernelStage.Spawn : KernelStage.Update);
                    AddKernelPorts(kernel, attributes, inputs, outputs, bindingSupported);
                    if (!bindingSupported)
                        unsupported.Add(Text("Gpu.Unsupported.StageMismatch", "Kernel stage {0} does not match flow kind {1}", kernel.Stage, flow.Kind));
                }
            }
            else if (flow.Kind == FlowKind.RenderBillboard)
            {
                if (flow.Kernel is not null)
                {
                    unsupported.Add(Text("Gpu.Unsupported.BillboardKernel", "Billboard render does not consume an authored compute kernel"));
                    if (kernel is not null) AddKernelPorts(kernel, attributes, inputs, outputs, false);
                }
                if (flow.Resource is { } resourceId && resources.TryGetValue(resourceId, out var resource))
                {
                    var validResource = resource.Kind == ResourceKind.Texture2D;
                    inputs.Add(new(resource.Id, resource.Kind.ToString(), GpuFlowPortDirection.Input,
                        "authored resource binding · engine-side rendering is pending",
                        null, null, [], validResource, false, validResource));
                    if (!validResource)
                        unsupported.Add(Text("Gpu.Unsupported.BillboardResourceType", "Billboard render requires Texture2D, not {0}", resource.Kind));
                }
                else
                    unsupported.Add(Text("Gpu.Unsupported.BillboardResourceMissing", "Billboard render resource is missing or unresolved"));
            }
            else
            {
                unsupported.Add(Text("Gpu.Unsupported.FlowKind", "{0} is declared but not implemented by the GPU source compiler", flow.Kind));
                if (flow.Kernel is { } unsupportedKernel)
                {
                    unsupported.Add(Text("Gpu.Unsupported.KernelReference", "Authored kernel reference: {0}", unsupportedKernel));
                    if (kernel is not null) AddKernelPorts(kernel, attributes, inputs, outputs, false);
                }
                if (flow.Resource is { } resourceId)
                {
                    if (resources.TryGetValue(resourceId, out var resource))
                        inputs.Add(new(resource.Id, resource.Kind.ToString(), GpuFlowPortDirection.Input,
                            "authored binding; relationship semantics are unsupported",
                            null, null, [], true, false, false));
                    else
                        unsupported.Add(Text("Gpu.Unsupported.MissingResource", "Resource '{0}' is not present in the authored program", resourceId));
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

        var bindingDetail = bindingSupported ? "" : Text("Gpu.Port.BindingUnsupported", " · referenced kernel only; flow binding unsupported");
        foreach (var read in kernel.Nodes.Where(node => node.Op == GpuOp.ReadAttribute && node.Attribute is not null))
        {
            var declared = attributes.GetValueOrDefault(read.Attribute!);
            var reachable = reachableFromWrite.Contains(read.Id);
            inputs.Add(new(read.Attribute!, declared?.Type.ToString() ?? "unknown",
                GpuFlowPortDirection.Input,
                $"particle attribute read · node {read.Id}" +
                (reachable ? "" : Text("Gpu.Port.NoPathToWrite", " · no path to a kernel write")) + bindingDetail,
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
                (reachable ? "" : Text("Gpu.Port.NoPathToWrite", " · no path to a kernel write")) + bindingDetail,
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
