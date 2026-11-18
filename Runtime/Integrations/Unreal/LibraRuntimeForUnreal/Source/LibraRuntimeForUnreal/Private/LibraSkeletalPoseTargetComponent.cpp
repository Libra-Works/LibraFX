#include "LibraSkeletalPoseTargetComponent.h"

#include "Components/SkeletalMeshComponent.h"
#include "Engine/SkeletalMesh.h"

namespace {
struct FLibraPoseRegistryEntry
{
    TWeakObjectPtr<USkeletalMeshComponent> Mesh;
    TWeakObjectPtr<ULibraSkeletalPoseTargetComponent> Provider;
    uint64 Generation = 0;
    bool bHasProvider = false;
};

struct FLibraMeshRevision
{
    TWeakObjectPtr<USkeletalMesh> Asset;
    uint64 Generation = 0;
};

TMap<FString, FLibraPoseRegistryEntry> GRegisteredPoseTargets;
TMap<TWeakObjectPtr<USkeletalMeshComponent>, FLibraMeshRevision> GMeshRevisions;
uint64 GNextPoseGeneration = 0;

auto NextGeneration() -> uint64
{
    check(IsInGameThread());
    checkf(GNextPoseGeneration != MAX_uint64, TEXT("Libra SOURCE generation space exhausted."));
    return ++GNextPoseGeneration;
}

auto IsUsableMesh(USkeletalMeshComponent* mesh) -> bool
{
    return IsValid(mesh) && IsValid(mesh->GetSkeletalMeshAsset());
}

auto HasSkeletalPoseControlCharacter(const FString& value) -> bool
{
    for (const auto character : value)
        if (FChar::IsControl(character)) return true;
    return false;
}
}

ULibraSkeletalPoseTargetComponent::ULibraSkeletalPoseTargetComponent()
{
    PrimaryComponentTick.bCanEverTick = false;
}

void ULibraSkeletalPoseTargetComponent::OnRegister()
{
    Super::OnRegister();
    check(IsInGameThread());
    bHasRegistration = false;
    if (!RegistrationKey.IsEmpty() &&
        RegisterTarget(RegistrationKey, SkeletalMesh, Registration, this))
        bHasRegistration = true;
}

void ULibraSkeletalPoseTargetComponent::OnUnregister()
{
    check(IsInGameThread());
    InvalidateTarget(SkeletalMesh);
    if (bHasRegistration)
    {
        UnregisterTarget(Registration);
        bHasRegistration = false;
    }
    Super::OnUnregister();
}

bool ULibraSkeletalPoseTargetComponent::RegisterTarget(const FString& key,
    USkeletalMeshComponent* mesh, FLibraSkeletalPoseRegistration& outRegistration,
    ULibraSkeletalPoseTargetComponent* provider)
{
    check(IsInGameThread());
    outRegistration = {};
    const auto hasControl = HasSkeletalPoseControlCharacter(key);
    if (key.IsEmpty() || key.TrimStartAndEnd() != key || hasControl || !IsUsableMesh(mesh))
        return false;

    FLibraSkeletalPoseRegistration registration;
    registration.Key = key;
    registration.Mesh = mesh;
    registration.Provider = provider;
    registration.bHasProvider = IsValid(provider);
    registration.RegistryGeneration = NextGeneration();
    registration.TargetGeneration = TargetGeneration(mesh);
    registration.MeshAsset = mesh->GetSkeletalMeshAsset();
    GRegisteredPoseTargets.Add(key, { mesh, provider, registration.RegistryGeneration, registration.bHasProvider });
    outRegistration = MoveTemp(registration);
    return true;
}

void ULibraSkeletalPoseTargetComponent::UnregisterTarget(
    const FLibraSkeletalPoseRegistration& registration)
{
    check(IsInGameThread());
    if (registration.Key.IsEmpty()) return;
    if (auto* current = GRegisteredPoseTargets.Find(registration.Key);
        current && current->Generation == registration.RegistryGeneration &&
        current->Mesh == registration.Mesh)
        GRegisteredPoseTargets.Remove(registration.Key);
}

bool ULibraSkeletalPoseTargetComponent::ResolveOwnerTarget(AActor* owner,
    FLibraSkeletalPoseRegistration& outRegistration)
{
    check(IsInGameThread());
    outRegistration = {};
    if (!IsValid(owner)) return false;
    TArray<ULibraSkeletalPoseTargetComponent*> targets;
    owner->GetComponents<ULibraSkeletalPoseTargetComponent>(targets);
    if (targets.Num() != 1 || !IsValid(targets[0])) return false;
    if (!targets[0]->GetRegistration(outRegistration)) return false;
    outRegistration.Key.Reset();
    outRegistration.RegistryGeneration = 0;
    return true;
}

bool ULibraSkeletalPoseTargetComponent::ResolveKeyTarget(const FString& key,
    FLibraSkeletalPoseRegistration& outRegistration)
{
    check(IsInGameThread());
    outRegistration = {};
    const auto* entry = GRegisteredPoseTargets.Find(key);
    if (!entry || !entry->Mesh.IsValid()) return false;
    auto* mesh = entry->Mesh.Get();
    outRegistration.Key = key;
    outRegistration.Mesh = mesh;
    outRegistration.RegistryGeneration = entry->Generation;
    outRegistration.Provider = entry->Provider;
    outRegistration.bHasProvider = entry->bHasProvider;
    outRegistration.TargetGeneration = TargetGeneration(mesh);
    outRegistration.MeshAsset = mesh->GetSkeletalMeshAsset();
    return IsCurrent(outRegistration);
}

bool ULibraSkeletalPoseTargetComponent::IsCurrent(
    const FLibraSkeletalPoseRegistration& registration)
{
    check(IsInGameThread());
    auto* mesh = registration.Mesh.Get();
    if (!IsUsableMesh(mesh) || mesh->GetSkeletalMeshAsset() != registration.MeshAsset.Get() ||
        TargetGeneration(mesh) != registration.TargetGeneration)
        return false;
    if (registration.bHasProvider)
    {
        auto* provider = registration.Provider.Get();
        if (!IsValid(provider) || provider->SkeletalMesh != mesh) return false;
    }
    if (!registration.Key.IsEmpty())
    {
        const auto* entry = GRegisteredPoseTargets.Find(registration.Key);
        if (!entry || entry->Generation != registration.RegistryGeneration || entry->Mesh != mesh)
            return false;
    }
    return true;
}

uint64 ULibraSkeletalPoseTargetComponent::TargetGeneration(USkeletalMeshComponent* mesh)
{
    check(IsInGameThread());
    if (!IsUsableMesh(mesh)) return 0;
    const TWeakObjectPtr<USkeletalMeshComponent> key(mesh);
    const TWeakObjectPtr<USkeletalMesh> asset(mesh->GetSkeletalMeshAsset());
    auto* revision = GMeshRevisions.Find(key);
    if (!revision)
    {
        GMeshRevisions.Add(key, { asset, NextGeneration() });
        revision = GMeshRevisions.Find(key);
    }
    else if (revision->Asset != asset)
    {
        revision->Asset = asset;
        revision->Generation = NextGeneration();
    }
    return revision->Generation;
}

void ULibraSkeletalPoseTargetComponent::InvalidateTarget(USkeletalMeshComponent* mesh)
{
    check(IsInGameThread());
    if (!IsUsableMesh(mesh)) return;
    const TWeakObjectPtr<USkeletalMeshComponent> key(mesh);
    if (auto* revision = GMeshRevisions.Find(key))
    {
        revision->Asset = mesh->GetSkeletalMeshAsset();
        revision->Generation = NextGeneration();
    }
}

bool ULibraSkeletalPoseTargetComponent::GetRegistration(
    FLibraSkeletalPoseRegistration& outRegistration) const
{
    check(IsInGameThread());
    outRegistration = {};
    if (!IsUsableMesh(SkeletalMesh)) return false;
    if (bHasRegistration && IsCurrent(Registration))
    {
        outRegistration = Registration;
        return true;
    }
    outRegistration.Mesh = SkeletalMesh;
    outRegistration.Provider = const_cast<ULibraSkeletalPoseTargetComponent*>(this);
    outRegistration.bHasProvider = true;
    outRegistration.TargetGeneration = TargetGeneration(SkeletalMesh);
    outRegistration.MeshAsset = SkeletalMesh->GetSkeletalMeshAsset();
    return IsCurrent(outRegistration);
}
