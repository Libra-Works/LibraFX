// Included by LibraGpuEffectRuntime.cpp so this diagnostic can exercise the production raster pass.
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuParticleSpriteTest, "Libra.GPU.Render.ParticleSpriteAnimation",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuParticleSpriteTest::RunTest(const FString &parameters)
{
    FString directory;
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraParticleSpriteFixtures="), directory))
    {
        AddInfo(TEXT("Skipped: supply --particle-sprite authoring fixtures with -LibraParticleSpriteFixtures=."));
        return true;
    }
    if (GUsingNullRHI || GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5)
    {
        AddError(TEXT("A real SM5 device is required."));
        return false;
    }
    auto texture = FImageUtils::ImportFileAsTexture2D(FPaths::Combine(directory, TEXT("atlas.png")));
    if (!TestNotNull(TEXT("Fixture atlas loaded"), texture))
    {
        return false;
    }
    texture->UpdateResource();
    FlushRenderingCommands();
    for (const auto layered : {false, true})
    {
        FString json;
        TSharedPtr<FJsonObject> root;
        const auto name = layered ? TEXT("gpu-composition.json") : TEXT("gpu-sequence.json");
        if (!FFileHelper::LoadFileToString(json, *FPaths::Combine(directory, name)) ||
            !FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(json), root))
        {
            AddError(TEXT("Missing authored GPU fixture."));
            return false;
        }
        const auto bundle = root->GetArrayField(TEXT("programs"))[0]->AsObject()->GetObjectField(TEXT("unreal"));
        bundle->GetObjectField(TEXT("resourceAssetPaths"))
            ->SetStringField(TEXT("billboardTexture"), texture->GetPathName());
        FString source;
        FJsonSerializer::Serialize(bundle.ToSharedRef(), TJsonWriterFactory<>::Create(&source));
        auto asset = NewObject<ULibraGpuEffectAsset>();
        FString error;
        if (!asset->ImportBundle(source, error))
        {
            AddError(error);
            return false;
        }
        const auto step =
            asset->GetFlow().FindByPredicate([](const FLibraGpuFlowStep &value) { return value.bSpriteAnimation; });
        if (!TestNotNull(TEXT("Renderer timeline imported"), step))
        {
            return false;
        }
        TestEqual(TEXT("Visible composition part count"), step->SpritePartCount, layered ? 2 : 1);
        const auto values = step->SpriteValues;
        const auto parts = step->SpritePartCount;
        const auto atlas = texture->GetResource()->TextureRHI;
        int32 colors[4][3]{};
        ENQUEUE_RENDER_COMMAND(LibraParticleSpriteFramebuffer)(
            [values, parts, atlas, &colors](FRHICommandListImmediate &commandList)
            {
                for (int32 sample = 0; sample < 4; ++sample)
                {
                    FTestParticle particle{};
                    particle.PositionAge.W = sample == 0 ? 0.f : sample == 1 ? .5f : .8f;
                    particle.VelocityLifetime.W = 5;
                    particle.Color = FVector4f(1, 1, 1, 1);
                    particle.SizeRotation = FVector4f(.8f, .4f, 0, 0);
                    particle.SeedAlive.Y = 1;
                    FRDGBuilder graph(commandList);
                    const auto particles = CreateStructuredBuffer(graph, TEXT("LibraGpu.SpriteTestParticle"),
                                                                  sizeof(particle), 1, &particle, sizeof(particle));
                    const auto timeline =
                        CreateStructuredBuffer(graph, TEXT("LibraGpu.SpriteTestTimeline"), 16, values.Num() / 4,
                                               values.GetData(), values.Num() * sizeof(float));
                    const auto target = graph.CreateTexture(
                        FRDGTextureDesc::Create2D(FIntPoint(64, 64), PF_R8G8B8A8, FClearValueBinding::Black,
                                                  TexCreate_RenderTargetable | TexCreate_ShaderResource),
                        TEXT("LibraGpu.SpriteTestTarget"));
                    AddClearRenderTargetPass(graph, target);
                    auto draw = graph.AllocParameters<FLibraGpuBillboardPassParameters>();
                    draw->VS.ParticleData = graph.CreateSRV(particles);
                    draw->VS.ViewProjection = FMatrix44f::Identity;
                    draw->VS.PreViewTranslation = FVector3f::ZeroVector;
                    draw->VS.CameraRight = FVector3f(1, 0, 0);
                    draw->VS.CameraUp = FVector3f(0, 1, 0);
                    draw->VS.SizeScale = 1;
                    draw->PS.BillboardTexture = atlas;
                    draw->PS.TextureFlags = 0;
                    draw->PS.BillboardSampler = TStaticSamplerState<SF_Point>::GetRHI();
                    draw->PS.RenderTargets[0] = FRenderTargetBinding(target, ERenderTargetLoadAction::ELoad);
                    AddLibraGpuBillboardRasterPass(graph, draw, FIntRect(0, 0, 64, 64), 1, GMaxRHIFeatureLevel,
                                                   TEXT("LibraGpu.SpriteProof"), ELibraGpuBillboardBlend::Alpha,
                                                   nullptr, timeline, sample == 3 ? 0 : parts, true);
                    FRHIGPUTextureReadback readback(TEXT("LibraGpu.SpriteProofReadback"));
                    AddEnqueueCopyPass(graph, &readback, target);
                    graph.Execute();
                    if (!WaitForLibraGpuReadback(commandList, readback))
                    {
                        continue;
                    }
                    int32 pitch = 0;
                    const auto pixels = static_cast<const uint8 *>(readback.Lock(pitch));
                    if (pixels)
                    {
                        for (int32 y = 0; y < 64; ++y)
                        {
                            for (int32 x = 0; x < 64; ++x)
                            {
                                const auto p = pixels + (y * pitch + x) * 4;
                                if (p[0] > 38 && p[1] < p[0] * .3f && p[2] < p[0] * .3f)
                                {
                                    ++colors[sample][0];
                                }
                                if (p[2] > 38 && p[0] < p[2] * .3f && p[1] < p[2] * .3f)
                                {
                                    ++colors[sample][1];
                                }
                                if (p[0] > 38 && p[1] > 38 && p[2] < FMath::Min(p[0], p[1]) * .3f)
                                {
                                    ++colors[sample][2];
                                }
                            }
                        }
                    }
                    readback.Unlock();
                }
            });
        FlushRenderingCommands();
        TestTrue(TEXT("Initial red cell"), colors[0][0] > 4);
        TestTrue(TEXT("Authored frame durations select blue"), colors[1][1] > 4);
        TestTrue(TEXT("Final red cell or transformed gold part"), colors[2][layered ? 2 : 0] > 4);
        if (layered)
        {
            TestTrue(TEXT("Composition transform changes gold coverage"),
                     colors[0][2] > 4 && colors[0][2] != colors[1][2]);
        }
        TestEqual(TEXT("All hidden parts leave clear framebuffer"), colors[3][0] + colors[3][1] + colors[3][2], 0);
        AddInfo(FString::Printf(TEXT("%s: red=%d blue=%d final=%d gold=%d/%d hidden=%d"), name, colors[0][0],
                                colors[1][1], colors[2][layered ? 2 : 0], colors[0][2], colors[1][2], colors[3][0]));
    }
    return !HasAnyErrors();
}
