#pragma once

#include "CoreMinimal.h"
#include "LibraRuntimeTypes.h"
#include "UObject/StrongObjectPtr.h"
#include "liberafx/platform/LbDebugDrawQueue.h"

class AActor;
class UStaticMesh;
class USceneComponent;
class FJsonObject;

// Preview-only owner. Never installed on an exported effect or engine worker.
class LIBRARUNTIMEFORUNREAL_API FLibraDebugDrawScope final
{
public:
    using FReadValue = TFunction<ELibraRuntimeResult(FGuid, FVector4&, int32&)>;
    using FResolveModel = TFunction<UStaticMesh*(const FString&)>;
    ~FLibraDebugDrawScope();
    bool Configure(const FJsonObject& root, AActor& actor, FReadValue read,
        FResolveModel resolve, int64 sequence, FString& error);
    bool Update(int64 sequence, FString& error);
    void Clear();
    int32 Count() const { return entries.Num(); }
private:
    struct FInput
    {
        FString id;
        FVector4 value = FVector4(0, 0, 0, 0);
        FGuid source;
        int32 width = 0, component = 0;
    };
    struct FEntry
    {
        liberafx::DebugDrawCommand command;
        TArray<FInput> inputs;
        TStrongObjectPtr<USceneComponent> component;
    };
    TArray<FEntry> entries;
    TArray<liberafx::DebugDrawCommand> storage;
    liberafx::DebugDrawQueue queue;
    int64 programSequence = -1;
    FReadValue readValue;
};
