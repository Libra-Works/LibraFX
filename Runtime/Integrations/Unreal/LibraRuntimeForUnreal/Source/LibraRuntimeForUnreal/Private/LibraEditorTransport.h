#pragma once

#include "CoreMinimal.h"
#include "Templates/UniquePtr.h"

struct FLibraEditorTransportSettings
{
	FString Host = TEXT("localhost");
	uint16 Port = 8766;
	FString AccessToken;
	FString CertificateFingerprint;
};

class FLibraEditorTransport
{
public:
	FLibraEditorTransport();
	~FLibraEditorTransport();

	FLibraEditorTransport(const FLibraEditorTransport&) = delete;
	FLibraEditorTransport& operator=(const FLibraEditorTransport&) = delete;

	bool Start(const FLibraEditorTransportSettings& Settings, FString& OutError);
	void Stop();
	bool IsConnected() const;
	bool TryReceive(TArray<uint8>& OutMessage);
	void RequestResync();
	bool QueueText(const FString& Message);
	FString GetStatus() const;

private:
	struct FImpl;
	TUniquePtr<FImpl> Impl;
};
