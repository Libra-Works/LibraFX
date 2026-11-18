#pragma once

#include "CoreMinimal.h"

// Local, same-user discovery only. Invitations still use normal pinned TLS.
class FLibraViewerEndpoint final
{
public:
    FLibraViewerEndpoint();
    ~FLibraViewerEndpoint();
    void Start(const FString& name);
    void Stop();
    void SetConnected(bool connected);
    bool TakeInvitation(FString& invitation);
private:
    struct FState;
    TUniquePtr<FState> state;
};
