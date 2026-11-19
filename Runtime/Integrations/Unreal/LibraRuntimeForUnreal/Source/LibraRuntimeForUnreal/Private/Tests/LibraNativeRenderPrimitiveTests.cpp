#if WITH_DEV_AUTOMATION_TESTS

#include "LibraNativeRenderPrimitives.h"
#include "LibraSpawnableComponent.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Engine/World.h"
#include "Materials/Material.h"
#include "Misc/AutomationTest.h"
#include "RenderCommandFence.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraNativeRenderPrimitiveTest,
    "Libra.Runtime.Rendering.NativePrimitiveContracts",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraNativeRenderPrimitiveTest::RunTest(const FString&)
{
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false,
        FName(TEXT("LibraNativeRenderPrimitiveWorld")), nullptr, true,
        ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated native rendering world"), world))
    {
        return false;
    }
    auto actor = world->SpawnActor<AActor>();
    auto owner = NewObject<ULibraSpawnableComponent>(actor);
    actor->AddInstanceComponent(owner);
    owner->RegisterComponent();
    auto* mesh = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Plane.Plane"));
    auto material = NewObject<UMaterial>(owner, NAME_None, RF_Transient);
    if (!TestNotNull(TEXT("Instanced StaticMesh fixture"), mesh) ||
        !TestNotNull(TEXT("Native scene material fixture"), material))
    {
        world->DestroyWorld(false);
        return false;
    }
    material->SetShadingModel(MSM_Unlit);
    auto needsRecompile = false;
    material->SetMaterialUsage(needsRecompile, MATUSAGE_InstancedStaticMeshes);
    auto materials = TArray<TObjectPtr<UMaterialInterface>>{material};

    {
        auto primitive = MakeUnique<FLibraNativeInstancedMeshPrimitive>();
        TestTrue(TEXT("Instanced scene primitive registers without an ISM Component"),
            primitive->Create(*owner, mesh, MakeArrayView(materials), FTransform::Identity));
        auto transforms = TArray<FTransform>{
            FTransform(FRotator::ZeroRotator, FVector(0, 0, 10), FVector(1)),
            FTransform(FRotator::ZeroRotator, FVector(20, 0, 10), FVector(1)),
            FTransform(FRotator::ZeroRotator, FVector(40, 0, 10), FVector(1))};
        auto colors = TArray<FLinearColor>{
            FLinearColor(1, 0, 0, 1), FLinearColor(0, 1, 0, 1), FLinearColor(0, 0, 1, 1)};
        TestTrue(TEXT("Three instance transforms and RGBA custom-data records submit"),
            primitive->Update(transforms, colors, FTransform::Identity));
        auto renderFence = FRenderCommandFence{};
        renderFence.BeginFence();
        renderFence.Wait();
        TestEqual(TEXT("Instance data stores three live particles"), primitive->LiveInstanceCount, 3);
        TestTrue(TEXT("Allocated capacity covers live instances"),
            primitive->GetAllocatedInstanceCapacity() >= primitive->LiveInstanceCount);
        TestTrue(TEXT("Reported transfer includes every transform and four custom floats"),
            primitive->SubmittedBytes >= uint64(3) *
                (sizeof(FRenderTransform) + sizeof(float) * 4));
        TestTrue(TEXT("Particle bounds include translated instances"),
            primitive->WorldBounds.BoxExtent.X > 10 && !primitive->WorldBounds.ContainsNaN());
        auto components = TArray<UInstancedStaticMeshComponent*>{};
        actor->GetComponents(components);
        TestEqual(TEXT("CPU instance rendering creates no ISM Component"), components.Num(), 0);

        auto plane = MakeUnique<FLibraNativeFlexiblePlanePrimitive>();
        TestTrue(TEXT("FlexiblePlane descriptor registers without a Component"),
            plane->Create(*owner, material, FTransform::Identity));
        auto vertices = TArray<FDynamicMeshVertex>{};
        vertices.Emplace(FVector3f(0, 0, 0), FVector3f(1, 0, 0), FVector3f(0, 0, 1),
            FVector2f(0, 0), FColor::White);
        vertices.Emplace(FVector3f(0, 20, 0), FVector3f(1, 0, 0), FVector3f(0, 0, 1),
            FVector2f(1, 0), FColor::White);
        vertices.Emplace(FVector3f(0, 20, 30), FVector3f(1, 0, 0), FVector3f(0, 0, 1),
            FVector2f(1, 1), FColor::White);
        auto indices = TArray<uint32>{0, 1, 2};
        plane->SetGeometry(vertices, indices, renderFence, FTransform::Identity);
        TestTrue(TEXT("FlexiblePlane proxy publishes calculated bounds"),
            plane->WorldBounds.BoxExtent.SizeSquared() > 0 && !plane->WorldBounds.ContainsNaN());
        TestTrue(TEXT("FlexiblePlane geometry transfer is counted"),
            plane->SubmittedBytes >= sizeof(FDynamicMeshVertex) * 3 + sizeof(uint32) * 3);
        components.Reset();
        actor->GetComponents(components);
        TestEqual(TEXT("FlexiblePlane rendering creates no ISM Component"), components.Num(), 0);
        plane->Remove();
        primitive->Remove();
        plane.Reset();
        primitive.Reset();
    }

    world->DestroyWorld(false);
    return true;
}

#endif
