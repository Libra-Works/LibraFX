#pragma once
#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "liberafx/LbParticleSprite.h"

namespace LibraParticleSpriteData
{
inline bool Read(const TSharedPtr<FJsonObject> &parent, bool &present, liberafx::ParticleSpriteAnimation &out)
{
    const auto value = parent->TryGetField(TEXT("spriteAnimation"));
    present = value.IsValid() && value->Type != EJson::Null;
    if (!present)
    {
        return true;
    }
    if (value->Type != EJson::Object)
    {
        return false;
    }
    const auto object = value->AsObject();
    double version = 0, parts = 0;
    const TArray<TSharedPtr<FJsonValue>> *array = nullptr;
    if (!object->TryGetNumberField(TEXT("version"), version) || version != 1 ||
        !object->TryGetNumberField(TEXT("partCount"), parts) || !FMath::IsFinite(parts) || parts < 0 || parts > 32 ||
        FMath::FloorToDouble(parts) != parts || !object->TryGetArrayField(TEXT("values"), array) ||
        array->Num() > 1'200'000)
    {
        return false;
    }
    auto values = TArray<double>{};
    values.SetNumUninitialized(array->Num());
    for (auto index = 0; index < array->Num(); ++index)
    {
        if (!(*array)[index].IsValid() || !(*array)[index]->TryGetNumber(values[index]))
        {
            return false;
        }
    }
    return out.configure(values.GetData(), values.Num(), static_cast<std::size_t>(parts)) == liberafx::Result::Ok;
}
} // namespace LibraParticleSpriteData
