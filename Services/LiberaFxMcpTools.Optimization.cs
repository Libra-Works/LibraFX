using System.ComponentModel;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

public sealed partial class LiberaFxMcpTools
{
    [McpServerTool(Name = "libera_graph_analyze_optimization", ReadOnly = true, OpenWorld = false)]
    [Description("Compare default distribution export with CPU kernel fusion disabled for the current graph and its descendants. Reports actual package/CPU byte counts without editing nodes, navigating or changing history. Smaller binary size is not a measured frame-time improvement. Read at effect scope for CPU/GPU containers; isolated stage attributes cannot compile as an effect.")]
    public static async Task<string> AnalyzeOptimization(ActiveGraphProvider activeGraph)
    {
        var snapshot = await ChatMcpExecution.InvokeAsync(() => activeGraph.Graph.CreateCurrentSnapshot() with
            { AutoDestroyWhenIdle = activeGraph.Graph.AutoDestroyWhenIdle });
        try
        {
            var original = SpawnableGroupPackage.Compile(snapshot,
                options: new VfxCompileOptions { OptimizeCpuParticleKernels = false });
            var optimized = SpawnableGroupPackage.Compile(snapshot);
            var originalBytes = SpawnableGroupPackageBinary.Serialize(original).LongLength;
            var optimizedBytes = SpawnableGroupPackageBinary.Serialize(optimized).LongLength;
            static long CpuBytes(SpawnableGroupPackage package) => package.CpuPrograms.LongLength +
                package.Templates.Sum(template => template.CpuPrograms.LongLength);
            static IEnumerable<GraphNodeSnapshot> Nodes(GraphSnapshot graph) => graph.Nodes.SelectMany(node =>
                new[] { node }.Concat(node.ChildGraph is { } child ? Nodes(child) : []));
            var nodes = Nodes(snapshot).ToArray();
            return JsonSerializer.Serialize(new
            {
                scope = "currentGraphAndDescendants",
                authoringGraphChanged = false,
                runtimeExecuted = false,
                measuredFrameMilliseconds = (double?)null,
                authoring = new { nodes = nodes.Length, cpuSystems = nodes.Count(node => CpuParticleSchema.IsContainer(node.Kind)),
                    gpuSystems = nodes.Count(node => GpuParticleEditorService.IsGpu(node.Kind)) },
                binary = new
                {
                    unoptimizedPackageBytes = originalBytes, optimizedPackageBytes = optimizedBytes,
                    packageBytesSaved = originalBytes - optimizedBytes,
                    unoptimizedCpuBytes = CpuBytes(original), optimizedCpuBytes = CpuBytes(optimized),
                    cpuBytesSaved = CpuBytes(original) - CpuBytes(optimized)
                },
                automaticOptimization = new
                {
                    enabled = true, appliesDuring = "distributionExport", liveDebugRetainsSourceNodes = true,
                    currentCpuFusion = "Single-consumer Age / Life -> Clamp(0,1) -> 1-x -> Compose RGBA with constant RGB; eligible V3 stages only.",
                    barriers = new[] { "externalStageIO", "unknownCallbacks", "sharedIntermediateConsumers", "guardsOrNewerFormats" }
                },
                interpretation = "Compare compiled output and same-backend runtime evidence. Fewer authoring nodes, fewer bytes or faster MCP calls do not by themselves prove faster Update. No matching fusion is not proof that the effect cannot be improved."
            }, JsonOptions);
        }
        catch (InvalidDataException ex) { throw new McpException(ex.Message); }
    }
}
