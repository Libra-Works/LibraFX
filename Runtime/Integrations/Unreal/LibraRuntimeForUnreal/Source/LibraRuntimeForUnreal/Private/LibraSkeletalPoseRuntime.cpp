#include "LibraSkeletalPoseRuntime.h"

#include "Components/ActorComponent.h"
#include "Components/SkeletalMeshComponent.h"
#include "Engine/SkeletalMesh.h"
#include "Misc/Base64.h"
#include "liberafx/LbSpawnableHost.h"

#include <array>
#include <cmath>

namespace {
constexpr const TCHAR* MatrixPrefix = TEXT("__LFXS1M_");
constexpr const TCHAR* ValidityPrefix = TEXT("__LFXS1V_");

auto BigEndianUint32(const uint8* value) -> uint32
{
    return (static_cast<uint32>(value[0]) << 24) | (static_cast<uint32>(value[1]) << 16) |
        (static_cast<uint32>(value[2]) << 8) | static_cast<uint32>(value[3]);
}

auto Utf8(const FString& value) -> std::string
{
    return std::string(TCHAR_TO_UTF8(*value));
}

auto TryDecodeUtf8(const std::string& value, FString& out) -> bool
{
    out = UTF8_TO_TCHAR(value.c_str());
    return std::string(TCHAR_TO_UTF8(*out)) == value;
}

auto ReadBindingBytes(const std::string& name, bool& isValidity, TArray<uint8>& bytes) -> bool
{
    isValidity = false;
    auto sourceName = UTF8_TO_TCHAR(name.c_str());
    auto prefix = FCString::Strncmp(sourceName, MatrixPrefix, FCString::Strlen(MatrixPrefix)) == 0
        ? MatrixPrefix : FCString::Strncmp(sourceName, ValidityPrefix, FCString::Strlen(ValidityPrefix)) == 0
        ? ValidityPrefix : nullptr;
    if (!prefix) return false;
    isValidity = prefix == ValidityPrefix;
    auto token = FString(sourceName + FCString::Strlen(prefix));
    token.ReplaceInline(TEXT("-"), TEXT("+"));
    token.ReplaceInline(TEXT("_"), TEXT("/"));
    while ((token.Len() % 4) != 0) token.AppendChar(TEXT('='));
    if (!FBase64::Decode(token, bytes)) return false;
    auto encoded = FBase64::Encode(bytes);
    encoded.ReplaceInline(TEXT("+"), TEXT("-"));
    encoded.ReplaceInline(TEXT("/"), TEXT("_"));
    encoded.ReplaceInline(TEXT("="), TEXT(""));
    return encoded == FString(sourceName + FCString::Strlen(prefix));
}

auto ValidatePath(const FString& path) -> bool
{
    if (path.IsEmpty() || path.StartsWith(TEXT("/")) || path.EndsWith(TEXT("/"))) return false;
    for (const auto character : path)
        if (FChar::IsControl(character)) return false;
    TArray<FString> parts;
    path.ParseIntoArray(parts, TEXT("/"), false);
    return parts.Num() > 0 && !parts.ContainsByPredicate([](const FString& part)
    {
        return part.IsEmpty() || part == TEXT(".") || part == TEXT("..") ||
            part.Contains(TEXT("\n")) || part.Contains(TEXT("\r"));
    });
}

auto HasControlCharacter(const FString& value) -> bool
{
    for (const auto character : value)
        if (FChar::IsControl(character)) return true;
    return false;
}

auto HasSourcePrefix(const std::string& name) -> bool
{
    return name.rfind("__LFXS1M_", 0) == 0 || name.rfind("__LFXS1V_", 0) == 0;
}

auto ResolveBoneIndex(const USkeletalMeshComponent& component, const FString& path) -> int32
{
    auto* skeletalMesh = component.GetSkeletalMeshAsset();
    if (!skeletalMesh || !ValidatePath(path)) return INDEX_NONE;
    const auto& skeleton = skeletalMesh->GetRefSkeleton();
    for (auto index = 0; index < skeleton.GetNum(); ++index)
    {
        auto segments = TArray<FString>{};
        auto current = index;
        while (current != INDEX_NONE)
        {
            segments.Insert(skeleton.GetBoneName(current).ToString(), 0);
            current = skeleton.GetParentIndex(current);
        }
        if (FString::Join(segments, TEXT("/")) == path) return index;
    }
    return INDEX_NONE;
}

auto MatrixValues(const FTransform& transform, std::array<double, 16>& out) -> bool
{
    const auto matrix = transform.ToMatrixWithScale();
    for (auto row = 0; row < 4; ++row)
        for (auto column = 0; column < 4; ++column)
        {
            const auto value = static_cast<double>(matrix.M[column][row]);
            if (!FMath::IsFinite(value)) return false;
            // Unreal's FMatrix uses row-vector composition. Transpose into the
            // Core's row-major storage / column-vector convention.
            out[static_cast<std::size_t>(row * 4 + column)] = value;
        }
    return true;
}
}

bool FLibraSkeletalPoseRuntime::TryParse(const std::string& name,
    FLibraSkeletalPoseSource& source, bool& isValidity)
{
    auto bytes = TArray<uint8>{};
    if (!ReadBindingBytes(name, isValidity, bytes) || bytes.Num() < 21 || bytes[0] != 1)
        return false;
    source.SourceNodeId = FGuid(BigEndianUint32(bytes.GetData() + 1),
        BigEndianUint32(bytes.GetData() + 5), BigEndianUint32(bytes.GetData() + 9),
        BigEndianUint32(bytes.GetData() + 13));
    source.Target = bytes[17];
    source.Space = bytes[18];
    const auto keyLength = static_cast<int32>(bytes[19]);
    if (bytes.Num() < 21 + keyLength) return false;
    const auto boneLength = static_cast<int32>(bytes[20 + keyLength]);
    if (bytes.Num() != 21 + keyLength + boneLength || source.Target > 1 || source.Space > 2)
        return false;
    const auto key = std::string(reinterpret_cast<const char*>(bytes.GetData() + 20), keyLength);
    const auto bone = std::string(reinterpret_cast<const char*>(bytes.GetData() + 21 + keyLength), boneLength);
    if (!TryDecodeUtf8(key, source.Key) || !TryDecodeUtf8(bone, source.BonePath) ||
        key.size() + bone.size() > 63 || !source.SourceNodeId.IsValid() ||
        (source.Target == 0 && !source.Key.IsEmpty()) ||
        source.Key.TrimStartAndEnd() != source.Key || HasControlCharacter(source.Key) ||
        (source.Target == 1 && source.Key.IsEmpty()) || !ValidatePath(source.BonePath))
        return false;
    return true;
}

bool FLibraSkeletalPoseRuntime::Initialize(const liberafx::SpawnableHost& host,
    AActor* owner, TArray<UActorComponent*>& tickPrerequisites)
{
    Sources.Reset();
    TickPrerequisites.Reset();
    TMap<FGuid, FLibraSkeletalPoseSource> parsed;
    for (const auto& port : host.package().gameIoPorts)
    {
        auto source = FLibraSkeletalPoseSource{};
        auto isValidity = false;
        if (!TryParse(port.name, source, isValidity))
        {
            if (HasSourcePrefix(port.name)) return false;
            continue;
        }
        auto& existing = parsed.FindOrAdd(source.SourceNodeId);
        if (!existing.SourceNodeId.IsValid())
            existing = source;
        else if (existing.Key != source.Key || existing.BonePath != source.BonePath ||
            existing.Target != source.Target || existing.Space != source.Space)
            return false;
        if (isValidity)
        {
            if (!existing.ValidityPort.IsEmpty()) return false;
            existing.ValidityPort = UTF8_TO_TCHAR(port.name.c_str());
        }
        else
        {
            if (!existing.MatrixPort.IsEmpty()) return false;
            existing.MatrixPort = UTF8_TO_TCHAR(port.name.c_str());
        }
    }

    for (auto& pair : parsed)
    {
        auto source = MoveTemp(pair.Value);
        if (source.MatrixPort.IsEmpty() || source.ValidityPort.IsEmpty()) return false;
        const auto hasTarget = source.Target == 0
            ? ULibraSkeletalPoseTargetComponent::ResolveOwnerTarget(owner, source.Registration)
            : ULibraSkeletalPoseTargetComponent::ResolveKeyTarget(source.Key, source.Registration);
        if (hasTarget)
        {
            source.bBound = true;
            if (auto* mesh = source.Registration.Mesh.Get())
            {
                tickPrerequisites.AddUnique(mesh);
                TickPrerequisites.AddUnique(mesh);
            }
        }
        Sources.Add(MoveTemp(source));
    }
    return true;
}

auto FLibraSkeletalPoseRuntime::TryCapture(FLibraSkeletalPoseSource& source,
    AActor* owner, std::array<double, 16>& matrix) -> bool
{
    matrix.fill(0);
    if (source.bInvalid) return false;
    if (!source.bBound)
    {
        source.bBound = source.Target == 0
            ? ULibraSkeletalPoseTargetComponent::ResolveOwnerTarget(owner, source.Registration)
            : ULibraSkeletalPoseTargetComponent::ResolveKeyTarget(source.Key, source.Registration);
        if (!source.bBound) return false;
    }
    if (!ULibraSkeletalPoseTargetComponent::IsCurrent(source.Registration))
    {
        source.bInvalid = true;
        return false;
    }
    auto* mesh = source.Registration.Mesh.Get();
    if (!mesh || mesh->GetSkeletalMeshAsset() != source.Registration.MeshAsset.Get())
    {
        source.bInvalid = true;
        return false;
    }
    const auto boneIndex = ResolveBoneIndex(*mesh, source.BonePath);
    if (boneIndex == INDEX_NONE) return false;
    const auto componentTransform = mesh->GetComponentTransform();
    const auto componentSpaceBone = mesh->GetBoneTransform(boneIndex, FTransform::Identity);
    FTransform selected;
    if (source.Space == 0)
    {
        const auto parentIndex = mesh->GetSkeletalMeshAsset()->GetRefSkeleton().GetParentIndex(boneIndex);
        if (parentIndex == INDEX_NONE) return false;
        const auto componentSpaceParent = mesh->GetBoneTransform(parentIndex, FTransform::Identity);
        selected = componentSpaceBone.GetRelativeTransform(componentSpaceParent);
    }
    else if (source.Space == 1)
    {
        selected = componentSpaceBone;
    }
    else
    {
        selected = mesh->GetBoneTransform(boneIndex, mesh->GetComponentTransform());
    }
    return MatrixValues(selected, matrix);
}

auto FLibraSkeletalPoseRuntime::Publish(liberafx::SpawnableHost& host,
    AActor* owner) -> liberafx::Result
{
    for (auto& source : Sources)
    {
        auto matrix = std::array<double, 16>{};
        const auto valid = TryCapture(source, owner, matrix);
        auto matrixName = Utf8(source.MatrixPort);
        auto validityName = Utf8(source.ValidityPort);
        auto result = host.setGameProperty(matrixName.c_str(), matrix.data(), matrix.size());
        if (result != liberafx::Result::Ok) return result;
        const auto validity = valid ? 1.0 : 0.0;
        result = host.setGameProperty(validityName.c_str(), &validity, 1);
        if (result != liberafx::Result::Ok) return result;
    }
    return liberafx::Result::Ok;
}

void FLibraSkeletalPoseRuntime::AddTickPrerequisites(UActorComponent& component) const
{
    check(IsInGameThread());
    for (const auto& prerequisite : TickPrerequisites)
        if (auto* target = prerequisite.Get())
            component.AddTickPrerequisiteComponent(target);
}

void FLibraSkeletalPoseRuntime::RemoveTickPrerequisites(UActorComponent& component) const
{
    check(IsInGameThread());
    for (const auto& prerequisite : TickPrerequisites)
        if (auto* target = prerequisite.Get())
            component.RemoveTickPrerequisiteComponent(target);
}
