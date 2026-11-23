#pragma once
#include "CoreMinimal.h"
#include "liberafx/host/LbProceduralAdapter.h"

class FJsonObject;
class UTexture;
class ULibraSpawnableComponent;
struct FLibraProceduralDefinition;
namespace LibraProcedural
{
bool Prepare(const TSharedPtr<FJsonObject>& config, TSharedPtr<FLibraProceduralDefinition>& output, FString& error);
uint64 Bytes(const TSharedPtr<FLibraProceduralDefinition>& definition);
int32 DrawCount(const TSharedPtr<FLibraProceduralDefinition>& definition);
}
class FLibraProceduralScope final
{
public:
    FLibraProceduralScope();
    ~FLibraProceduralScope();
    bool Create(ULibraSpawnableComponent& owner, const TSharedPtr<FLibraProceduralDefinition>& definition, FString& error);
    bool Update(const liberafx::procedural_adapter::Read& read, double time,
        const FTransform& ownerTransform, bool visible, FString& error);
    void SetPresentation(const FTransform& transform, bool visible);
    int32 DrawCount() const;
    UTexture* GetResourceTexture(const FString& resourceId) const;
private:
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};
