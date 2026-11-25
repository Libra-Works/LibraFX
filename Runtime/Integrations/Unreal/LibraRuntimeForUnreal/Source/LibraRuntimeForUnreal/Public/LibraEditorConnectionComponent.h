#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "Templates/Function.h"
#include "LibraRuntimeTypes.h"
#include "LibraEditorConnectionComponent.generated.h"

class ULibraEffectComponent;
class ULibraSpawnableAsset;
struct FLibraEditorConnectionState;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FOnLibraPreviewUpdated,
    ELibraRuntimeResult, Result, int64, Sequence);

UCLASS(ClassGroup = (Effects), meta = (BlueprintSpawnableComponent, DisplayName = "Libra Editor Connection"))
class LIBRARUNTIMEFORUNREAL_API ULibraEditorConnectionComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    ULibraEditorConnectionComponent();
    virtual ~ULibraEditorConnectionComponent() override;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Editor")
    TObjectPtr<ULibraEffectComponent> Target;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Editor")
    FString Host = TEXT("127.0.0.1");

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Editor", meta = (ClampMin = "1", ClampMax = "65535"))
    int32 Port = 8766;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Transient, Category = "Libra Editor", meta = (PasswordField = true))
    FString AccessToken;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Transient, Category = "Libra Editor")
    FString CertificateFingerprint;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Editor")
    bool bAutoConnect = false;

    UPROPERTY(BlueprintReadOnly, Transient, Category = "Libra Editor")
    bool bIsConnected = false;

    UPROPERTY(BlueprintReadOnly, Transient, Category = "Libra Editor")
    bool bEditorPlaying = false;

    UPROPERTY(BlueprintReadOnly, Transient, Category = "Libra Editor")
    int64 LastAppliedSequence = 0;

    UPROPERTY(BlueprintReadOnly, Transient, Category = "Libra Editor")
    ELibraRuntimeResult LastResult = ELibraRuntimeResult::NotReady;

    UPROPERTY(BlueprintReadOnly, Transient, Category = "Libra Editor")
    FString Status;

    TFunction<FString(const FString&)> EditorAssetRequestHandler;

    bool QueueEditorMessage(const FString& message);

    UPROPERTY(BlueprintAssignable, Category = "Libra Editor")
    FOnLibraPreviewUpdated OnPreviewUpdated;

    // Rebind cached value/parameter/signal handles here after a new program is installed.
    UPROPERTY(BlueprintAssignable, Category = "Libra Editor")
    FOnLibraPreviewUpdated OnPreviewProgramChanged;

    UFUNCTION(BlueprintCallable, Category = "Libra Editor")
    bool ConnectToEditor();

    UFUNCTION(BlueprintCallable, Category = "Libra Editor")
    void DisconnectFromEditor();

    UFUNCTION(BlueprintCallable, Category = "Libra Editor")
    void RequestSnapshot();

    virtual void TickComponent(float deltaTime, ELevelTick tickType, FActorComponentTickFunction* tickFunction) override;
    virtual void BeginDestroy() override;
    virtual void OnUnregister() override;

protected:
    virtual void BeginPlay() override;
    virtual void EndPlay(const EEndPlayReason::Type endPlayReason) override;

private:
    bool ApplyMessage(TConstArrayView<uint8> message);
    void CloseConnection(bool restoreEffect);
    TSharedPtr<FLibraEditorConnectionState, ESPMode::NotThreadSafe> State;
    FGuid ViewerId = FGuid::NewGuid();

    UPROPERTY(Transient)
    TObjectPtr<ULibraSpawnableAsset> PreviewPackage;

    UPROPERTY(Transient)
    TObjectPtr<ULibraSpawnableAsset> OriginalPackage;
};
