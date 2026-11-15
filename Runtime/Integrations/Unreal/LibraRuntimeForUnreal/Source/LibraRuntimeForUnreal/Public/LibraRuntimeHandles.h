#pragma once

#include "CoreMinimal.h"
#include "liberafx/VfxRuntime.h"
#include "LibraRuntimeHandles.generated.h"

class ULibraEffectComponent;

USTRUCT(BlueprintType)
struct LIBRARUNTIMEFORUNREAL_API FLibraValueHandle
{
    GENERATED_BODY()
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Transient, Category = "Libra Runtime")
    FGuid NodeId;

private:
    friend class ULibraEffectComponent;
    TWeakObjectPtr<UObject> owner;
    liberafx::ValueHandle native;
};

USTRUCT(BlueprintType)
struct LIBRARUNTIMEFORUNREAL_API FLibraParameterHandle
{
    GENERATED_BODY()
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Transient, Category = "Libra Runtime")
    FGuid NodeId;

private:
    friend class ULibraEffectComponent;
    TWeakObjectPtr<UObject> owner;
    liberafx::ParameterHandle native;
};

USTRUCT(BlueprintType)
struct LIBRARUNTIMEFORUNREAL_API FLibraSignalHandle
{
    GENERATED_BODY()
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Transient, Category = "Libra Runtime")
    FGuid NodeId;

private:
    friend class ULibraEffectComponent;
    TWeakObjectPtr<UObject> owner;
    liberafx::SignalHandle native;
};
