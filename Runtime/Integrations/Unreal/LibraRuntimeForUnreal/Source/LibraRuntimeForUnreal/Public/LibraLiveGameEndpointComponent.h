#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "LibraLiveGameSession.h"
#include "LibraLiveGameEndpointComponent.generated.h"

// Separate explicitly isolated, local-only development transport. Not preview
// TLS, no listener/remote discovery/credentials; provision a private spool root.
UCLASS(ClassGroup = (Effects), meta = (BlueprintSpawnableComponent))
class LIBRARUNTIMEFORUNREAL_API ULibraLiveGameEndpointComponent : public UActorComponent
{
    GENERATED_BODY()
public:
    ULibraLiveGameEndpointComponent();
    virtual ~ULibraLiveGameEndpointComponent() override;
    UPROPERTY(EditAnywhere, Category = "Libra Live Development")
    bool bDevelopmentOptIn = false;
    UPROPERTY(EditAnywhere, Category = "Libra Live Development")
    FString IsolatedSessionRoot;
    UPROPERTY(EditAnywhere, Category = "Libra Live Development")
    TArray<FLibraSpawnableAssetBinding> AssetBindings;
    UPROPERTY(VisibleAnywhere, Category = "Libra Live Development")
    FString EndpointDirectory;
    UPROPERTY(VisibleAnywhere, Category = "Libra Live Development")
    FString LastError;
    FLibraLiveGameSession* GetSession() const { return session.Get(); }
    // Test/explicit host entry points; all game-thread only.
    bool StartEndpoint();
    void PumpEndpoint();
    void StopEndpoint();
    virtual void TickComponent(float deltaTime, ELevelTick tickType, FActorComponentTickFunction* tickFunction) override;
protected:
    virtual void BeginPlay() override;
    virtual void EndPlay(const EEndPlayReason::Type reason) override;
    virtual void OnComponentDestroyed(bool destroyingHierarchy) override;
private:
    void Publish(const FString& name, const FString& text);
    void Reply(const FString& sequence, liberafx::Result result, const FString& error, const FString& target = {});
    TUniquePtr<FLibraLiveGameSession> session;
    FString identity, waitingSequence;
    liberafx::RequestId waitingRequest{};
    uint64 lastSequence = 0;
    bool hasWaiting = false;
};
