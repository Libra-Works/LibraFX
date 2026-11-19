#pragma once

#include "LibraFxGodotFlexiblePlane.h"
#include "liberafx/LbSpawnableHost.h"
#include <godot_cpp/variant/packed_float32_array.hpp>
#include "liberafx/LbSceneBindings.h"
#include <godot_cpp/classes/mesh.hpp>
#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/node3d.hpp>
#include <godot_cpp/classes/mesh_instance3d.hpp>
#include <godot_cpp/classes/material.hpp>
#include <godot_cpp/classes/shader_material.hpp>
#include <godot_cpp/classes/quad_mesh.hpp>
#include <godot_cpp/classes/standard_material3d.hpp>
#include <godot_cpp/classes/texture2d.hpp>
#include <godot_cpp/classes/multi_mesh.hpp>
#include <godot_cpp/classes/multi_mesh_instance3d.hpp>
#include <godot_cpp/classes/ref.hpp>
#include <godot_cpp/variant/string.hpp>
#include <array>
#include <cstdint>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

namespace liberafx::godot_adapter {

class PackageGpuRenderer;

using MeshBindings = std::unordered_map<std::string, godot::Ref<godot::Mesh>>;
using TextureBindings = std::unordered_map<std::string, godot::Ref<godot::Texture2D>>;
using MaterialBindings = std::unordered_map<std::string, godot::Ref<godot::Material>>;

// CPU package renderer. Engine assets are supplied explicitly by the Godot caller.
class PackageRenderRenderer final {
public:
    static auto create(const SpawnablePackage& package, const MeshBindings& meshBindings,
                       const TextureBindings& textureBindings, const MaterialBindings& materialBindings,
                       std::unique_ptr<PackageRenderRenderer>& out) -> Result;
    ~PackageRenderRenderer();

    auto attach(godot::Node& owner) -> Result;
    auto advance(SpawnableHost& host, double effectTime, PackageGpuRenderer* gpu,
        godot::String& gpuError) -> Result;
    // Debug facades retain their own lifecycle event stream. Call handleEvent
    // for each acquired event, then updateValues only after the worker is idle.
    auto handleEvent(const SpawnableHostEvent& event, PackageGpuRenderer* gpu,
        godot::String& gpuError) -> Result;
    auto updateValues(SpawnableHost& host, double effectTime) -> Result;
    // Call before an explicit non-contiguous seek; backwards time also resets automatically.
    void resetFlexibleHistories();

private:
    struct Input final {
        std::array<std::uint8_t, 16> sourceNodeId{};
        std::array<double, 4> defaults{};
        std::uint8_t width = 0;
        std::uint8_t sourceComponent = 0;
        bool connected = false;
    };
    struct SpriteCell final {
        std::array<double, 4> uv{};
        std::int32_t width = 0;
        std::int32_t height = 0;
    };
    struct SpriteFrame final {
        std::uint32_t cellIndex = 0;
        double durationFrames = 0;
    };
    struct CompositionKey final {
        double frame = 0;
        double x = 0;
        double y = 0;
        double rotationDegrees = 0;
        double scaleX = 1;
        double scaleY = 1;
    };
    struct SpriteAnimation final {
        std::uint8_t version = 0;
        std::int32_t textureWidth = 0;
        std::int32_t textureHeight = 0;
        double fps = 0;
        std::uint32_t startFrame = 0;
        std::uint32_t endFrame = 0;
        bool loop = false;
        double durationFrames = 0;
        std::int32_t drawOrder = 0;
        std::vector<SpriteCell> cells;
        std::vector<SpriteFrame> frames;
        std::vector<double> durations;
        std::vector<CompositionKey> keys;
        std::vector<liberafx::SpritePoseKey> nativeKeys;
    };
    struct Draw final {
        struct Parameter final {
            std::int32_t slot = 0;
            godot::StringName name;
            Input input{};
            godot::Ref<godot::Texture2D> texture;
            godot::Variant::Type type = godot::Variant::NIL;
        };
        std::array<std::uint8_t, 16> nodeId{};
        std::string nodeName;
        std::string assetIdentity;
        bool sprite = false;
        bool billboard = false;
        bool flexiblePlane = false;
        flexible_adapter::Definition flexibleDefinition;
        godot::Ref<godot::Texture2D> flexibleTexture;
        Input position{};
        Input rotation{};
        Input scale{};
        Input tint{};
        Input frame{};
        Input columns{};
        Input rows{};
        Input fps{};
        Input playing{};
        SpriteAnimation animation{};
        std::unordered_map<std::int32_t, godot::Ref<godot::Material>> materials;
        std::vector<Parameter> parameters;
    };
    struct RenderInstance final {
        godot::MeshInstance3D* node = nullptr;
        std::unique_ptr<FlexiblePlaneRenderer> flexiblePlane;
        godot::Ref<godot::QuadMesh> quad;
        godot::Ref<godot::StandardMaterial3D> material;
        std::unordered_map<std::int32_t, godot::Ref<godot::ShaderMaterial>> shaderMaterials;
    };
    struct CpuParticleDraw final {
        std::array<std::uint8_t, 16> nodeId{};
        std::string nodeName;
        std::string assetIdentity;
        godot::Ref<godot::Texture2D> texture;
        bool additive = false;
        double drawScale = 1;
    };
    struct CpuParticleRenderInstance final {
        godot::MultiMeshInstance3D* node = nullptr;
        godot::Ref<godot::MultiMesh> multiMesh;
        godot::PackedFloat32Array buffer;
        std::uint32_t capacity = 0;
    };
    struct TemplateDraws final {
        std::array<std::uint8_t, 16> containerId{};
        std::vector<Draw> draws;
    };
    struct VisualScope final {
        std::array<std::uint8_t, 16> containerId{};
        std::uint64_t instanceId = 0;
        double startTime = 0;
        godot::Node3D* node = nullptr;
        std::vector<RenderInstance> renderables;
        flexible_adapter::HistoryScope flexibleHistory;
        double lastFlexibleTime = 0;
        bool hasFlexibleTime = false;
        bool flexibleHidden = false;
        bool visibleBeforeFlexibleFailure = true;
    };

    PackageRenderRenderer(const MeshBindings& meshBindings,
                          const TextureBindings& textureBindings);

    static auto readDraws(const std::vector<std::uint8_t>& bytes,
                          const MeshBindings& meshBindings,
                          const TextureBindings& textureBindings,
                          const MaterialBindings& materialBindings,
                          std::vector<Draw>& out,
                          std::vector<CpuParticleDraw>* cpuParticles = nullptr) -> Result;
    static auto applyMaterials(godot::MeshInstance3D& node, const Draw& draw,
                               RenderInstance& out) -> Result;
    auto updateMaterials(SpawnableHost& host, const Draw& draw, const RenderInstance& renderable,
                         const VisualScope* child) const -> Result;
    static auto parseId(const godot::String& text, std::array<std::uint8_t, 16>& id) -> bool;
    static auto uuidString(const std::uint8_t* bytes) -> godot::String;
    auto makeRenderable(godot::Node& parent, const Draw& draw,
                        RenderInstance& out) -> Result;
    auto makeCpuRenderable(godot::Node& parent, const CpuParticleDraw& draw,
                           CpuParticleRenderInstance& out) -> Result;
    auto makeScope(godot::Node& parent, const std::vector<Draw>& draws,
                   godot::Node3D*& node, std::vector<RenderInstance>& renderables) -> Result;
    auto updateScope(SpawnableHost& host, const std::vector<Draw>& draws,
                     const std::vector<RenderInstance>& renderables,
                     const VisualScope* child, double localTime,
                     flexible_adapter::HistoryScope& flexibleHistory,
                     bool& flexibleHidden, bool& visibleBeforeFlexibleFailure) -> Result;
    auto updateCpuParticles(SpawnableHost& host) -> Result;
    auto currentFlexibleCamera() const -> godot::Camera3D*;
    auto updateInput(SpawnableHost& host, const Input& input, const VisualScope* child,
                     std::array<double, 4>& values) const -> Result;
    auto handleEvents(SpawnableHost& host, PackageGpuRenderer* gpu, godot::String& gpuError) -> Result;
    auto findTemplate(const std::uint8_t containerId[16]) const -> const TemplateDraws*;
    auto findChild(const std::uint8_t containerId[16], std::uint64_t instanceId)
        -> std::vector<VisualScope>::iterator;
    void removeChild(std::vector<VisualScope>::iterator child);
    void clear() noexcept;

    MeshBindings meshBindings_;
    TextureBindings textureBindings_;
    std::vector<Draw> rootDraws_;
    std::vector<CpuParticleDraw> rootCpuParticles_;
    std::vector<TemplateDraws> templates_;
    godot::Node3D* root_ = nullptr;
    std::vector<RenderInstance> rootRenderables_;
    std::vector<CpuParticleRenderInstance> rootCpuRenderables_;
    std::vector<VisualScope> children_;
    flexible_adapter::HistoryScope rootFlexibleHistory_;
    double rootLastFlexibleTime_ = 0;
    bool rootHasFlexibleTime_ = false;
    bool rootFlexibleHidden_ = false;
    bool rootVisibleBeforeFlexibleFailure_ = true;
};

}
