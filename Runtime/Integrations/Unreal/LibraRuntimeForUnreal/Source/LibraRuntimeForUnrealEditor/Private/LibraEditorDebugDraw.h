#pragma once
#include "LibraDebugDrawScope.h"
#include "LibraEffectComponent.h"
#include "Engine/StaticMesh.h"
#include "Misc/PackageName.h"

class FLibraEditorDebugDraw final
{
public:
    bool Configure(const FJsonObject& root, AActor& actor, ULibraEffectComponent& effect,
        int64 sequence, FString& error)
    {
        auto weakEffect = TWeakObjectPtr<ULibraEffectComponent>(&effect);
        return scope.Configure(root, actor, [weakEffect](FGuid id, FVector4& value, int32& width)
        {
            return weakEffect.IsValid() ? weakEffect->ReadRootValue(id, value, width) : ELibraRuntimeResult::NotReady;
        }, [](const FString& path) -> UStaticMesh*
        {
            return FPackageName::IsValidObjectPath(path) ? LoadObject<UStaticMesh>(nullptr, *path) : nullptr;
        }, sequence, error);
    }
    bool Update(ULibraEffectComponent&, int64 sequence, FString& error) { return scope.Update(sequence, error); }
    void Clear() { scope.Clear(); }
private:
    FLibraDebugDrawScope scope;
};
