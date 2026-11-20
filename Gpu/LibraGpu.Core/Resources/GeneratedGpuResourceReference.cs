namespace LibraGpu.Core;

/// <summary>A producer output in the same live effect scope, separate from an engine asset.</summary>
public sealed record GeneratedGpuResourceReference(Guid ProducerNodeId, string OutputId);
