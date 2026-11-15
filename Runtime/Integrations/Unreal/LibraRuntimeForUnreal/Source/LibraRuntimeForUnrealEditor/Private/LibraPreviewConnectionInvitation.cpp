#include "LibraPreviewConnectionInvitation.h"

#include "Serialization/JsonReader.h"

namespace
{
    bool isInvitationHexCharacter(TCHAR value)
    {
        return (value >= TEXT('0') && value <= TEXT('9')) || (value >= TEXT('a') && value <= TEXT('f')) ||
            (value >= TEXT('A') && value <= TEXT('F'));
    }

    bool isInvitationHex(const FString& value)
    {
        if (value.Len() != 64) return false;
        for (auto c : value)
            if (!isInvitationHexCharacter(c)) return false;
        return true;
    }

    bool isInvitationIpv4(const FString& host)
    {
        auto groups = TArray<FString>{};
        host.ParseIntoArray(groups, TEXT("."), false);
        if (groups.Num() != 4) return false;
        for (const auto& group : groups)
        {
            if (group.IsEmpty() || group.Len() > 3) return false;
            for (auto c : group) if (c < TEXT('0') || c > TEXT('9')) return false;
            if (FCString::Atoi(*group) > 255) return false;
        }
        return true;
    }

    bool isInvitationHost(const FString& host)
    {
        if (host.IsEmpty() || host.Len() > 253) return false;
        if (host.Contains(TEXT(":")))
        {
            auto compressed = host.Find(TEXT("::"));
            if (host.Contains(TEXT(":::")) ||
                (compressed != INDEX_NONE && host.Find(TEXT("::"), ESearchCase::CaseSensitive,
                    ESearchDir::FromStart, compressed + 2) != INDEX_NONE) ||
                (host.StartsWith(TEXT(":")) && !host.StartsWith(TEXT("::"))) ||
                (host.EndsWith(TEXT(":")) && !host.EndsWith(TEXT("::")))) return false;
            auto groups = TArray<FString>{};
            host.ParseIntoArray(groups, TEXT(":"), true);
            auto words = 0;
            for (auto index = 0; index < groups.Num(); ++index)
            {
                const auto& group = groups[index];
                if (group.Contains(TEXT(".")))
                {
                    if (index != groups.Num() - 1 || !isInvitationIpv4(group)) return false;
                    words += 2;
                }
                else
                {
                    if (group.Len() > 4) return false;
                    for (auto c : group) if (!isInvitationHexCharacter(c)) return false;
                    ++words;
                }
            }
            return compressed == INDEX_NONE ? words == 8 : words < 8;
        }
        auto numeric = true;
        for (auto c : host) numeric &= (c >= TEXT('0') && c <= TEXT('9')) || c == TEXT('.');
        if (numeric && host.Contains(TEXT("."))) return isInvitationIpv4(host);
        auto labels = TArray<FString>{};
        auto domain = host.EndsWith(TEXT(".")) ? host.LeftChop(1) : host;
        domain.ParseIntoArray(labels, TEXT("."), false);
        if (labels.IsEmpty()) return false;
        for (const auto& label : labels)
        {
            if (label.IsEmpty() || label.Len() > 63 || label.StartsWith(TEXT("-")) || label.EndsWith(TEXT("-")))
                return false;
            for (auto c : label)
                if (!((c >= TEXT('0') && c <= TEXT('9')) || (c >= TEXT('a') && c <= TEXT('z')) ||
                    (c >= TEXT('A') && c <= TEXT('Z')) || c == TEXT('-'))) return false;
        }
        return true;
    }
}

bool ParseLibraPreviewConnectionInvitation(const FString& text, FLibraPreviewConnectionInvitation& result, FString& error)
{
    error = TEXT("Invalid connection invitation. Copy connection information from LIBERA FX Connections, then Connect.");
    if (text.Len() > 4096 || FTCHARToUTF8(*text).Length() > 4096) return false;
    auto reader = TJsonReaderFactory<>::Create(text);
    auto notation = EJsonNotation::Error;
    if (!reader->ReadNext(notation) || notation != EJsonNotation::ObjectStart) return false;
    auto candidate = FLibraPreviewConnectionInvitation{};
    auto fields = TSet<FString>{};
    auto type = FString{};
    auto version = double{0};
    auto port = double{0};
    auto ended = false;
    while (reader->ReadNext(notation))
    {
        if (notation == EJsonNotation::ObjectEnd) { ended = true; break; }
        auto key = reader->GetIdentifier();
        if (fields.Contains(key)) return false;
        fields.Add(key);
        if (key == TEXT("version") || key == TEXT("port"))
        {
            if (notation != EJsonNotation::Number) return false;
            if (key == TEXT("version")) version = reader->GetValueAsNumber();
            else port = reader->GetValueAsNumber();
        }
        else
        {
            if (notation != EJsonNotation::String) return false;
            auto value = FString(reader->GetValueAsString());
            if (key == TEXT("type")) type = MoveTemp(value);
            else if (key == TEXT("host")) candidate.host = MoveTemp(value);
            else if (key == TEXT("accessToken")) candidate.accessToken = MoveTemp(value);
            else if (key == TEXT("certificateFingerprint")) candidate.certificateFingerprint = MoveTemp(value);
            else return false;
        }
    }
    if (!ended || reader->ReadNext(notation) || !reader->GetErrorMessage().IsEmpty() || fields.Num() != 6 ||
        type != TEXT("librafx_preview_connection") || version != 1 || !FMath::IsFinite(port) ||
        port < 1 || port > 65535 || FMath::FloorToDouble(port) != port || !isInvitationHost(candidate.host) ||
        !isInvitationHex(candidate.accessToken) || !isInvitationHex(candidate.certificateFingerprint)) return false;
    candidate.port = static_cast<int32>(port);
    result = MoveTemp(candidate);
    error.Reset();
    return true;
}

#if WITH_DEV_AUTOMATION_TESTS
#include "Misc/AutomationTest.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewInvitationTest, "Libra.Editor.Preview.ConnectionInvitation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewInvitationTest::RunTest(const FString&)
{
    auto valid = FString(TEXT("{\"type\":\"librafx_preview_connection\",\"version\":1,\"host\":\"127.0.0.1\","
        "\"port\":8766,\"accessToken\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\","
        "\"certificateFingerprint\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\"}"));
    auto invitation = FLibraPreviewConnectionInvitation{};
    auto error = FString{};
    TestTrue(TEXT("A valid clipboard invitation is parsed transactionally"),
        ParseLibraPreviewConnectionInvitation(valid, invitation, error));
    TestTrue(TEXT("DNS hostnames are supported"), ParseLibraPreviewConnectionInvitation(
        valid.Replace(TEXT("127.0.0.1"), TEXT("preview.local")), invitation, error));
    TestTrue(TEXT("IPv6 addresses are supported"), ParseLibraPreviewConnectionInvitation(
        valid.Replace(TEXT("127.0.0.1"), TEXT("::1")), invitation, error));
    for (const auto& invalid : {
        valid.Replace(TEXT("\"version\":1"), TEXT("\"version\":2")),
        valid.Replace(TEXT("\"port\":8766"), TEXT("\"port\":8766,\"port\":9000")),
        valid.Replace(TEXT("\"host\":\"127.0.0.1\""), TEXT("\"host\":\"127.0.0.1\",\"\\u0068ost\":\"localhost\"")),
        valid.Replace(TEXT("\"port\":8766"), TEXT("\"port\":1.5")),
        valid.Replace(TEXT("\"port\":8766"), TEXT("\"port\":65536")),
        valid.Replace(TEXT("127.0.0.1"), TEXT("https://localhost/path")),
        valid.Replace(TEXT("127.0.0.1"), TEXT("localhost\\r\\nInjected")),
        valid.Replace(TEXT("127.0.0.1"), TEXT("999.0.0.1")),
        valid.Replace(TEXT("127.0.0.1"), TEXT(":::1")),
        valid.Replace(TEXT("127.0.0.1"), TEXT("::\uff11")),
        valid.Replace(TEXT("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            TEXT("not-a-preview-token")),
        valid.LeftChop(1) + TEXT(",\"extra\":true}"),
        valid + TEXT("{}"),
        FString::ChrN(4097, TEXT('x'))})
    {
        TestFalse(TEXT("Malformed, duplicate, unsafe or oversized clipboard invitations are rejected"),
            ParseLibraPreviewConnectionInvitation(invalid, invitation, error));
        TestTrue(TEXT("Rejected invitation does not replace the last validated endpoint"), invitation.host == TEXT("::1"));
    }
    return true;
}
#endif
