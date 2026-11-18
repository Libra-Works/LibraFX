#include "LibraDebugDrawScope.h"
#include "Components/SceneComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Components/TextRenderComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/StaticMesh.h"
#include "GameFramework/Actor.h"
#include "Misc/PackageName.h"

namespace {
auto makeTransform(const liberafx::DebugDrawCommand& command, FTransform& out) -> bool
{
    const auto* m = command.world;
    auto x = FVector(m[0], m[4], m[8]);
    auto y = FVector(m[1], m[5], m[9]);
    auto z = FVector(m[2], m[6], m[10]);
    auto scale = FVector(x.Size(), y.Size(), z.Size());
    if (scale.GetMin() <= SMALL_NUMBER) return false;
    x /= scale.X; y /= scale.Y; z /= scale.Z;
    if (FMath::Abs(FVector::DotProduct(x, y)) > 0.0001 ||
        FMath::Abs(FVector::DotProduct(x, z)) > 0.0001 ||
        FMath::Abs(FVector::DotProduct(y, z)) > 0.0001) return false;
    if (FVector::DotProduct(FVector::CrossProduct(x, y), z) < 0) { x = -x; scale.X = -scale.X; }
    auto rotation = FMatrix::Identity;
    rotation.SetAxes(&x, &y, &z);
    out = FTransform(FQuat(rotation), FVector(m[3], m[7], m[11]), scale * command.size);
    return !out.ContainsNaN();
}
}

FLibraDebugDrawScope::~FLibraDebugDrawScope() { Clear(); }

void FLibraDebugDrawScope::Clear()
{
    check(IsInGameThread());
    for (auto& entry : entries)
        if (entry.component.IsValid()) { entry.component->SetVisibility(false); entry.component->DestroyComponent(); }
    entries.Reset();
    queue.reset();
    storage.Reset();
    programSequence = -1;
    readValue = {};
}

bool FLibraDebugDrawScope::Configure(const FJsonObject& root, AActor& actor,
    FReadValue read, FResolveModel resolve, int64 sequence, FString& error)
{
    Clear(); // A transfer is a new complete scope, not an accumulating draw list.
    const TArray<TSharedPtr<FJsonValue>>* nodes = nullptr;
    if (!root.TryGetArrayField(TEXT("nodes"), nodes) || nodes->Num() > 128)
    { error = TEXT("Debug preview scope exceeds 128 draws."); return false; }
    auto ids = TSet<FGuid>{};
    for (const auto& value : *nodes)
    {
        auto object = value.IsValid() ? value->AsObject() : nullptr;
        auto kind = FString{};
        if (!object || !object->TryGetStringField(TEXT("kind"), kind) ||
            (kind != TEXT("debugText") && kind != TEXT("debugModel"))) continue;
        auto fail = [&]() { error = TEXT("Invalid preview-only Debug draw descriptor or unavailable input/asset."); Clear(); return false; };
        auto entry = FEntry{};
        auto idText = FString{}, text = FString{}, path = FString{};
        auto id = FGuid{};
        auto previewOnly = false;
        auto version = 0;
        auto size = 0.0;
        if (!object->TryGetBoolField(TEXT("previewOnly"), previewOnly) || !previewOnly ||
            !object->TryGetNumberField(TEXT("debugVersion"), version) || version != 1 ||
            !object->TryGetStringField(TEXT("nodeId"), idText) || !FGuid::Parse(idText, id) ||
            !id.IsValid() || ids.Contains(id) ||
            !object->TryGetNumberField(TEXT("size"), size) || !FMath::IsFinite(size) || size <= 0 || size > 10000)
            return fail();
        ids.Add(id);
        // FGuid memory order is local only; identity is never serialized from here.
        FMemory::Memcpy(entry.command.nodeId, &id, 16);
        entry.command.size = static_cast<float>(size);
        entry.command.kind = kind == TEXT("debugText") ? liberafx::DebugDrawKind::Text : liberafx::DebugDrawKind::Model;
        if (entry.command.kind == liberafx::DebugDrawKind::Text)
        {
            if (!object->TryGetStringField(TEXT("text"), text)) return fail();
            auto utf8 = FTCHARToUTF8(*text);
            if (utf8.Length() > 1024) return fail();
            for (auto index = 0; index < text.Len(); ++index) if (text[index] == 0) return fail();
            FMemory::Memcpy(entry.command.text, utf8.Get(), utf8.Length());
        }
        else
        {
            if (!object->TryGetStringField(TEXT("assetPath"), path) || path.IsEmpty())
                return fail();
            auto utf8 = FTCHARToUTF8(*path);
            if (utf8.Length() > 512) return fail();
            FMemory::Memcpy(entry.command.asset, utf8.Get(), utf8.Length());
        }
        const TArray<TSharedPtr<FJsonValue>>* inputs = nullptr;
        if (!object->TryGetArrayField(TEXT("inputs"), inputs) || inputs->Num() != 5) return fail();
        auto inputIds = TSet<FString>{};
        for (const auto& inputValue : *inputs)
        {
            auto inputObject = inputValue.IsValid() ? inputValue->AsObject() : nullptr;
            auto input = FInput{};
            const TArray<TSharedPtr<FJsonValue>>* defaults = nullptr;
            auto source = FString{};
            if (!inputObject || !inputObject->TryGetStringField(TEXT("id"), input.id) ||
                inputIds.Contains(input.id) ||
                !inputObject->TryGetNumberField(TEXT("width"), input.width) ||
                !inputObject->TryGetNumberField(TEXT("sourceComponent"), input.component) ||
                !inputObject->TryGetArrayField(TEXT("defaults"), defaults)) return fail();
            inputIds.Add(input.id);
            auto row = input.id == TEXT("row0") || input.id == TEXT("row1") ||
                input.id == TEXT("row2") || input.id == TEXT("row3");
            if ((!row && input.id != TEXT("visible")) || input.width != (row ? 4 : 1) ||
                defaults->Num() != input.width || input.component < 0 || input.component + input.width > 4)
                return fail();
            for (auto lane = 0; lane < input.width; ++lane)
            {
                auto number = 0.0;
                if (!(*defaults)[lane].IsValid() || !(*defaults)[lane]->TryGetNumber(number) ||
                    !FMath::IsFinite(number) || FMath::Abs(number) > MAX_flt) return fail();
                input.value[lane] = number;
            }
            if (inputObject->TryGetStringField(TEXT("sourceNodeId"), source) && !source.IsEmpty())
            {
                if (!FGuid::Parse(source, input.source) || !input.source.IsValid() || !read) return fail();
            }
            entry.inputs.Add(MoveTemp(input));
        }
        auto component = static_cast<USceneComponent*>(nullptr);
        if (entry.command.kind == liberafx::DebugDrawKind::Text)
        {
            auto label = NewObject<UTextRenderComponent>(&actor);
            label->SetText(FText::FromString(text)); // World component, not HUD/screen text.
            label->SetWorldSize(1);
            label->SetHorizontalAlignment(EHTA_Center);
            component = label;
        }
        else
        {
            auto mesh = resolve ? resolve(path) : nullptr;
            if (!mesh) return fail(); // No engine basic-shape substitution.
            auto model = NewObject<UStaticMeshComponent>(&actor);
            model->SetStaticMesh(mesh);
            model->SetCollisionEnabled(ECollisionEnabled::NoCollision);
            component = model;
        }
        component->SetMobility(EComponentMobility::Movable);
        component->SetVisibility(false);
        component->SetAbsolute(true, true, true);
        actor.AddInstanceComponent(component);
        component->RegisterComponent();
        entry.component.Reset(component);
        entries.Add(MoveTemp(entry));
    }
    storage.SetNum(FMath::Max(1, entries.Num()));
    if (queue.initialize(storage.GetData(), static_cast<size_t>(storage.Num())) != liberafx::RenderResult::Ok) return false;
    programSequence = sequence;
    readValue = MoveTemp(read);
    return true;
}

bool FLibraDebugDrawScope::Update(int64 sequence, FString& error)
{
    if (entries.IsEmpty()) return true;
    if (sequence != programSequence) { Clear(); return true; }
    auto fail = [&]() { error = TEXT("Debug world Matrix is non-affine, singular, sheared, nonfinite, or its source is unavailable."); Clear(); return false; };
    queue.reset();
    if (queue.begin() != liberafx::RenderResult::Ok) return fail();
    for (auto& entry : entries)
    {
        auto command = entry.command;
        for (const auto& input : entry.inputs)
        {
            auto value = input.value;
            if (input.source.IsValid())
            {
                auto source = FVector4{};
                auto width = 0;
                if (!readValue || readValue(input.source, source, width) != ELibraRuntimeResult::Ok ||
                    input.component + input.width > width) return fail();
                for (auto lane = 0; lane < input.width; ++lane) value[lane] = source[input.component + lane];
            }
            if (input.id == TEXT("visible"))
            {
                if (!FMath::IsFinite(value.X)) return fail();
                command.visible = value.X > 0;
            }
            else
            {
                auto row = input.id[3] - TEXT('0');
                for (auto lane = 0; lane < 4; ++lane) command.world[row * 4 + lane] = static_cast<float>(value[lane]);
            }
        }
        auto transform = FTransform{};
        if (!liberafx::DebugDrawQueue::valid(command) || (command.visible && !makeTransform(command, transform)) ||
            queue.append(command) != liberafx::RenderResult::Ok) return fail();
    }
    if (queue.seal() != liberafx::RenderResult::Ok) return fail();
    auto accepted = queue.submit([](void* context, liberafx::DebugDrawBatch batch) noexcept
    {
        auto& self = *static_cast<FLibraDebugDrawScope*>(context);
        // Validate the entire batch before making any component visible.
        if (batch.count != static_cast<size_t>(self.entries.Num())) return false;
        for (auto index = size_t{0}; index < batch.count; ++index)
        {
            auto transform = FTransform{};
            if (!self.entries[static_cast<int32>(index)].component.IsValid() ||
                (batch.commands[index].visible && !makeTransform(batch.commands[index], transform))) return false;
        }
        for (auto index = size_t{0}; index < batch.count; ++index)
        {
            auto transform = FTransform{};
            auto component = self.entries[static_cast<int32>(index)].component.Get();
            if (batch.commands[index].visible)
            {
                makeTransform(batch.commands[index], transform);
                component->SetWorldTransform(transform);
            }
            component->SetVisibility(batch.commands[index].visible);
        }
        return true;
    }, this);
    return accepted == liberafx::RenderResult::Ok ? true : fail();
}
