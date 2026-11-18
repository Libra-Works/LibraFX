#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "liberafx/SceneBindings.h"

// Shared by the packaged gameplay renderer and the editor live preview.
struct FLibraSpriteCompositionRender
{
    TArray<liberafx::SpritePoseKey> Keys;
    double Duration = 0;
    int32 Order = 0;

    bool Read(const TSharedPtr<FJsonObject>& json, double fps, bool loop,
        const TArray<double>& durations, int32 start, int32 end, bool billboard)
    {
        const TArray<TSharedPtr<FJsonValue>>* keys = nullptr;
        auto order = double{};
        if (billboard || start != 0 || end != durations.Num() - 1 ||
            !json->TryGetNumberField(TEXT("durationFrames"), Duration) ||
            !json->TryGetNumberField(TEXT("drawOrder"), order) ||
            !FMath::IsFinite(order) || order < 0 || order >= 32 || FMath::FloorToDouble(order) != order ||
            !json->TryGetArrayField(TEXT("keys"), keys) || keys->Num() > 256)
            return false;
        Order = static_cast<int32>(order);
        auto ids = TSet<FGuid>{};
        Keys.Reserve(keys->Num());
        for (const auto& value : *keys)
        {
            const auto key = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
            auto identity = FString{};
            auto id = FGuid{};
            auto item = liberafx::SpritePoseKey{};
            if (!key || !key->TryGetStringField(TEXT("id"), identity) || !FGuid::Parse(identity, id) ||
                !id.IsValid() || ids.Contains(id) ||
                !key->TryGetNumberField(TEXT("frame"), item.frame) ||
                !key->TryGetNumberField(TEXT("x"), item.pose.x) ||
                !key->TryGetNumberField(TEXT("y"), item.pose.y) ||
                !key->TryGetNumberField(TEXT("rotationDegrees"), item.pose.rotationDegrees) ||
                !key->TryGetNumberField(TEXT("scaleX"), item.pose.scaleX) ||
                !key->TryGetNumberField(TEXT("scaleY"), item.pose.scaleY))
                return false;
            ids.Add(id);
            Keys.Add(item);
        }
        auto frame = uint32{};
        auto pose = liberafx::SpritePose{};
        return Sample(0, fps, loop, durations, frame, pose);
    }

    bool Sample(double time, double fps, bool loop, const TArray<double>& durations,
        uint32& frame, liberafx::SpritePose& pose) const
    {
        return liberafx::sampleSpriteComposition(time, fps, Duration, loop, durations.GetData(),
            static_cast<uint32>(durations.Num()), Keys.GetData(), static_cast<uint32>(Keys.Num()), frame, pose);
    }

    static FTransform Transform(const liberafx::SpritePose& pose, int32 width, int32 height)
    {
        // /Engine/BasicShapes/Plane spans 100 units on both axes.
        return FTransform(FRotator(0, pose.rotationDegrees, 0), FVector(pose.x, pose.y, 0),
            FVector(pose.scaleX * width / 100, pose.scaleY * height / 100, 1));
    }
};
