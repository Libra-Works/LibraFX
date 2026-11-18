#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "LibraSkeletalPoseTargetComponent.generated.h"

class USkeletalMeshComponent;
class USkeletalMesh;
class AActor;

struct LIBRARUNTIMEFORUNREAL_API FLibraSkeletalPoseRegistration
{
    FString Key;
    TWeakObjectPtr<USkeletalMeshComponent> Mesh;
    TWeakObjectPtr<ULibraSkeletalPoseTargetComponent> Provider;
    uint64 RegistryGeneration = 0;
    uint64 TargetGeneration = 0;
    TWeakObjectPtr<USkeletalMesh> MeshAsset;
    bool bHasProvider = false;
};

UCLASS(ClassGroup = (Libra), meta = (BlueprintSpawnableComponent, DisplayName = "Libra Skeletal Pose Target"))
class LIBRARUNTIMEFORUNREAL_API ULibraSkeletalPoseTargetComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    ULibraSkeletalPoseTargetComponent();

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra FX|SOURCE")
    TObjectPtr<USkeletalMeshComponent> SkeletalMesh;

    // Empty disables registration. This is an explicit key, not an actor or bone name lookup.
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra FX|SOURCE")
    FString RegistrationKey;

    virtual void OnRegister() override;
    virtual void OnUnregister() override;

    static bool RegisterTarget(const FString& key, USkeletalMeshComponent* mesh,
        FLibraSkeletalPoseRegistration& outRegistration,
        ULibraSkeletalPoseTargetComponent* provider = nullptr);
    static void UnregisterTarget(const FLibraSkeletalPoseRegistration& registration);
    static bool ResolveOwnerTarget(AActor* owner, FLibraSkeletalPoseRegistration& outRegistration);
    static bool ResolveKeyTarget(const FString& key, FLibraSkeletalPoseRegistration& outRegistration);
    static bool IsCurrent(const FLibraSkeletalPoseRegistration& registration);
    static uint64 TargetGeneration(USkeletalMeshComponent* mesh);
    static void InvalidateTarget(USkeletalMeshComponent* mesh);
    bool GetRegistration(FLibraSkeletalPoseRegistration& outRegistration) const;

private:
    FLibraSkeletalPoseRegistration Registration;
    bool bHasRegistration = false;
};
