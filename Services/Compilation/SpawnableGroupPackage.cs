using System.Security.Cryptography;
using System.Text;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public sealed record SpawnableInput(string PortId, Guid TemplateNodeId, string PortType, int Width, bool IsColor);
public sealed record SpawnableTemplate(Guid ContainerId, Guid SignalTypeId, byte[] Bytecode,
    IReadOnlyList<SpawnableInput> Inputs, byte[] RenderConfiguration, IReadOnlyList<GraphNodeSnapshot> GpuContainers);
public sealed record SpawnableGroupPackage(byte[] ParentBytecode, IReadOnlyList<SpawnableTemplate> Templates)
{
    public const int FormatVersion = 1;
    public static readonly Guid SignalPassthroughTypeId = Guid.Parse("989fa5b5-485f-4e75-8542-9c5500000001");

    public static Guid SignalTypeId(Guid containerId, GraphSnapshot child)
    {
        var shape = string.Join(";", child.Nodes.Where(node => SpawnableGroupSchema.IsInput(node.Kind))
            .Select(node => $"{node.Id:D}:{node.PortType}"));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.SpawnableGroup.Signal.v1:{containerId:D}:{shape}"));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    public static SpawnableGroupPackage Compile(GraphSnapshot graph)
    {
        GraphValidator.Validate(graph);
        var templates = new List<SpawnableTemplate>();
        void Visit(GraphSnapshot current, bool withinTemplate, bool directTemplate)
        {
            foreach (var node in current.Nodes)
            {
                if (SpawnableGroupSchema.IsInput(node.Kind) && !directTemplate)
                    throw new InvalidDataException("Spawn input nodes must be directly inside a Spawnable Group.");
                if (node.Kind == GraphNodeKind.SpawnableGroup)
                {
                    if (withinTemplate) throw new InvalidDataException("Nested Spawnable Groups are not supported.");
                    var child = node.ChildGraph!;
                    var inputs = child.Nodes.Where(item => SpawnableGroupSchema.IsInput(item.Kind))
                        .Select(item => new SpawnableInput(SpawnableGroupSchema.PortId(item.Id), item.Id,
                            item.PortType, item.PortType switch
                            {
                                "Float" => 1, "FloatX2" => 2, "FloatX3" => 3,
                                "FloatX4" or "Color" => 4, _ => throw new InvalidDataException("Invalid spawn input.")
                            }, item.PortType == "Color")).ToArray();
                    Visit(child, true, true);
                    var bytecode = VfxProgram.CompileForPackage(child).ToBinary();
                    var render = RenderProgramBindings.CreateScene(child, 0);
                    var gpu = CollectGpu(child).ToArray();
                    templates.Add(new(node.Id, SignalTypeId(node.Id, child), bytecode, inputs, render, gpu));
                }
                else if (node.ChildGraph is { } child && !GpuParticleEditorService.IsGpu(node.Kind))
                    Visit(child, withinTemplate, false);
            }
        }
        Visit(graph, false, false);
        var parent = VfxProgram.CompileForPackage(graph).ToBinary();
        return new(parent, templates.AsReadOnly());
    }

    private static IEnumerable<GraphNodeSnapshot> CollectGpu(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (GpuParticleEditorService.IsGpu(node.Kind)) yield return node;
            else if (node.ChildGraph is { } child)
                foreach (var nested in CollectGpu(child)) yield return nested;
        }
    }
}
