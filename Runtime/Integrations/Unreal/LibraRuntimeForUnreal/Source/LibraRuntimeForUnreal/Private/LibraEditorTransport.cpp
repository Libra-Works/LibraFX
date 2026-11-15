#include "LibraEditorTransport.h"

#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#if WITH_DEV_AUTOMATION_TESTS && LIBRA_WITH_EDITOR_TRANSPORT && WITH_SSL
#include "Misc/AutomationTest.h"
#endif

#if LIBRA_WITH_EDITOR_TRANSPORT && WITH_SSL
#include "SocketSubsystem.h"
#pragma push_macro("UI")
#pragma push_macro("min")
#pragma push_macro("max")
#undef UI
// OpenSSL's UI typedef conflicts with UE's UI namespace.
#define UI LIBRA_OPENSSL_UI
THIRD_PARTY_INCLUDES_START
#include <libwebsockets.h>
#include <openssl/crypto.h>
#include <openssl/ec.h>
#include <openssl/evp.h>
#include <openssl/ssl.h>
#include <openssl/x509.h>
#include <openssl/sha.h>
THIRD_PARTY_INCLUDES_END
#pragma pop_macro("max")
#pragma pop_macro("min")
#pragma pop_macro("UI")
#endif

#if LIBRA_WITH_EDITOR_TRANSPORT && WITH_SSL
namespace
{
	bool IsCertificatePinMatch(X509* Certificate, const std::array<uint8, 32>& ExpectedFingerprint)
	{
		if (Certificate == nullptr)
		{
			return false;
		}

		const int32 DerSize = i2d_X509(Certificate, nullptr);
		if (DerSize <= 0)
		{
			return false;
		}

		std::vector<uint8_t> Der(static_cast<size_t>(DerSize));
		unsigned char* DerCursor = Der.data();
		if (i2d_X509(Certificate, &DerCursor) != DerSize)
		{
			return false;
		}

		std::array<uint8, 32> ActualFingerprint{};
		if (SHA256(Der.data(), Der.size(), ActualFingerprint.data()) == nullptr)
		{
			return false;
		}
		return CRYPTO_memcmp(ActualFingerprint.data(), ExpectedFingerprint.data(), ActualFingerprint.size()) == 0;
	}
}
#endif

struct FLibraEditorTransport::FImpl
{
	static constexpr size_t FingerprintBytes = 32;
	static constexpr size_t MaxMessageBytes = 128ull * 1024ull * 1024ull + 48ull;
	static constexpr size_t MaxQueuedMessages = 8;
	static constexpr size_t MaxQueuedBytes = 256ull * 1024ull * 1024ull;
	static constexpr unsigned int DnsTimeoutSeconds = 5;
	static constexpr unsigned int ConnectTimeoutSeconds = 10;

	mutable std::mutex Mutex;
	std::condition_variable StateChanged;
	std::deque<TArray<uint8>> ReceiveQueue;
	std::deque<TArray<uint8>> SendQueue;
	size_t SendBytes = 0;
	size_t QueuedBytes = 0;
	FString Status = TEXT("Stopped");
	std::atomic<bool> StopRequested{false};
	std::atomic<bool> Failed{false};
	std::atomic<bool> Connected{false};
	std::atomic<bool> PinConfirmed{false};
	std::atomic<bool> AuthHeaderAdded{false};
	std::atomic<bool> ResyncRequested{false};
	std::atomic<bool> CloseSent{false};
	std::mutex ContextMutex;
	std::thread Worker;

	FString Host;
	uint16 Port = 8766;
	FString AccessToken;
	std::array<uint8, FingerprintBytes> ExpectedFingerprint{};
	TArray<uint8> CurrentMessage;
	bool CurrentMessageIsBinary = false;

#if LIBRA_WITH_EDITOR_TRANSPORT && WITH_SSL
	struct FDnsQuery
	{
		std::mutex Mutex;
		std::condition_variable Changed;
		bool Complete = false;
		FString Address;
	};
	ISocketSubsystem* SocketSubsystem = nullptr;
	std::shared_ptr<FDnsQuery> DnsQuery;
	std::atomic<lws_context*> Context{nullptr};
	lws* WebSocket = nullptr;
#endif

	void SetStatus(const TCHAR* NewStatus)
	{
		{
			std::lock_guard<std::mutex> Lock(Mutex);
			Status = NewStatus;
		}
		StateChanged.notify_all();
	}

	void SetFailure(const TCHAR* NewStatus)
	{
		bool Expected = false;
		if (Failed.compare_exchange_strong(Expected, true))
		{
			SetStatus(NewStatus);
		}
	}

	void CancelService()
	{
#if LIBRA_WITH_EDITOR_TRANSPORT && WITH_SSL
		std::lock_guard<std::mutex> Lock(ContextMutex);
		if (DnsQuery)
		{
			DnsQuery->Changed.notify_all();
		}
		if (lws_context* ActiveContext = Context.load())
		{
			lws_cancel_service(ActiveContext);
		}
#endif
	}

	bool HasOutgoing()
	{
		auto lock = std::lock_guard<std::mutex>(Mutex);
		return !SendQueue.empty();
	}

	static bool IsHex(TCHAR Character)
	{
		return (Character >= TEXT('0') && Character <= TEXT('9')) ||
			(Character >= TEXT('a') && Character <= TEXT('f')) ||
			(Character >= TEXT('A') && Character <= TEXT('F'));
	}

	static bool ParseFingerprint(const FString& Text, std::array<uint8, FingerprintBytes>& OutBytes)
	{
		if (Text.Len() != FingerprintBytes * 2)
		{
			return false;
		}

		for (int32 Index = 0; Index < static_cast<int32>(FingerprintBytes); ++Index)
		{
			const TCHAR High = Text[Index * 2];
			const TCHAR Low = Text[Index * 2 + 1];
			if (!IsHex(High) || !IsHex(Low))
			{
				return false;
			}

			auto HexValue = [](TCHAR Character) -> uint8
			{
				if (Character >= TEXT('0') && Character <= TEXT('9'))
				{
					return static_cast<uint8>(Character - TEXT('0'));
				}
				return static_cast<uint8>((Character | 0x20) - TEXT('a') + 10);
			};
			OutBytes[Index] = static_cast<uint8>((HexValue(High) << 4) | HexValue(Low));
		}
		return true;
	}

	static bool IsValidHost(const FString& Value)
	{
		if (Value.IsEmpty() || Value.Len() > 253 || Value.TrimStartAndEnd() != Value)
		{
			return false;
		}

		for (TCHAR Character : Value)
		{
			const bool bAllowed =
				(Character >= TEXT('a') && Character <= TEXT('z')) ||
				(Character >= TEXT('A') && Character <= TEXT('Z')) ||
				(Character >= TEXT('0') && Character <= TEXT('9')) ||
				Character == TEXT('.') || Character == TEXT('-') ||
				Character == TEXT(':') || Character == TEXT('[') || Character == TEXT(']');
			if (!bAllowed)
			{
				return false;
			}
		}
		return true;
	}

	static bool IsValidToken(const FString& Value)
	{
		if (Value.Len() != 64)
		{
			return false;
		}
		for (TCHAR Character : Value)
		{
			if (!((Character >= TEXT('0') && Character <= TEXT('9')) ||
				(Character >= TEXT('A') && Character <= TEXT('F'))))
			{
				return false;
			}
		}
		return true;
	}

#if LIBRA_WITH_EDITOR_TRANSPORT && WITH_SSL
	bool ResolveHost(std::string& OutAddress)
	{
		FString ResolveName = Host;
		if (ResolveName.StartsWith(TEXT("[")) && ResolveName.EndsWith(TEXT("]")))
		{
			ResolveName = ResolveName.Mid(1, ResolveName.Len() - 2);
		}
		if (auto numeric = SocketSubsystem->GetAddressFromString(ResolveName))
		{
			auto address = numeric->ToString(false).Replace(TEXT("["), TEXT("")).Replace(TEXT("]"), TEXT(""));
			auto encoded = FTCHARToUTF8(*address);
			OutAddress.assign(encoded.Get(), encoded.Length());
			return true;
		}

		auto query = std::make_shared<FDnsQuery>();
		{
			auto lock = std::lock_guard<std::mutex>(ContextMutex);
			if (DnsQuery)
			{
				auto queryLock = std::lock_guard<std::mutex>(DnsQuery->Mutex);
				if (!DnsQuery->Complete)
				{
					SetFailure(TEXT("A previous DNS query is still pending"));
					return false;
				}
			}
			DnsQuery = query;
		}
		const auto service = FString::FromInt(Port);
		// UE owns the resolver task; its callback never captures the transport.
		SocketSubsystem->GetAddressInfoAsync([query](FAddressInfoResult result)
		{
			auto lock = std::lock_guard<std::mutex>(query->Mutex);
			if (result.ReturnCode == SE_NO_ERROR)
			{
				for (const auto& entry : result.Results)
				{
					auto address = entry.Address->ToString(false).Replace(TEXT("["), TEXT("")).Replace(TEXT("]"), TEXT(""));
					if (query->Address.IsEmpty() || !address.Contains(TEXT(":")))
					{
						query->Address = MoveTemp(address);
						if (!query->Address.Contains(TEXT(":"))) { break; }
					}
				}
			}
			query->Complete = true;
			query->Changed.notify_all();
		}, *ResolveName, *service, EAddressInfoFlags::Default, NAME_None, ESocketType::SOCKTYPE_Streaming);
		auto lock = std::unique_lock<std::mutex>(query->Mutex);
		const auto ready = query->Changed.wait_for(lock, std::chrono::seconds(DnsTimeoutSeconds), [this, query]
		{
			return query->Complete || StopRequested.load();
		});
		if (StopRequested.load()) { return false; }
		if (!ready || query->Address.IsEmpty())
		{
			SetFailure(ready ? TEXT("DNS resolution failed") : TEXT("DNS resolution timed out"));
			return false;
		}
		const auto encoded = FTCHARToUTF8(*query->Address);
		OutAddress.assign(encoded.Get(), encoded.Length());
		return true;
	}

	bool VerifyCertificatePin(X509_STORE_CTX* StoreContext)
	{
		if (StoreContext == nullptr)
		{
			SetFailure(TEXT("TLS certificate verification failed"));
			return false;
		}

		const int32 Depth = X509_STORE_CTX_get_error_depth(StoreContext);
		if (Depth > 0)
		{
			if (PinConfirmed.load())
			{
				X509_STORE_CTX_set_error(StoreContext, X509_V_OK);
				return true;
			}
			SetFailure(TEXT("TLS certificate verification failed"));
			return false;
		}

		X509* Certificate = X509_STORE_CTX_get_current_cert(StoreContext);
		if (!IsCertificatePinMatch(Certificate, ExpectedFingerprint))
		{
			SetFailure(TEXT("TLS certificate pin mismatch"));
			return false;
		}

		PinConfirmed.store(true);
		X509_STORE_CTX_set_error(StoreContext, X509_V_OK);
		return true;
	}

	static int OpenSslVerifyCallback(int PreverifyOk, X509_STORE_CTX* StoreContext)
	{
		(void)PreverifyOk;
		SSL* Ssl = static_cast<SSL*>(X509_STORE_CTX_get_ex_data(StoreContext, SSL_get_ex_data_X509_STORE_CTX_idx()));
		SSL_CTX* SslContext = Ssl != nullptr ? SSL_get_SSL_CTX(Ssl) : nullptr;
		auto* State = SslContext != nullptr ? static_cast<FImpl*>(SSL_CTX_get_app_data(SslContext)) : nullptr;
		return State != nullptr && State->VerifyCertificatePin(StoreContext) ? 1 : 0;
	}

	static int WebSocketCallback(
		struct lws* Wsi,
		enum lws_callback_reasons Reason,
		void* User,
		void* In,
		size_t Length)
	{
		(void)User;
		FImpl* State = Wsi != nullptr
			? static_cast<FImpl*>(lws_context_user(lws_get_context(Wsi)))
			: nullptr;
		if (State == nullptr)
		{
			return 1;
		}

		switch (Reason)
		{
		case LWS_CALLBACK_OPENSSL_PERFORM_SERVER_CERT_VERIFICATION:
		{
			auto* StoreContext = static_cast<X509_STORE_CTX*>(User);
			if (!State->VerifyCertificatePin(StoreContext))
			{
				return 1;
			}
			return 0;
		}
		case LWS_CALLBACK_CLIENT_APPEND_HANDSHAKE_HEADER:
		{
			if (!State->PinConfirmed.load())
			{
				State->SetFailure(TEXT("TLS certificate pin was not verified"));
				return 1;
			}

			bool Expected = false;
			if (!State->AuthHeaderAdded.compare_exchange_strong(Expected, true))
			{
				State->SetFailure(TEXT("Redirects are not allowed"));
				return 1;
			}

			auto** HeaderPosition = static_cast<unsigned char**>(In);
			const auto* HeaderName = reinterpret_cast<const unsigned char*>("X-LiberaFx-Preview-Token:");
			const FTCHARToUTF8 EncodedToken(*State->AccessToken);
			const auto* HeaderValue = reinterpret_cast<const unsigned char*>(EncodedToken.Get());
			const size_t HeaderValueLength = static_cast<size_t>(EncodedToken.Length());
			if (HeaderPosition == nullptr || *HeaderPosition == nullptr ||
				lws_add_http_header_by_name(Wsi, HeaderName, HeaderValue, static_cast<int>(HeaderValueLength), HeaderPosition,
					*HeaderPosition + Length) != 0)
			{
				State->SetFailure(TEXT("Could not prepare the authenticated handshake"));
				return 1;
			}
			return 0;
		}
		case LWS_CALLBACK_CLIENT_FILTER_PRE_ESTABLISH:
			if (!State->PinConfirmed.load() || !State->AuthHeaderAdded.load())
			{
				State->SetFailure(TEXT("TLS authentication was not confirmed"));
				return 1;
			}
			return 0;
		case LWS_CALLBACK_CLIENT_ESTABLISHED:
			if (!State->PinConfirmed.load() || !State->AuthHeaderAdded.load())
			{
				State->SetFailure(TEXT("TLS authentication was not confirmed"));
				return 1;
			}
			State->Connected.store(true);
			State->SetStatus(TEXT("Connected"));
			return 0;
		case LWS_CALLBACK_CLIENT_RECEIVE:
		{
			if (lws_is_first_fragment(Wsi))
			{
				State->CurrentMessage.Reset();
				State->CurrentMessageIsBinary = lws_frame_is_binary(Wsi) != 0;
				if (!State->CurrentMessageIsBinary)
				{
					State->SetFailure(TEXT("Text messages are not accepted"));
					return -1;
				}
			}

			if (!State->CurrentMessageIsBinary ||
				Length > MaxMessageBytes - static_cast<size_t>(State->CurrentMessage.Num()))
			{
				State->SetFailure(TEXT("Incoming message limit exceeded"));
				return -1;
			}

			{
				std::lock_guard<std::mutex> Lock(State->Mutex);
				if (State->ReceiveQueue.size() >= MaxQueuedMessages ||
					State->QueuedBytes + static_cast<size_t>(State->CurrentMessage.Num()) + Length > MaxQueuedBytes)
				{
					State->Status = TEXT("Receive queue limit exceeded");
					State->Failed.store(true);
					State->StateChanged.notify_all();
					return -1;
				}
			}

			if (Length > 0)
			{
				const auto* Bytes = static_cast<const uint8_t*>(In);
				State->CurrentMessage.Append(Bytes, static_cast<int32>(Length));
			}

			if (lws_is_final_fragment(Wsi) && lws_remaining_packet_payload(Wsi) == 0)
			{
				std::lock_guard<std::mutex> Lock(State->Mutex);
				if (State->ReceiveQueue.size() >= MaxQueuedMessages ||
					State->QueuedBytes + State->CurrentMessage.Num() > MaxQueuedBytes)
				{
					State->Status = TEXT("Receive queue limit exceeded");
					State->Failed.store(true);
					State->StateChanged.notify_all();
					return -1;
				}
				State->QueuedBytes += static_cast<size_t>(State->CurrentMessage.Num());
				State->ReceiveQueue.emplace_back(MoveTemp(State->CurrentMessage));
				State->CurrentMessage.Reset();
			}
			return 0;
		}
		case LWS_CALLBACK_CLIENT_WRITEABLE:
		{
			if (State->StopRequested.load())
			{
				lws_close_reason(Wsi, LWS_CLOSE_STATUS_NORMAL, nullptr, 0);
				State->CloseSent.store(true);
				return -1;
			}

			static constexpr char ResyncMessage[] = "{\"type\":\"resync\"}";
			auto message = TArray<uint8>{};
			if (State->ResyncRequested.exchange(false))
				message.Append(reinterpret_cast<const uint8*>(ResyncMessage), sizeof(ResyncMessage) - 1);
			else
			{
				auto lock = std::lock_guard<std::mutex>(State->Mutex);
				if (State->SendQueue.empty()) return 0;
				message = MoveTemp(State->SendQueue.front());
				State->SendBytes -= message.Num();
				State->SendQueue.pop_front();
			}
			auto buffer = std::vector<unsigned char>(LWS_PRE + message.Num());
			std::memcpy(buffer.data() + LWS_PRE, message.GetData(), message.Num());
			auto written = lws_write(Wsi, buffer.data() + LWS_PRE, message.Num(), LWS_WRITE_TEXT);
			if (written != message.Num())
			{
				State->SetFailure(TEXT("Could not send preview feedback"));
				return -1;
			}
			if (State->HasOutgoing()) lws_callback_on_writable(Wsi);
			return 0;
		}
		case LWS_CALLBACK_CLIENT_CONNECTION_ERROR:
			if (!State->StopRequested.load() && !State->Failed.load())
			{
				State->SetFailure(TEXT("Connection failed"));
			}
			State->Connected.store(false);
			return 0;
		case LWS_CALLBACK_CLIENT_CLOSED:
			State->Connected.store(false);
			if (!State->StopRequested.load() && !State->Failed.load())
			{
				State->SetFailure(TEXT("Disconnected"));
			}
			return 0;
		default:
			return 0;
		}
	}

	void Run()
	{
		FTCHARToUTF8 HostUtf8(*Host);
		const std::string HostName(HostUtf8.Get(), HostUtf8.Length());
		std::string NumericAddress;
		if (!ResolveHost(NumericAddress))
		{
			if (StopRequested.load())
			{
				SetStatus(TEXT("Stopped"));
			}
			return;
		}

		SSL_CTX* SslContext = SSL_CTX_new(TLS_client_method());
		if (SslContext == nullptr)
		{
			SetFailure(TEXT("Could not initialize TLS"));
			return;
		}

		SSL_CTX_set_app_data(SslContext, this);
		if (SSL_CTX_set_min_proto_version(SslContext, TLS1_2_VERSION) != 1)
		{
			SSL_CTX_free(SslContext);
			SetFailure(TEXT("Could not configure TLS"));
			return;
		}
		SSL_CTX_set_verify(SslContext, SSL_VERIFY_PEER, OpenSslVerifyCallback);

		static const lws_protocols Protocols[] = {
			{"liberafx-preview-v3", WebSocketCallback, 0, 64 * 1024},
			{nullptr, nullptr, 0, 0}
		};

		lws_context_creation_info ContextInfo{};
		ContextInfo.port = CONTEXT_PORT_NO_LISTEN;
		ContextInfo.protocols = Protocols;
		ContextInfo.user = this;
		ContextInfo.timeout_secs = ConnectTimeoutSeconds;
		ContextInfo.ws_ping_pong_interval = 5;
		ContextInfo.provided_client_ssl_ctx = SslContext;
		ContextInfo.options = 0;

		lws_context* NewContext = lws_create_context(&ContextInfo);
		if (NewContext == nullptr)
		{
			SSL_CTX_free(SslContext);
			SetFailure(TEXT("Could not initialize secure WebSocket transport"));
			return;
		}
		{
			std::lock_guard<std::mutex> Lock(ContextMutex);
			Context.store(NewContext);
		}
		if (StopRequested.load())
		{
			{
				std::lock_guard<std::mutex> Lock(ContextMutex);
				Context.store(nullptr);
			}
			lws_context_destroy(NewContext);
			SSL_CTX_free(SslContext);
			SetStatus(TEXT("Stopped"));
			return;
		}

		lws_client_connect_info ConnectInfo{};
		ConnectInfo.context = NewContext;
		ConnectInfo.address = NumericAddress.c_str();
		ConnectInfo.port = Port;
		ConnectInfo.ssl_connection = LCCSCF_USE_SSL | LCCSCF_SKIP_SERVER_CERT_HOSTNAME_CHECK;
		ConnectInfo.path = "/preview?protocol=3";
		ConnectInfo.host = HostName.c_str();
		ConnectInfo.protocol = nullptr;
		ConnectInfo.ietf_version_or_minus_one = -1;
		ConnectInfo.pwsi = &WebSocket;
		ConnectInfo.local_protocol_name = Protocols[0].name;

		lws* InitialSocket = lws_client_connect_via_info(&ConnectInfo);
		if (InitialSocket == nullptr)
		{
			SetFailure(TEXT("Connection failed"));
		}

		auto CloseDeadline = (std::chrono::steady_clock::time_point::max)();
		while (!Failed.load())
		{
			if (StopRequested.load())
			{
				if (CloseDeadline == (std::chrono::steady_clock::time_point::max)())
				{
					CloseDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(1);
				}
				if (!Connected.load() || WebSocket == nullptr || std::chrono::steady_clock::now() >= CloseDeadline)
				{
					break;
				}
				if (!CloseSent.load())
				{
					lws_callback_on_writable(WebSocket);
				}
			}
			else if ((ResyncRequested.load() || HasOutgoing()) && Connected.load() && WebSocket != nullptr)
			{
				lws_callback_on_writable(WebSocket);
			}
			if (lws_service(NewContext, 100) < 0 && !StopRequested.load())
			{
				SetFailure(TEXT("Connection failed"));
				break;
			}
			if (StopRequested.load() && CloseSent.load() && !Connected.load())
			{
				break;
			}
		}

		Connected.store(false);
		{
			std::lock_guard<std::mutex> Lock(ContextMutex);
			Context.store(nullptr);
		}
		lws_context_destroy(NewContext);
		SSL_CTX_free(SslContext);
		if (StopRequested.load())
		{
			SetStatus(TEXT("Stopped"));
		}
		else if (!Failed.load())
		{
			SetStatus(TEXT("Disconnected"));
		}
	}

#else
	void Run()
	{
		SetFailure(TEXT("Secure Editor transport is unavailable on this platform"));
	}
#endif
};

FLibraEditorTransport::FLibraEditorTransport()
	: Impl(MakeUnique<FImpl>())
{
}

FLibraEditorTransport::~FLibraEditorTransport()
{
	Stop();
}

bool FLibraEditorTransport::Start(const FLibraEditorTransportSettings& Settings, FString& OutError)
{
#if !LIBRA_WITH_EDITOR_TRANSPORT || !WITH_SSL
	(void)Settings;
	OutError = TEXT("Secure Editor transport is unavailable on this platform.");
	Impl->SetStatus(TEXT("Unavailable"));
	return false;
#else
	Stop();
	OutError.Reset();

	Impl->SocketSubsystem = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
	if (Impl->SocketSubsystem == nullptr)
	{
		OutError = TEXT("Platform socket subsystem is unavailable.");
		Impl->SetStatus(TEXT("Unavailable"));
		return false;
	}

	if (!FImpl::IsValidHost(Settings.Host) || Settings.Port == 0 ||
		!FImpl::IsValidToken(Settings.AccessToken) ||
		!FImpl::ParseFingerprint(Settings.CertificateFingerprint, Impl->ExpectedFingerprint))
	{
		OutError = TEXT("Invalid host, port, access token, or certificate fingerprint.");
		Impl->SetStatus(TEXT("Invalid settings"));
		return false;
	}

	Impl->Host = Settings.Host;
	Impl->Port = Settings.Port;
	Impl->AccessToken = Settings.AccessToken;
	Impl->StopRequested.store(false);
	Impl->Failed.store(false);
	Impl->Connected.store(false);
	Impl->PinConfirmed.store(false);
	Impl->AuthHeaderAdded.store(false);
	Impl->ResyncRequested.store(false);
	Impl->CloseSent.store(false);
	Impl->WebSocket = nullptr;
	Impl->CurrentMessage.Reset();
	{
		std::lock_guard<std::mutex> Lock(Impl->Mutex);
		Impl->ReceiveQueue.clear();
		Impl->SendQueue.clear();
		Impl->SendBytes = 0;
		Impl->QueuedBytes = 0;
		Impl->Status = TEXT("Connecting");
	}

	Impl->Worker = std::thread([State = Impl.Get()]()
	{
		State->Run();
	});

	std::unique_lock<std::mutex> Lock(Impl->Mutex);
	const bool bReady = Impl->StateChanged.wait_for(
		Lock,
		std::chrono::seconds(FImpl::ConnectTimeoutSeconds + FImpl::DnsTimeoutSeconds + 2),
		[State = Impl.Get()]
	{
		return State->Connected.load() || State->Failed.load() || State->StopRequested.load();
	});
	const bool bConnected = bReady && Impl->Connected.load();
	if (!bConnected)
	{
		Lock.unlock();
		const TCHAR* FailureStatus = nullptr;
		if (!Impl->Failed.load())
		{
			Impl->SetFailure(bReady ? TEXT("Connection failed") : TEXT("Connection attempt timed out"));
			FailureStatus = bReady ? TEXT("Connection failed") : TEXT("Connection attempt timed out");
			OutError = bReady ? TEXT("Secure connection failed.") : TEXT("Secure connection attempt timed out.");
		}
		else
		{
			FailureStatus = TEXT("Connection failed verification or handshake");
			OutError = TEXT("Secure connection failed verification or handshake.");
		}
		Stop();
		Impl->SetStatus(FailureStatus);
		return false;
	}
	return true;
#endif
}

void FLibraEditorTransport::Stop()
{
	if (!Impl)
	{
		return;
	}
	Impl->StopRequested.store(true);
	Impl->CancelService();
	if (Impl->Worker.joinable())
	{
		Impl->Worker.join();
	}
	Impl->Connected.store(false);
}

bool FLibraEditorTransport::IsConnected() const
{
	return Impl && Impl->Connected.load();
}

bool FLibraEditorTransport::TryReceive(TArray<uint8>& OutMessage)
{
	if (!Impl)
	{
		return false;
	}
	std::lock_guard<std::mutex> Lock(Impl->Mutex);
	if (Impl->ReceiveQueue.empty())
	{
		return false;
	}
	OutMessage = MoveTemp(Impl->ReceiveQueue.front());
	Impl->QueuedBytes -= static_cast<size_t>(OutMessage.Num());
	Impl->ReceiveQueue.pop_front();
	return true;
}

void FLibraEditorTransport::RequestResync()
{
	if (Impl && Impl->Connected.load())
	{
		Impl->ResyncRequested.store(true);
		Impl->CancelService();
	}
}

bool FLibraEditorTransport::QueueText(const FString& Message)
{
	if (!Impl || !Impl->Connected.load()) return false;
	auto encoded = FTCHARToUTF8(*Message);
	if (encoded.Length() == 0 || encoded.Length() > 1024 * 1024) return false;
	{
		auto lock = std::lock_guard<std::mutex>(Impl->Mutex);
		if (Impl->SendQueue.size() >= 32 || Impl->SendBytes + encoded.Length() > 4 * 1024 * 1024) return false;
		auto bytes = TArray<uint8>{};
		bytes.Append(reinterpret_cast<const uint8*>(encoded.Get()), encoded.Length());
		Impl->SendBytes += bytes.Num();
		Impl->SendQueue.emplace_back(MoveTemp(bytes));
	}
	Impl->CancelService();
	return true;
}

FString FLibraEditorTransport::GetStatus() const
{
	if (!Impl)
	{
		return TEXT("Stopped");
	}
	std::lock_guard<std::mutex> Lock(Impl->Mutex);
	return Impl->Status;
}

#if WITH_DEV_AUTOMATION_TESTS && LIBRA_WITH_EDITOR_TRANSPORT && WITH_SSL
IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FLibraEditorTransportValidationTest,
	"Libra.Runtime.EditorTransport.Validation",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraEditorTransportValidationTest::RunTest(const FString& Parameters)
{
	(void)Parameters;

	const FString ValidToken = TEXT("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");
	const FString ValidPin = TEXT("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");
	FString Error;

	{
		FLibraEditorTransport Transport;
		FLibraEditorTransportSettings Settings;
		Settings.AccessToken = ValidToken;
		Settings.CertificateFingerprint = ValidPin;
		Settings.Host = TEXT("localhost\r\nX-LiberaFx-Preview-Token: injected");
		TestFalse(TEXT("Reject host header injection before connecting"), Transport.Start(Settings, Error));
		TestEqual(TEXT("Invalid host status"), Transport.GetStatus(), FString(TEXT("Invalid settings")));
	}
	{
		FLibraEditorTransport Transport;
		FLibraEditorTransportSettings Settings;
		Settings.Host = TEXT("localhost/path");
		Settings.AccessToken = ValidToken;
		Settings.CertificateFingerprint = ValidPin;
		TestFalse(TEXT("Reject URL path in host before connecting"), Transport.Start(Settings, Error));
		TestEqual(TEXT("URL host rejected during settings validation"), Transport.GetStatus(), FString(TEXT("Invalid settings")));
	}
	{
		FLibraEditorTransport Transport;
		FLibraEditorTransportSettings Settings;
		Settings.Host = TEXT("localhost");
		Settings.CertificateFingerprint = ValidPin;
		TestFalse(TEXT("Reject missing access token before connecting"), Transport.Start(Settings, Error));
		TestEqual(TEXT("Missing token rejected during settings validation"), Transport.GetStatus(), FString(TEXT("Invalid settings")));
	}
	{
		FLibraEditorTransport Transport;
		FLibraEditorTransportSettings Settings;
		Settings.Host = TEXT("localhost");
		Settings.AccessToken = ValidToken;
		TestFalse(TEXT("Reject missing certificate pin before connecting"), Transport.Start(Settings, Error));
		TestEqual(TEXT("Missing pin rejected during settings validation"), Transport.GetStatus(), FString(TEXT("Invalid settings")));
	}
	{
		FLibraEditorTransport Transport;
		FLibraEditorTransportSettings Settings;
		Settings.Host = TEXT("localhost");
		Settings.AccessToken = TEXT("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCD\r\n");
		Settings.CertificateFingerprint = ValidPin;
		TestFalse(TEXT("Reject malformed token header value before connecting"), Transport.Start(Settings, Error));
		TestEqual(TEXT("Injected token rejected during settings validation"), Transport.GetStatus(), FString(TEXT("Invalid settings")));
	}
	{
		FLibraEditorTransport Transport;
		FLibraEditorTransportSettings Settings;
		Settings.Host = TEXT("localhost");
		Settings.AccessToken = ValidToken;
		Settings.CertificateFingerprint = TEXT("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEG");
		TestFalse(TEXT("Reject malformed fingerprint before connecting"), Transport.Start(Settings, Error));
		TestEqual(TEXT("Malformed fingerprint rejected during settings validation"), Transport.GetStatus(), FString(TEXT("Invalid settings")));
	}

	std::unique_ptr<EVP_PKEY_CTX, decltype(&EVP_PKEY_CTX_free)> KeyContext(
		EVP_PKEY_CTX_new_id(EVP_PKEY_EC, nullptr),
		EVP_PKEY_CTX_free);
	TestTrue(TEXT("Create an ephemeral key generator"), KeyContext != nullptr);
	if (!KeyContext ||
		EVP_PKEY_keygen_init(KeyContext.get()) <= 0 ||
		EVP_PKEY_CTX_set_ec_paramgen_curve_nid(KeyContext.get(), NID_X9_62_prime256v1) <= 0)
	{
		AddError(TEXT("Could not initialize ephemeral certificate key generation."));
		return false;
	}

	EVP_PKEY* RawKey = nullptr;
	if (EVP_PKEY_keygen(KeyContext.get(), &RawKey) <= 0 || RawKey == nullptr)
	{
		AddError(TEXT("Could not generate an ephemeral certificate key."));
		return false;
	}
	std::unique_ptr<EVP_PKEY, decltype(&EVP_PKEY_free)> Key(RawKey, EVP_PKEY_free);

	std::unique_ptr<X509, decltype(&X509_free)> Certificate(X509_new(), X509_free);
	TestTrue(TEXT("Allocate an ephemeral certificate"), Certificate != nullptr);
	if (!Certificate ||
		X509_set_version(Certificate.get(), 2) != 1 ||
		ASN1_INTEGER_set(X509_get_serialNumber(Certificate.get()), 1) != 1 ||
		X509_gmtime_adj(X509_get_notBefore(Certificate.get()), -60) == nullptr ||
		X509_gmtime_adj(X509_get_notAfter(Certificate.get()), 3600) == nullptr ||
		X509_set_pubkey(Certificate.get(), Key.get()) != 1)
	{
		AddError(TEXT("Could not initialize an ephemeral certificate."));
		return false;
	}

	X509_NAME* Subject = X509_get_subject_name(Certificate.get());
	static const unsigned char CommonName[] = "libra-transport-test";
	if (Subject == nullptr ||
		X509_NAME_add_entry_by_txt(Subject, "CN", MBSTRING_ASC, CommonName, -1, -1, 0) != 1 ||
		X509_set_issuer_name(Certificate.get(), Subject) != 1 ||
		X509_sign(Certificate.get(), Key.get(), EVP_sha256()) <= 0)
	{
		AddError(TEXT("Could not sign an ephemeral certificate."));
		return false;
	}

	const int32 DerSize = i2d_X509(Certificate.get(), nullptr);
	TestTrue(TEXT("Serialize certificate DER"), DerSize > 0);
	if (DerSize <= 0)
	{
		return false;
	}

	std::vector<uint8_t> Der(static_cast<size_t>(DerSize));
	unsigned char* DerCursor = Der.data();
	if (i2d_X509(Certificate.get(), &DerCursor) != DerSize)
	{
		AddError(TEXT("Could not serialize certificate DER."));
		return false;
	}

	std::array<uint8, 32> CorrectFingerprint{};
	if (SHA256(Der.data(), Der.size(), CorrectFingerprint.data()) == nullptr)
	{
		AddError(TEXT("Could not hash certificate DER."));
		return false;
	}
	TestTrue(TEXT("Accept the exact certificate DER SHA-256 pin"),
		IsCertificatePinMatch(Certificate.get(), CorrectFingerprint));

	CorrectFingerprint[0] ^= 0x80;
	TestFalse(TEXT("Reject a different certificate pin"),
		IsCertificatePinMatch(Certificate.get(), CorrectFingerprint));
	return true;
}
#endif
