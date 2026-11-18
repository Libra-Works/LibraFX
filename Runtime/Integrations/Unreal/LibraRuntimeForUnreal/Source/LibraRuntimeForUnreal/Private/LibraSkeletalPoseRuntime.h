#pragma once

#include "LibraSkeletalPoseTargetComponent.h"

#include <memory>
#include <string>

namespace liberafx { class SpawnableHost; }
class AActor;
class UActorComponent;

struct FLibraSkeletalPoseSource
{
    FGuid SourceNodeId;
    FString Key;
    FString BonePath;
    FString MatrixPort;
    FString ValidityPort;
    uint8 Target = 0;
    uint8 Space = 0;
    FLibraSkeletalPoseRegistration Registration;
    bool bBound = false;
    bool bInvalid = false;
};

class FLibraSkeletalPoseRuntime final
{
public:
    bool Initialize(const liberafx::SpawnableHost& host, AActor* owner,
        TArray<UActorComponent*>& tickPrerequisites);
    auto Publish(liberafx::SpawnableHost& host, AActor* owner) -> liberafx::Result;
    void AddTickPrerequisites(UActorComponent& component) const;
    void RemoveTickPrerequisites(UActorComponent& component) const;
    bool HasSources() const { return Sources.Num() != 0; }

private:
    static bool TryParse(const std::string& name, FLibraSkeletalPoseSource& source,
        bool& isValidity);
    bool TryCapture(FLibraSkeletalPoseSource& source, AActor* owner,
        std::array<double, 16>& matrix);
    TArray<FLibraSkeletalPoseSource> Sources;
    TArray<TWeakObjectPtr<UActorComponent>> TickPrerequisites;
};
