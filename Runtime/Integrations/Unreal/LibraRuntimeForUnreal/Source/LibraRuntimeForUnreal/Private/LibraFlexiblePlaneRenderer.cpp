#include "LibraFlexiblePlaneRenderer.h"
#include "LibraFlexiblePlaneComponent.h"
#include "Components/SceneComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/Texture.h"
#include "GameFramework/Actor.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"

namespace librafx_unreal_flexible {
using namespace liberafx::flexible_adapter;

auto number(const TSharedPtr<FJsonValue>& value, double& output) -> bool
{
    return value.IsValid() && value->Type == EJson::Number && value->TryGetNumber(output) && finiteFloat(output);
}

auto integer(const TSharedPtr<FJsonObject>& object, const TCHAR* name, int32& output) -> bool
{
    auto value = 0.0;
    if (!object->HasTypedField(name, EJson::Number) || !object->TryGetNumberField(name, value) || !FMath::IsFinite(value) ||
        value < 0 || value > 4096 || FMath::FloorToDouble(value) != value)
    {
        return false;
    }
    output = static_cast<int32>(value);
    return true;
}

auto vector(const TSharedPtr<FJsonObject>& object, const TCHAR* name, int32 width, double* output) -> bool
{
    const TArray<TSharedPtr<FJsonValue>>* values = nullptr;
    if (!object->TryGetArrayField(name, values) || values->Num() != width)
    {
        return false;
    }
    for (auto index = 0; index < width; ++index)
    {
        if (!number((*values)[index], output[index]))
        {
            return false;
        }
    }
    return true;
}

auto guid(const FString& text, Id& output) -> bool
{
    auto value = FGuid{};
    if (!FGuid::ParseExact(text, EGuidFormats::DigitsWithHyphens, value) || !value.IsValid())
    {
        return false;
    }
    const auto words = std::array<uint32, 4>{value.A, value.B, value.C, value.D};
    for (auto index = 0u; index < 16; ++index)
    {
        output[index] = static_cast<uint8>(words[index / 4] >> (24 - (index % 4) * 8));
    }
    return true;
}

auto parse(const TSharedPtr<FJsonObject>& node, FLibraFlexiblePlaneDefinition& output) -> bool
{
    auto kind = FString{}, nodeId = FString{};
    if (!node.IsValid() || !node->HasTypedField(TEXT("kind"), EJson::String) ||
        !node->HasTypedField(TEXT("nodeId"), EJson::String) ||
        !node->HasTypedField(TEXT("assetPath"), EJson::String) ||
        !node->TryGetStringField(TEXT("kind"), kind) || kind != TEXT("flexiblePlane") ||
        !node->TryGetStringField(TEXT("nodeId"), nodeId) || !FGuid::Parse(nodeId, output.nodeId) ||
        !output.nodeId.IsValid() || !node->TryGetStringField(TEXT("assetPath"), output.assetPath) ||
        output.assetPath.TrimStartAndEnd().IsEmpty())
    {
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* materials = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* textures = nullptr;
    auto billboard = false;
    if (!node->TryGetArrayField(TEXT("materials"), materials) || !materials->IsEmpty() ||
        !node->TryGetArrayField(TEXT("textures"), textures) || !textures->IsEmpty() ||
        !node->HasTypedField(TEXT("billboard"), EJson::Boolean) ||
        !node->TryGetBoolField(TEXT("billboard"), billboard) || billboard)
    {
        return false;
    }
    const TSharedPtr<FJsonObject>* plane = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* inputs = nullptr;
    if (!node->TryGetObjectField(TEXT("flexiblePlane"), plane) || !plane->IsValid() ||
        !node->TryGetArrayField(TEXT("inputs"), inputs) || inputs->Num() > 6)
    {
        return false;
    }
    auto version = 0, capacity = 0;
    auto sourceKind = FString{}, source = FString{}, facing = FString{}, uv = FString{};
    auto age = 0.0, repeat = 0.0;
    auto up = std::array<double, 3>{};
    auto& definition = output.native;
    if (!integer(*plane, TEXT("version"), version) || version != 1 ||
        !integer(*plane, TEXT("capacity"), capacity) ||
        !(*plane)->HasTypedField(TEXT("sourceKind"), EJson::String) ||
        !(*plane)->HasTypedField(TEXT("sourceNodeId"), EJson::String) ||
        !(*plane)->HasTypedField(TEXT("facing"), EJson::String) ||
        !(*plane)->HasTypedField(TEXT("uvMode"), EJson::String) ||
        !(*plane)->HasTypedField(TEXT("maxAge"), EJson::Number) ||
        !(*plane)->HasTypedField(TEXT("repeatLength"), EJson::Number) ||
        !(*plane)->TryGetStringField(TEXT("sourceKind"), sourceKind) ||
        (sourceKind != TEXT("list") && sourceKind != TEXT("history")) ||
        !(*plane)->TryGetStringField(TEXT("sourceNodeId"), source) || !guid(source, definition.source) ||
        !(*plane)->TryGetStringField(TEXT("facing"), facing) ||
        (facing != TEXT("camera") && facing != TEXT("fixedUp")) ||
        !(*plane)->TryGetStringField(TEXT("uvMode"), uv) ||
        (uv != TEXT("stretch") && uv != TEXT("repeat")) ||
        !(*plane)->TryGetNumberField(TEXT("maxAge"), age) || !finiteFloat(age) ||
        !(*plane)->TryGetNumberField(TEXT("repeatLength"), repeat) || !finiteFloat(repeat) ||
        !vector(*plane, TEXT("up"), 3, up.data()))
    {
        return false;
    }
    definition.history = sourceKind == TEXT("history");
    definition.capacity = static_cast<uint32>(capacity);
    definition.maxAge = age;
    definition.options = {facing == TEXT("camera") ? 1u : 0u, uv == TEXT("repeat") ? 1u : 0u,
        static_cast<float>(up[0]), static_cast<float>(up[1]), static_cast<float>(up[2]),
        0, 0, 0, static_cast<float>(repeat)};
    const TArray<TSharedPtr<FJsonValue>>* points = nullptr;
    if (!(*plane)->TryGetArrayField(TEXT("points"), points) || points->Num() > 64)
    {
        return false;
    }
    for (const auto& value : *points)
    {
        const auto point = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto position = std::array<double, 3>{};
        auto color = std::array<double, 4>{};
        auto width = 0.0;
        if (!point || !vector(point, TEXT("position"), 3, position.data()) ||
            !vector(point, TEXT("color"), 4, color.data()) ||
            !point->HasTypedField(TEXT("width"), EJson::Number) ||
            !point->TryGetNumberField(TEXT("width"), width) || !finiteFloat(width) || width < 0)
        {
            return false;
        }
        definition.points.push_back({static_cast<float>(position[0]), static_cast<float>(position[1]),
            static_cast<float>(position[2]), static_cast<float>(width), static_cast<float>(color[0]),
            static_cast<float>(color[1]), static_cast<float>(color[2]), static_cast<float>(color[3])});
    }
    for (const auto& value : *inputs)
    {
        const auto item = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto id = FString{}, sourceText = FString{};
        auto width = 0, component = 0;
        auto parsed = Input{};
        if (!item || !item->HasTypedField(TEXT("id"), EJson::String) ||
            !item->TryGetStringField(TEXT("id"), id) ||
            !integer(item, TEXT("width"), width) || width < 1 || width > 4 ||
            !integer(item, TEXT("sourceComponent"), component) || component + width > 4 ||
            !vector(item, TEXT("defaults"), width, parsed.defaults.data()))
        {
            return false;
        }
        const auto materialValue = item->TryGetField(TEXT("materialParameter"));
        if (!materialValue.IsValid() || materialValue->Type != EJson::Null)
        {
            return false;
        }
        const auto sourceValue = item->TryGetField(TEXT("sourceNodeId"));
        if (!sourceValue.IsValid())
        {
            return false;
        }
        if (sourceValue->Type != EJson::Null)
        {
            if (sourceValue->Type != EJson::String || !sourceValue->TryGetString(sourceText) ||
                !guid(sourceText, parsed.source))
            {
                return false;
            }
            parsed.connected = true;
        }
        parsed.id = TCHAR_TO_UTF8(*id);
        parsed.width = static_cast<uint32>(width);
        parsed.component = static_cast<uint32>(component);
        definition.inputs.push_back(std::move(parsed));
    }
    return validate(definition);
}
}

FLibraFlexiblePlaneRenderer::FLibraFlexiblePlaneRenderer(const FLibraFlexiblePlaneDefinition& definition)
    : definition_(definition), geometry_(definition.native)
{
}

bool FLibraFlexiblePlaneRenderer::Prepare(const TSharedPtr<FJsonObject>& node,
    FLibraFlexiblePlaneDefinition& output, FString& error)
{
    check(IsInGameThread());
    auto definition = FLibraFlexiblePlaneDefinition{};
    if (!librafx_unreal_flexible::parse(node, definition))
    {
        error = TEXT("Invalid Flexible Plane draw descriptor.");
        return false;
    }
    output = MoveTemp(definition);
    return true;
}

bool FLibraFlexiblePlaneRenderer::Create(AActor& actor, USceneComponent& parent,
    const FLibraFlexiblePlaneDefinition& definition, UTexture* texture, UMaterialInterface* material,
    FName textureParameter, TUniquePtr<FLibraFlexiblePlaneRenderer>& output, FString& error)
{
    check(IsInGameThread());
    auto parameters = TArray<FMaterialParameterInfo>{};
    auto ids = TArray<FGuid>{};
    if (material)
    {
        material->GetAllTextureParameterInfo(parameters, ids);
    }
    const auto hasParameter = parameters.ContainsByPredicate([textureParameter](const FMaterialParameterInfo& item) {
        return item.Name == textureParameter && item.Association == GlobalParameter && item.Index == INDEX_NONE;
    });
    if (!texture || !material || textureParameter.IsNone() || !hasParameter ||
        !liberafx::flexible_adapter::validate(definition.native))
    {
        error = TEXT("Flexible Plane requires a texture and material with the named global texture parameter.");
        return false;
    }
    auto renderer = TUniquePtr<FLibraFlexiblePlaneRenderer>(new FLibraFlexiblePlaneRenderer(definition));
    auto* component = NewObject<ULibraFlexiblePlaneComponent>(&actor);
    if (!component)
    {
        error = TEXT("Could not create Flexible Plane component.");
        return false;
    }
    auto* dynamic = UMaterialInstanceDynamic::Create(material, component);
    if (!dynamic)
    {
        error = TEXT("Could not create Flexible Plane material.");
        return false;
    }
    dynamic->SetTextureParameterValue(textureParameter, texture);
    component->SetMaterial(0, dynamic);
    component->SetMobility(EComponentMobility::Movable);
    component->SetCollisionEnabled(ECollisionEnabled::NoCollision);
    component->SetCastShadow(false);
    component->SetupAttachment(&parent);
    actor.AddInstanceComponent(component);
    component->RegisterComponent();
    renderer->component_.Reset(component);
    output = MoveTemp(renderer);
    return true;
}

FLibraFlexiblePlaneRenderer::~FLibraFlexiblePlaneRenderer()
{
    check(IsInGameThread());
    if (component_.IsValid())
    {
        component_->DestroyComponent();
    }
}

bool FLibraFlexiblePlaneRenderer::Update(double time, const FVector& cameraWorld,
    const FLibraFlexiblePlaneRead& read, FLibraFlexiblePlaneHistoryScope& history, FString& error)
{
    check(IsInGameThread());
    if (!component_.IsValid() || !read || cameraWorld.ContainsNaN())
    {
        error = TEXT("Flexible Plane has no component, source reader or finite camera.");
        return false;
    }
    const auto transform = component_->GetComponentTransform();
    const auto scale = transform.GetScale3D();
    const auto determinant = scale.X * scale.Y * scale.Z;
    if (definition_.native.options.facing == 1 &&
        (scale.ContainsNaN() || !FMath::IsFinite(determinant) || FMath::Abs(determinant) < 1e-12))
    {
        error = TEXT("Flexible Plane camera transform is singular.");
        return false;
    }
    const auto camera = transform.InverseTransformPosition(cameraWorld);
    const auto reader = [&read](const liberafx::flexible_adapter::Id& id, double* values,
        std::uint8_t& width) -> LibraResult { return read(id.data(), values, width); };
    const auto result = geometry_.update(definition_.native, history, reader, time,
        static_cast<float>(camera.X), static_cast<float>(camera.Y), static_cast<float>(camera.Z));
    if (result != LIBRA_OK)
    {
        error = FString::Printf(TEXT("Flexible Plane native evaluation failed: %d."), result);
        return false;
    }
    auto vertices = TArray<FDynamicMeshVertex>{};
    auto indices = TArray<uint32>{};
    const auto counts = geometry_.counts();
    vertices.Reserve(static_cast<int32>(counts.vertices));
    indices.Reserve(static_cast<int32>(counts.indices));
    for (auto index = 0u; index < counts.vertices; ++index)
    {
        const auto& vertex = geometry_.vertices()[index];
        if (vertex.r < 0 || vertex.r > 1 || vertex.g < 0 || vertex.g > 1 ||
            vertex.b < 0 || vertex.b > 1 || vertex.a < 0 || vertex.a > 1)
        {
            error = TEXT("Unreal Flexible Plane vertex colors must be in [0,1] for the dynamic mesh.");
            return false;
        }
        const auto pair = index & ~1u;
        const auto other = pair + 2 < counts.vertices ? pair + 2 : pair - 2;
        const auto& left = geometry_.vertices()[pair];
        const auto& right = geometry_.vertices()[pair + 1];
        const auto& neighbor = geometry_.vertices()[other];
        auto tangent = FVector3f(neighbor.x - left.x, neighbor.y - left.y, neighbor.z - left.z);
        if (other < pair)
        {
            tangent = -tangent;
        }
        tangent = tangent.GetSafeNormal();
        const auto side = FVector3f(right.x - left.x, right.y - left.y, right.z - left.z).GetSafeNormal();
        auto normal = FVector3f::CrossProduct(side, tangent).GetSafeNormal();
        if (tangent.IsNearlyZero())
        {
            tangent = FVector3f(1, 0, 0);
        }
        if (normal.IsNearlyZero())
        {
            normal = FVector3f(0, 0, 1);
        }
        vertices.Emplace(FVector3f(vertex.x, vertex.y, vertex.z), tangent, normal,
            FVector2f(vertex.u, vertex.v), FLinearColor(vertex.r, vertex.g, vertex.b, vertex.a).ToFColor(false));
    }
    for (auto index = 0u; index < counts.indices; ++index)
    {
        indices.Add(geometry_.indices()[index]);
    }
    component_->SetRibbon(MoveTemp(vertices), MoveTemp(indices));
    return true;
}
