using LibraGpu.Core;

internal static partial class Program
{
    private static void RunResourcePlanTests()
    {
        GpuFlowResource Buffer(string id) => new()
        {
            Id = id, Kind = GpuFlowResourceKind.StorageBuffer, Format = GpuResourceFormat.Float4,
            Width = 64, Lifetime = GpuResourceLifetime.Persistent, Initialization = GpuResourceInitialization.Zero
        };
        GpuResourcePass Pass(string id, params GpuResourceUse[] uses) => new()
        { Id = id, Operation = "TestOperation", Uses = uses.ToList() };
        void Invalid(GpuResourcePlan plan, string code)
        {
            try { GpuResourcePlanCompiler.Compile(plan); throw new InvalidOperationException("Invalid resource plan accepted."); }
            catch (GpuValidationException error)
            { Check(error.Diagnostics.Any(d => d.Code == code), $"Expected {code}, got {string.Join(',', error.Diagnostics.Select(d => d.Code))}"); }
        }

        Run("resource dependency order preserves versions and immutable plans", () =>
        {
            var plan = new GpuResourcePlan
            {
                Resources = [Buffer("field")],
                Passes =
                [
                    Pass("a-overwrite", new GpuResourceUse("out", "field", 2, GpuResourceAccess.StorageWrite)),
                    Pass("b-read", new GpuResourceUse("in", "field", 1, GpuResourceAccess.StorageRead)),
                    Pass("c-produce", new GpuResourceUse("out", "field", 1, GpuResourceAccess.StorageWrite)),
                    Pass("d-initial", new GpuResourceUse("in", "field", 0, GpuResourceAccess.StorageRead))
                ]
            };
            var compiled = GpuResourcePlanCompiler.Compile(plan);
            Check(compiled.Passes.Select(p => p.Id).SequenceEqual(["d-initial", "c-produce", "b-read", "a-overwrite"]),
                "Order must preserve initialization, producer-read and read-before-overwrite dependencies.");
            Check(compiled.OwnedBytes == 1024 && compiled.Transitions.Count == 3, "Memory and storage hazards must be explicit.");
            Check(compiled.Allocations[0] is { FirstPass: 0, LastPass: 3 }, "Lifetime must cover all accesses.");
            var json = GpuResourcePlanCompiler.Serialize(plan);
            Check(GpuResourcePlanCompiler.Serialize(GpuResourcePlanCompiler.Parse(json)) == json, "Resource plan roundtrip.");
            Check(GpuResourcePlanCompiler.Serialize(plan with { Passes = plan.Passes.AsEnumerable().Reverse().ToList() }) == json,
                "Serialization must be independent of authoring list order.");
            plan.Passes[0].Uses.Clear(); plan.Passes.Clear(); plan.Resources.Clear();
            Check(compiled.Passes.Count == 4 && compiled.Passes[^1].Uses.Count == 1 && compiled.Allocations.Count == 1,
                "Compiled metadata must snapshot mutable authoring lists.");
        });

        Run("resource undefined inputs, physical aliases and conflicting producers rejected", () =>
        {
            var plan = new GpuResourcePlan { Resources = [Buffer("field")] };
            Invalid(plan with { Passes = [Pass("missing", new GpuResourceUse("in", "absent", 0, GpuResourceAccess.StorageRead))] }, "ResourceBinding");
            Invalid(plan with { Passes = [Pass("read", new GpuResourceUse("in", "field", 1, GpuResourceAccess.StorageRead))] }, "ResourceProducer");
            Invalid(plan with { Resources = [Buffer("field") with { Initialization = GpuResourceInitialization.Undefined }],
                Passes = [Pass("read", new GpuResourceUse("in", "field", 0, GpuResourceAccess.StorageRead))] }, "ResourceUninitialized");
            Invalid(plan with { Passes = [Pass("alias", new GpuResourceUse("in", "field", 0, GpuResourceAccess.StorageRead),
                new GpuResourceUse("out", "field", 1, GpuResourceAccess.StorageWrite))] }, "ResourceAlias");
            Invalid(plan with { Passes = [Pass("write-a", new GpuResourceUse("out", "field", 1, GpuResourceAccess.StorageWrite)),
                Pass("write-b", new GpuResourceUse("out", "field", 1, GpuResourceAccess.StorageWrite))] }, "ResourceWriter");
            Invalid(plan with { Passes = [Pass("gap", new GpuResourceUse("out", "field", 2, GpuResourceAccess.StorageWrite))] }, "ResourceVersion");
            Invalid(plan with { Passes = [Pass("zero", new GpuResourceUse("out", "field", 0, GpuResourceAccess.StorageWrite))] }, "ResourceAccess");
            Invalid(plan with { Passes = [Pass("a") with { After = ["b"] }, Pass("b") with { After = ["a"] }] }, "ResourceCycle");
        });

        Run("resource feedback is explicit initialized history", () =>
        {
            var plan = new GpuResourcePlan
            {
                Resources = [Buffer("field")], Passes =
                [Pass("feedback", new GpuResourceUse("history", "field", 0, GpuResourceAccess.StorageRead, GpuResourceFrame.Previous),
                    new GpuResourceUse("output", "field", 1, GpuResourceAccess.StorageWrite))]
            };
            var compiled = GpuResourcePlanCompiler.Compile(plan);
            Check(compiled.OwnedBytes == 2048 && compiled.Allocations[0].RequiresHistory,
                "Feedback must allocate independent current and previous frame storage.");
            Invalid(plan with { Resources = [Buffer("field") with { Lifetime = GpuResourceLifetime.Temporary }] }, "ResourceHistory");
            Invalid(plan with { Resources = [Buffer("field") with { Initialization = GpuResourceInitialization.Undefined }] }, "ResourceHistory");
            Invalid(plan with { Passes = [Pass("history-write", new GpuResourceUse("out", "field", 1,
                GpuResourceAccess.StorageWrite, GpuResourceFrame.Previous))] }, "ResourceAccess");
        });

        Run("resource external ownership and backend capabilities", () =>
        {
            var plan = new GpuResourcePlan
            {
                Resources = [new()
                {
                    Id = "main-color", Kind = GpuFlowResourceKind.RenderTarget, Format = GpuResourceFormat.Rgba16Float,
                    Width = 1280, Height = 720, Lifetime = GpuResourceLifetime.External,
                    Initialization = GpuResourceInitialization.External, ExternalBinding = "host.main-color"
                }],
                Passes = [Pass("draw", new GpuResourceUse("color", "main-color", 1, GpuResourceAccess.RenderTarget))]
            };
            var capabilities = new GpuResourceCapabilities(Enum.GetValues<GpuFlowResourceKind>().ToHashSet(),
                Enum.GetValues<GpuResourceFormat>().ToHashSet(), Enum.GetValues<GpuResourceAccess>().ToHashSet(),
                new Dictionary<string, GpuExternalResourceBinding>(StringComparer.Ordinal), 1024);
            try { GpuResourcePlanCompiler.Compile(plan, capabilities); throw new InvalidOperationException("Unprovided framebuffer accepted."); }
            catch (GpuValidationException error) { Check(error.Diagnostics[0].Code == "ResourceCapability", "Missing main color binding diagnostic."); }
            var bound = capabilities with
            {
                ExternalBindings = new Dictionary<string, GpuExternalResourceBinding>(StringComparer.Ordinal)
                {
                    ["host.main-color"] = new(GpuFlowResourceKind.RenderTarget, GpuResourceFormat.Rgba16Float,
                        1280, 720, 1, new HashSet<GpuResourceAccess> { GpuResourceAccess.RenderTarget })
                }
            };
            var compiled = GpuResourcePlanCompiler.Compile(plan, bound);
            Check(compiled.OwnedBytes == 0 && compiled.Allocations[0].Resource.Lifetime == GpuResourceLifetime.External,
                "Host targets must never be counted or released as owned allocations.");
            try
            {
                GpuResourcePlanCompiler.Compile(plan with
                { Passes = [Pass("sample", new GpuResourceUse("in", "main-color", 0, GpuResourceAccess.Sample))] }, bound);
                throw new InvalidOperationException("A host target with no sampling permission was sampled.");
            }
            catch (GpuValidationException error) { Check(error.Diagnostics[0].Code == "ResourceCapability", "Per-binding access permissions."); }
            try
            {
                GpuResourcePlanCompiler.Compile(plan with { Resources = [plan.Resources[0] with { Width = 640 }] }, bound);
                throw new InvalidOperationException("Host target dimensions were silently changed.");
            }
            catch (GpuValidationException error) { Check(error.Diagnostics[0].Code == "ResourceCapability", "External allocation shape contract."); }
            Invalid(plan with { Resources = [plan.Resources[0] with { Lifetime = GpuResourceLifetime.Persistent }] }, "ResourceDeclaration");
            try
            {
                GpuResourcePlanCompiler.Compile(new() { Resources = [Buffer("field")] }, capabilities with { MaximumOwnedBytes = 1023 });
                throw new InvalidOperationException("Resource memory budget exceeded.");
            }
            catch (GpuValidationException error) { Check(error.Diagnostics[0].Code == "ResourceBudget", "Allocation budget diagnostic."); }
        });

        Run("fluid solver uses portable versioned resource plan", () =>
        {
            foreach (var iterations in new uint[] { 1, 2, 39, 40, 128 })
            {
                var settings = new Fluid2DSettings { Width = 24, Height = 40, PressureIterations = iterations };
                var plan = Fluid2DCompiler.ResourcePlan(settings);
                var compiled = GpuResourcePlanCompiler.Compile(plan);
                Check(compiled.OwnedBytes == 24 * 40 * 144 && compiled.Passes.Count == Fluid2DCompiler.Step(settings).Count + 2,
                    "Fluid graph must describe all allocations, simulation passes and visualization.");
                Check(compiled.Passes[^1].Id == "visualize" && compiled.Transitions.Any(t =>
                    t.Resource == "DyeOutput" && t.Before == GpuResourceAccess.StorageWrite && t.After == GpuResourceAccess.Sample),
                    "Generated output must transition from compute to sampled visualization.");
                var projection = compiled.Passes.Single(p => p.Operation == "LibraFluidProjectVelocity");
                Check(projection.Uses.Single(u => u.Binding == "Read1").Resource ==
                    (iterations % 2 == 1 ? "PressureScratch" : "Pressure"), "Resource graph must retain pressure ping-pong parity.");
                Check(Fluid2DCompiler.Metadata(settings, ShaderFormat.UnityCompute).ResourcePlan?.OwnedBytes == compiled.OwnedBytes,
                    "Actual fluid exports must contain the compiled resource contract.");
            }
        });

        Run("resource plan rejects malformed persistence", () =>
        {
            foreach (var json in new[] { "null", "[]", "{\"version\":1,\"Version\":1}", "{\"unknown\":true}",
                "{\"resources\":null}", "{\"passes\":[null]}", "{\"resources\":[{\"id\":\"a\",\"kind\":0,\"width\":8}]}" })
            {
                try { GpuResourcePlanCompiler.Parse(json); throw new InvalidOperationException("Malformed resource plan accepted."); }
                catch (GpuValidationException) { }
            }
        });
    }
}
