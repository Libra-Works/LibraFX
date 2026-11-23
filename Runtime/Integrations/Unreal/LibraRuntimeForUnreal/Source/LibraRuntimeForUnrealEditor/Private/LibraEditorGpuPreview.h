#pragma once

#include "CoreMinimal.h"
#include "UObject/StrongObjectPtr.h"

class AActor;
class ULibraGpuEffectAsset;
class ULibraGpuEffectRuntime;

class FLibraEditorGpuPreview final
{
public:
    explicit FLibraEditorGpuPreview(AActor* actor);
    ~FLibraEditorGpuPreview();
    bool Configure(const FString& json, int64 programSequence);
    void SetPlayback(int64 programSequence, bool playing, double timeSeconds, bool programChanged, double rate = 1);
    void Tick(float deltaTime, bool connected);
    void Reset();
    FString Status(int64 programSequence) const;

private:
    friend class FLibraGpuPreviewTransactionTest;
    struct FEmitter
    {
        FGuid id;
        TStrongObjectPtr<ULibraGpuEffectAsset> asset;
        TStrongObjectPtr<ULibraGpuEffectRuntime> runtime;
        TMap<FString, FString> resourcePaths;
        FString rendererIdentity;
        double timeSeconds = 0;
        bool executed = false;
    };
    TWeakObjectPtr<AActor> owner;
    TArray<FEmitter> emitters;
    TArray<FString> errors;
    FString configurationError;
    int64 rootSequence = 0;
    int64 appliedSequence = -1;
    double lastRemoteTime = 0;
    double lastRemoteArrival = 0;
    double playbackRate = 1;
    double currentTimeSeconds = 0;
    double seekTargetSeconds = 0;
    bool hasRemoteTime = false;
    bool remotePlaying = false;
    bool seekBlocked = false;
    bool seeking = false;
};
