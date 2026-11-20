#include "LibraProceduralPreview.h"
#include "LibraProceduralScope.h"
#include "LibraSpawnableComponent.h"
#include "LibraEffectComponent.h"
#include "Components/SceneComponent.h"
#include "GameFramework/Actor.h"
#include "UObject/StrongObjectPtr.h"

struct FLibraProceduralPreview::FImpl
{
    TStrongObjectPtr<ULibraSpawnableComponent> Owner;
    TUniquePtr<FLibraProceduralScope> Scope;
};
FLibraProceduralPreview::FLibraProceduralPreview() : Impl(MakeUnique<FImpl>()) {}
FLibraProceduralPreview::~FLibraProceduralPreview() { Clear(); }
bool FLibraProceduralPreview::Configure(AActor& actor, const TSharedPtr<FJsonObject>& scene, FString& error)
{
    auto definition = TSharedPtr<FLibraProceduralDefinition>{};
    if (!LibraProcedural::Prepare(scene, definition, error))
    {
        return false;
    }
    if (LibraProcedural::DrawCount(definition) == 0)
    {
        Clear();
        return true;
    }
    if (!Impl->Owner.IsValid())
    {
        Impl->Owner.Reset(NewObject<ULibraSpawnableComponent>(&actor));
        Impl->Owner->bAutoPlay = false;
        Impl->Owner->RegisterComponent();
    }
    auto scope = MakeUnique<FLibraProceduralScope>();
    if (!scope->Create(*Impl->Owner.Get(), definition, error))
    {
        return false;
    }
    Impl->Scope = MoveTemp(scope);
    return true;
}
bool FLibraProceduralPreview::Update(ULibraEffectComponent& effect, double time, FString& error)
{
    if (!Impl->Scope || !Impl->Owner.IsValid())
    {
        return true;
    }
    auto* actor = Impl->Owner->GetOwner();
    if (!actor)
    {
        error = TEXT("Procedural preview owner is unavailable.");
        return false;
    }
    auto* root = actor->GetRootComponent();
    return Impl->Scope->Update([&effect](const liberafx::flexible_adapter::Id& id, double* output, uint8& width) -> LibraResult
        {
            uint32 words[4]{};
            for (auto i = 0u; i < 16; ++i)
            {
                words[i / 4] |= static_cast<uint32>(id[i]) << (24 - i % 4 * 8);
            }
            auto value = FVector4{}; auto count = int32{};
            const auto result = effect.ReadRootValue(FGuid(words[0], words[1], words[2], words[3]), value, count);
            if (result != ELibraRuntimeResult::Ok || count < 1 || count > 4)
            {
                return result == ELibraRuntimeResult::Ok ? LIBRA_INVALID_VALUE_TYPE : static_cast<LibraResult>(result);
            }
            width = static_cast<uint8>(count);
            for (auto i = 0; i < count; ++i)
            {
                output[i] = value[i];
            }
            return LIBRA_OK;
        }, time, root ? root->GetComponentTransform() : actor->GetActorTransform(), !actor->IsHidden(), error);
}
void FLibraProceduralPreview::Clear()
{
    Impl->Scope.Reset();
    if (Impl->Owner.IsValid())
    {
        Impl->Owner->DestroyComponent(); Impl->Owner.Reset();
    }
}
