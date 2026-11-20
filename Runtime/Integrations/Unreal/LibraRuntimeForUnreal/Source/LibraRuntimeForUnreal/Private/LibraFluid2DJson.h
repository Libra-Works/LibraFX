#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "liberafx/gpu/LbFluid2D.h"
#include <utility>

// Shared by package preflight and live scope creation. Required legacy values
// are read by the callers; this checks the envelope and optional model data.
static bool LibraReadFluidModelProperties(const TSharedPtr<FJsonObject>& object,
    liberafx::gpu::Fluid2DSettings& settings)
{
    static const TSet<FString> required{
        TEXT("version"), TEXT("width"), TEXT("height"), TEXT("domainWidth"), TEXT("domainHeight"),
        TEXT("pressureIterations"), TEXT("maximumStep"), TEXT("maximumSubsteps"),
        TEXT("velocityDissipation"), TEXT("densityDissipation"), TEXT("sourceEnabled"),
        TEXT("sourceX"), TEXT("sourceY"), TEXT("sourceRadius"), TEXT("sourceDensity"),
        TEXT("sourceVelocityX"), TEXT("sourceVelocityY"), TEXT("buoyancy"),
        TEXT("colorR"), TEXT("colorG"), TEXT("colorB"), TEXT("colorA")
    };
    static const TSet<FString> optional{
        TEXT("vorticity"), TEXT("model"), TEXT("gravity"), TEXT("initialWaterDepth"), TEXT("maximumWaterSpeed"),
        TEXT("fuelBurnRate"), TEXT("ignitionTemperature"), TEXT("heatYield"), TEXT("coolingRate"), TEXT("sourceTemperature"), TEXT("forces")
    };
    if (!object.IsValid())
    {
        return false;
    }
    for (const auto& key : required)
    {
        if (!object->HasField(key))
        {
            return false;
        }
    }
    for (const auto& entry : object->Values)
    {
#if defined(UE_JSONOBJECT_LEGACY_STRING_KEYS) && !UE_JSONOBJECT_LEGACY_STRING_KEYS
        const auto key = FString(entry.Key.ToView());
#else
        const auto& key = entry.Key;
#endif
        if (!required.Contains(key) && !optional.Contains(key))
        {
            return false;
        }
    }
    settings.model = 0;
    if (object->HasField(TEXT("model")))
    {
        FString model;
        if (!object->TryGetStringField(TEXT("model"), model))
        {
            return false;
        }
        if (model == TEXT("FreeSurface")) settings.model = 1;
        else if (model == TEXT("ShallowWater")) settings.model = 2;
        else if (model == TEXT("Combustion")) settings.model = 3;
        else if (model != TEXT("Smoke")) return false;
    }
    settings.gravity = 9.81f;
    settings.initialWaterDepth = .25f;
    settings.maximumWaterSpeed = 4;
    settings.fuelBurnRate = 4;
    settings.ignitionTemperature = .5f;
    settings.heatYield = 2;
    settings.coolingRate = 1;
    settings.sourceTemperature = 1;
    for (auto [key, target] : {std::pair{TEXT("gravity"), &settings.gravity},
        {TEXT("initialWaterDepth"), &settings.initialWaterDepth}, {TEXT("maximumWaterSpeed"), &settings.maximumWaterSpeed},
        {TEXT("fuelBurnRate"), &settings.fuelBurnRate}, {TEXT("ignitionTemperature"), &settings.ignitionTemperature},
        {TEXT("heatYield"), &settings.heatYield}, {TEXT("coolingRate"), &settings.coolingRate}, {TEXT("sourceTemperature"), &settings.sourceTemperature}})
    {
        if (object->HasField(key))
        {
            double value = 0;
            if (!object->TryGetNumberField(key, value) || !FMath::IsFinite(value) || !FMath::IsFinite(float(value)))
            {
                return false;
            }
            *target = float(value);
        }
    }
    settings.forceCount = 0;
    if (object->HasField(TEXT("forces")))
    {
        const TArray<TSharedPtr<FJsonValue>>* forces = nullptr;
        if (!object->TryGetArrayField(TEXT("forces"), forces) || forces->Num() > 8)
        {
            return false;
        }
        TSet<FGuid> ids;
        TArray<FGuid> orderedIds;
        for (auto index = 0; index < forces->Num(); ++index)
        {
            const auto force = (*forces)[index]->AsObject();
            FString identity, kind;
            FGuid id;
            if (!force.IsValid() || force->Values.Num() != 10 + (force->HasField(TEXT("inputA")) ? 1 : 0) + (force->HasField(TEXT("inputB")) ? 1 : 0) || !force->TryGetStringField(TEXT("id"), identity) ||
                !FGuid::Parse(identity, id) || !id.IsValid() || ids.Contains(id) || !force->TryGetStringField(TEXT("kind"), kind))
            {
                return false;
            }
            ids.Add(id);
            auto& node = settings.forces[index];
            if (kind == TEXT("Acceleration")) node.kind = 0;
            else if (kind == TEXT("Noise")) node.kind = 1;
            else if (kind == TEXT("CurlNoise")) node.kind = 2;
            else if (kind == TEXT("CellPosition")) node.kind = 3;
            else if (kind == TEXT("Time")) node.kind = 4;
            else if (kind == TEXT("Density")) node.kind = 5;
            else if (kind == TEXT("Temperature")) node.kind = 6;
            else if (kind == TEXT("Add")) node.kind = 7;
            else if (kind == TEXT("Multiply")) node.kind = 8;
            else if (kind == TEXT("Sine")) node.kind = 9;
            else if (kind == TEXT("Smoothstep")) node.kind = 10;
            else return false;
            for (auto [key, target] : {std::pair{TEXT("inputA"), &node.inputA}, {TEXT("inputB"), &node.inputB}})
            {
                if (!force->HasField(key))
                {
                    continue;
                }
                auto referenceText = FString{};
                auto reference = FGuid{};
                if (!force->TryGetStringField(key, referenceText) || !FGuid::Parse(referenceText, reference))
                {
                    return false;
                }
                const auto source = orderedIds.IndexOfByKey(reference);
                if (source == INDEX_NONE)
                {
                    return false;
                }
                *target = static_cast<uint32>(source + 1);
            }
            orderedIds.Add(id);
            for (auto [key, target] : {std::pair{TEXT("x"), &node.x}, {TEXT("y"), &node.y},
                {TEXT("strength"), &node.strength}, {TEXT("frequency"), &node.frequency},
                {TEXT("roughness"), &node.roughness}, {TEXT("animationSpeed"), &node.animationSpeed}})
            {
                double value = 0;
                if (!force->TryGetNumberField(key, value) || !FMath::IsFinite(value) || !FMath::IsFinite(float(value))) return false;
                *target = float(value);
            }
            double seed = -1, octaves = -1;
            if (!force->TryGetNumberField(TEXT("seed"), seed) || !force->TryGetNumberField(TEXT("octaves"), octaves) ||
                seed < 0 || seed > 16777215 || FMath::FloorToDouble(seed) != seed || octaves < 1 || octaves > 8 || FMath::FloorToDouble(octaves) != octaves)
            {
                return false;
            }
            node.seed = static_cast<uint32>(seed); node.octaves = static_cast<uint32>(octaves);
        }
        settings.forceCount = static_cast<uint32>(forces->Num());
    }
    return true;
}

static const TCHAR* LibraFluidMaterialFor(uint32 model)
{
    return model == 1 || model == 2 ? TEXT("builtin:fluid-water") : model == 3 ? TEXT("builtin:fluid-fire") : TEXT("builtin:fluid-alpha");
}

static bool LibraFluidMaterialMatches(const TSharedPtr<FJsonObject>& settings, const FString& material)
{
    auto parsed = liberafx::gpu::Fluid2DSettings{};
    return LibraReadFluidModelProperties(settings, parsed) && material == LibraFluidMaterialFor(parsed.model);
}
