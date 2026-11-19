#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
#include "LibraSkeletalPoseTargetComponent.h"
#include "LibraSpawnableAsset.h"
#include "LibraSpawnableComponent.h"
#include "Animation/AnimSequence.h"
#include "Animation/AnimSingleNodeInstance.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Components/SkeletalMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/SceneCapture2D.h"
#include "Engine/SkeletalMesh.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Engine/World.h"
#include "ImageUtils.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "RenderingThread.h"
#include "liberafx/LbSpawnablePackage.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSkeletalPosePackageTest, "Libra.Runtime.SkeletalPose.AnimatedPackage",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSkeletalPosePackageTest::RunTest(const FString&)
{
    auto fixture = FString{}, meshPath = FString{}, animationPath = FString{}, bonePath = FString{};
    const auto* commandLine = FCommandLine::Get();
    const auto hasFixture = FParse::Value(commandLine, TEXT("LibraSkeletalFixture="), fixture);
    const auto hasMesh = FParse::Value(commandLine, TEXT("LibraSkeletalMesh="), meshPath);
    const auto hasAnimation = FParse::Value(commandLine, TEXT("LibraSkeletalAnimation="), animationPath);
    const auto hasBone = FParse::Value(commandLine, TEXT("LibraSkeletalBone="), bonePath);
    if (!hasFixture && !hasMesh && !hasAnimation && !hasBone)
    {
        AddInfo(TEXT("Skipped: animated SOURCE acceptance requires project-specific skeletal fixtures."));
        return true;
    }
    if (!hasFixture || !hasMesh || !hasAnimation || !hasBone)
    {
        AddError(TEXT("Supply the compiled skeletal fixture, mesh, animation and matching exact bone path."));
        return false;
    }
    auto* mesh = LoadObject<USkeletalMesh>(nullptr, *meshPath);
    auto* clip = LoadObject<UAnimSequence>(nullptr, *animationPath);
    auto bytes = TArray<uint8>{};
    auto package = liberafx::SpawnablePackage{};
    if (!TestTrue(TEXT("Load isolated skeletal asset, clip and compiler package"),
        mesh && clip && mesh->GetSkeleton() && mesh->GetSkeleton() == clip->GetSkeleton() &&
        FFileHelper::LoadFileToArray(bytes, *fixture) &&
        liberafx::loadSpawnablePackage(bytes.GetData(), bytes.Num(), package) == liberafx::Result::Ok))
    {
        return false;
    }
    auto boneName = FString{};
    if (!bonePath.Split(TEXT("/"), nullptr, &boneName, ESearchCase::CaseSensitive, ESearchDir::FromEnd))
    {
        AddError(TEXT("Use a child bone to exercise all three distinct spaces."));
        return false;
    }
    const auto& skeleton = mesh->GetRefSkeleton();
    const auto bone = skeleton.FindBoneIndex(FName(*boneName));
    if (!TestTrue(TEXT("Fixture child bone exists"), bone != INDEX_NONE && skeleton.GetParentIndex(bone) != INDEX_NONE))
    {
        return false;
    }
    const auto parent = skeleton.GetParentIndex(bone);
    const auto setup = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto* world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &setup);
    if (!TestNotNull(TEXT("Isolated animated test world"), world))
    {
        return false;
    }
    ON_SCOPE_EXIT { world->DestroyWorld(false); };
    auto* actor = world->SpawnActor<AActor>();
    auto* external = world->SpawnActor<AActor>();
    auto createMesh = [&](AActor* owner, const FTransform& position)
    {
        auto* component = NewObject<USkeletalMeshComponent>(owner);
        owner->AddInstanceComponent(component);
        owner->SetRootComponent(component);
        component->SetSkeletalMesh(mesh);
        component->SetRelativeTransform(position);
        component->RegisterComponent();
        component->PlayAnimation(clip, false);
        return component;
    };
    auto* ownedPose = createMesh(actor, FTransform(FRotator(0, 40, 0), FVector(30, 80, 110), FVector(2, 1, 0.5)));
    auto* registeredPose = createMesh(external, FTransform(FRotator(20, 0, 0), FVector(-70, 5, 80), FVector(1, 3, 2)));
    auto* provider = NewObject<ULibraSkeletalPoseTargetComponent>(actor);
    provider->SkeletalMesh = ownedPose;
    actor->AddInstanceComponent(provider);
    provider->RegisterComponent();
    auto* effect = NewObject<ULibraSpawnableComponent>(actor);
    effect->bAutoPlay = false;
    effect->Package = NewObject<ULibraSpawnableAsset>(effect);
    actor->AddInstanceComponent(effect);
    effect->RegisterComponent();
    auto seek = [&](USkeletalMeshComponent& pose, float fraction)
    {
        auto* animation = pose.GetSingleNodeInstance();
        if (!animation)
        {
            return false;
        }
        animation->SetPosition(clip->GetPlayLength() * fraction, false);
        pose.TickAnimation(0, false);
        pose.RefreshBoneTransforms();
        return pose.GetComponentSpaceTransforms().IsValidIndex(bone) &&
            pose.GetComponentSpaceTransforms().IsValidIndex(parent);
    };
    if (!TestTrue(TEXT("Evaluate actual skeletal animation"),
        seek(*ownedPose, 0.1f) && seek(*registeredPose, 0.3f)) ||
        !TestEqual(TEXT("Admit actual compiled SOURCE package"),
        effect->Package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok) ||
        !TestEqual(TEXT("Play with only the owner target registered"), effect->Play(), ELibraRuntimeResult::Ok))
    {
        return false;
    }
    auto tick = [&]() { effect->TickComponent(1.0f / 60, LEVELTICK_All, &effect->PrimaryComponentTick); };
    auto* capture = world->SpawnActor<ASceneCapture2D>();
    auto* camera = capture->GetCaptureComponent2D();
    auto* target = NewObject<UTextureRenderTarget2D>(capture);
    constexpr auto imageSize = 256;
    target->ClearColor = FLinearColor::Black;
    target->InitCustomFormat(imageSize, imageSize, PF_B8G8R8A8, false);
    camera->TextureTarget = target;
    camera->CaptureSource = SCS_FinalColorLDR;
    camera->ProjectionType = ECameraProjectionMode::Orthographic;
    camera->bCaptureEveryFrame = false;
    camera->bCaptureOnMovement = false;
    camera->ShowFlags.SetLighting(false);
    camera->ShowFlags.SetAtmosphere(false);
    camera->ShowFlags.SetFog(false);
    camera->PrimitiveRenderMode = ESceneCapturePrimitiveRenderMode::PRM_UseShowOnlyList;
    auto readMarker = [&](const TCHAR* label, const FVector& expected, bool visible)
    {
        auto markers = TArray<UStaticMeshComponent*>{};
        actor->GetComponents(markers);
        if (!TestEqual(TEXT("One ordinary Model Draw marker"), markers.Num(), 1))
        {
            return false;
        }
        if (visible && !TestTrue(TEXT("Draw object's world position tracks the animated bone"),
            markers[0]->GetComponentLocation().Equals(expected, 0.01)))
        {
            return false;
        }
        camera->ClearShowOnlyComponents();
        camera->ShowOnlyComponent(markers[0]);
        world->SendAllEndOfFrameUpdates();
        camera->CaptureScene();
        FlushRenderingCommands();
        auto pixels = TArray<FColor>{};
        if (!TestTrue(TEXT("SOURCE framebuffer is readable"),
            target->GameThread_GetRenderTargetResource()->ReadPixels(pixels) && pixels.Num() == imageSize * imageSize))
        {
            return false;
        }
        auto count = 0;
        auto sum = FVector2D::ZeroVector;
        for (auto index = 0; index < pixels.Num(); ++index)
        {
            const auto& pixel = pixels[index];
            if (pixel.R > 24 || pixel.G > 24 || pixel.B > 24)
            {
                ++count;
                sum += FVector2D(index % imageSize + 0.5, index / imageSize + 0.5);
            }
        }
        auto png = TArray64<uint8>{};
        FImageUtils::PNGCompressImageArray(imageSize, imageSize,
            TArrayView64<const FColor>(pixels.GetData(), pixels.Num()), png);
        const auto filename = FPaths::GetPath(fixture) / FString::Printf(TEXT("unreal-source-%s.png"), label);
        if (!TestTrue(TEXT("Save isolated SOURCE framebuffer"), FFileHelper::SaveArrayToFile(png, *filename)))
        {
            return false;
        }
        AddInfo(FString::Printf(TEXT("SOURCE marker %s: %d pixels"), label, count));
        if (!visible)
        {
            return TestEqual(TEXT("Invalid SOURCE does not render a marker"), count, 0);
        }
        const auto relative = expected - camera->GetComponentLocation();
        const auto projected = FVector2D(FVector::DotProduct(relative, camera->GetRightVector()),
            -FVector::DotProduct(relative, camera->GetUpVector())) * (imageSize / camera->OrthoWidth) +
            FVector2D(imageSize / 2.0, imageSize / 2.0);
        return TestTrue(TEXT("Framebuffer marker pixels are centered on the bone"),
            count >= 12 && FVector2D::Distance(sum / count, projected) < 6);
    };
    auto read = [&](const std::string& name, TArray<double>& values)
    {
        for (const auto& port : package.gameIoPorts)
        {
            if (port.name != name)
            {
                continue;
            }
            auto readRow = [&](const std::array<uint8, 16>& id)
            {
                auto word = [&](int32 offset)
                {
                    return (uint32(id[offset]) << 24) | (uint32(id[offset + 1]) << 16) |
                        (uint32(id[offset + 2]) << 8) | uint32(id[offset + 3]);
                };
                auto value = FVector4{};
                auto count = int32{};
                if (effect->ReadRootValue(FGuid(word(0), word(4), word(8), word(12)), value, count) != ELibraRuntimeResult::Ok)
                {
                    return false;
                }
                for (auto lane = 0; lane < count; ++lane)
                {
                    values.Add(value[lane]);
                }
                return true;
            };
            if (!port.runtimeNodeIds)
            {
                return readRow(port.nodeId);
            }
            for (const auto& row : *port.runtimeNodeIds)
            {
                if (!readRow(row))
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    };
    auto verifyPose = [&](const char* prefix, const USkeletalMeshComponent* pose)
    {
        const auto matrix = pose ? pose->GetComponentSpaceTransforms()[bone].ToMatrixWithScale() : FMatrix{};
        const auto local = pose ? matrix * pose->GetComponentSpaceTransforms()[parent].ToInverseMatrixWithScale() : FMatrix{};
        const auto worldMatrix = pose ? pose->GetBoneMatrix(bone) : FMatrix{};
        auto pass = true;
        auto checkSpace = [&](const char* space, const FMatrix& expected)
        {
            const auto name = std::string(prefix) + space;
            auto values = TArray<double>{}, valid = TArray<double>{};
            auto equal = read(name, values) && values.Num() == 16 &&
                read(name + "Valid", valid) && valid.Num() == 1 && valid[0] == (pose ? 1.0 : 0.0);
            for (auto row = 0; equal && row < 4; ++row)
            {
                for (auto col = 0; equal && col < 4; ++col)
                {
                    equal = FMath::IsNearlyEqual(values[row * 4 + col], pose ? expected.M[col][row] : 0.0, 0.001);
                }
            }
            pass &= TestTrue(UTF8_TO_TCHAR(name.c_str()), equal);
        };
        checkSpace("BoneLocal", local);
        checkSpace("Component", matrix);
        checkSpace("World", worldMatrix);
        return pass;
    };
    const auto firstPosition = ownedPose->GetBoneMatrix(bone).GetOrigin();
    if (!seek(*ownedPose, 0.5f))
    {
        AddError(TEXT("Could not evaluate the second marker frame."));
        return false;
    }
    const auto nextPosition = ownedPose->GetBoneMatrix(bone).GetOrigin();
    const auto motion = nextPosition - firstPosition;
    if (!TestTrue(TEXT("Selected animation samples visibly separate the marker"),
        motion.Size() > 5 && seek(*ownedPose, 0.1f)))
    {
        return false;
    }
    const auto motionAxis = motion.GetSafeNormal();
    const auto up = FMath::Abs(motionAxis.Z) < 0.9 ? FVector::UpVector : FVector::ForwardVector;
    const auto view = FVector::CrossProduct(motionAxis, up).GetSafeNormal();
    const auto center = (firstPosition + nextPosition) * 0.5;
    capture->SetActorLocationAndRotation(center - view * 400,
        FRotationMatrix::MakeFromXY(view, motion).Rotator());
    camera->OrthoWidth = FMath::Max(100.0, motion.Size() * 2.0);
    tick();
    if (!verifyPose("Owner", ownedPose) || !verifyPose("Registered", nullptr) ||
        !readMarker(TEXT("first"), firstPosition, true))
    {
        return false;
    }
    auto registration = FLibraSkeletalPoseRegistration{};
    if (!TestTrue(TEXT("Register the keyed target after play"),
        ULibraSkeletalPoseTargetComponent::RegisterTarget(TEXT("hero.skeleton"), registeredPose, registration)))
    {
        return false;
    }
    ON_SCOPE_EXIT { ULibraSkeletalPoseTargetComponent::UnregisterTarget(registration); };
    tick();
    if (!verifyPose("Registered", nullptr))
    {
        return false;
    }
    const auto previous = registeredPose->GetBoneMatrix(bone);
    if (!TestTrue(TEXT("Sample a second animation position"),
        seek(*registeredPose, 0.7f) && seek(*ownedPose, 0.5f)))
    {
        return false;
    }
    tick();
    if (!TestFalse(TEXT("The actual clip changed the selected bone"), previous.Equals(registeredPose->GetBoneMatrix(bone), 0.001)) ||
        !verifyPose("Owner", ownedPose) || !verifyPose("Registered", registeredPose) ||
        !readMarker(TEXT("moved"), nextPosition, true))
    {
        return false;
    }
    ULibraSkeletalPoseTargetComponent::UnregisterTarget(registration);
    if (!TestTrue(TEXT("Re-registering the key does not retarget the running effect"),
        ULibraSkeletalPoseTargetComponent::RegisterTarget(TEXT("hero.skeleton"), registeredPose, registration)))
    {
        return false;
    }
    ownedPose->SetSkeletalMesh(nullptr);
    tick();
    return verifyPose("Owner", nullptr) && verifyPose("Registered", nullptr) &&
        readMarker(TEXT("unavailable"), FVector::ZeroVector, false);
}
#endif
