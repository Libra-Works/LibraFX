#pragma once

#include "LibraFxGodotFlexiblePlane.h"
#include "LibraFxGodotProcedural.h"
#include "LibraFxGodotGpu.h"
#include "LibraFxGodotRenderServer.h"
#include "liberafx/LbSpawnableHost.h"
#include "liberafx/LbParticleAtlas.h"
#include "liberafx/LbParticleSprite.h"
#include <godot_cpp/variant/packed_float32_array.hpp>
#include "liberafx/LbSceneBindings.h"
#include <godot_cpp/classes/mesh.hpp>
#include <godot_cpp/classes/node3d.hpp>
#include <godot_cpp/classes/material.hpp>
#include <godot_cpp/classes/shader_material.hpp>
#include <godot_cpp/classes/quad_mesh.hpp>
#include <godot_cpp/classes/standard_material3d.hpp>
#include <godot_cpp/classes/texture2d.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/rid.hpp>
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
class PackageRenderRenderer;

class LibraRenderScopeNode final : public godot::Node3D {
    GDCLASS(LibraRenderScopeNode, godot::Node3D)
public:
    void setRenderer(PackageRenderRenderer* renderer);
    void _notification(int what);
protected:
    static void _bind_methods() {}
private:
    PackageRenderRenderer* renderer_ = nullptr;
};

using MeshBindings = std::unordered_map<std::string, godot::Ref<godot::Mesh>>;
using TextureBindings = std::unordered_map<std::string, godot::Ref<godot::Texture2D>>;
using MaterialBindings = std::unordered_map<std::string, godot::Ref<godot::Material>>;

// CPU package renderer. Engine assets are supplied explicitly by the Godot caller.
class PackageRenderRenderer final {
public:
    static auto create(const SpawnablePackage& package, const MeshBindings& meshBindings,
                       const TextureBindings& textureBindings, const MaterialBindings& materialBindings,
                       std::unique_ptr<PackageRenderRenderer>& out, PackageGpuRenderer* gpu = nullptr) -> Result;
    ~PackageRenderRenderer();

    auto attach(godot::Node& owner, const CoordinateBasis& coordinates = {}) -> Result;
    auto advance(SpawnableHost& host, double effectTime, PackageGpuRenderer* gpu,
        godot::String& gpuError) -> Result;
    // Debug facades retain their own lifecycle event stream. Call handleEvent
    // for each acquired event, then updateValues only after the worker is idle.
    auto handleEvent(const SpawnableHostEvent& event, PackageGpuRenderer* gpu,
        godot::String& gpuError) -> Result;
    auto updateValues(SpawnableHost& host, double effectTime) -> Result;
    auto renderStats() const -> godot::Dictionary;
    void synchronizePresentation(bool exiting = false);
    void setPreviewOffset(const godot::Vector3& offset);
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
        std::vector<GeneratedGpuDrawTexture> generatedTextures;
    };
    struct RenderInstance final {
        godot::Ref<godot::Mesh> mesh;
        std::unique_ptr<FlexiblePlaneRenderer> flexiblePlane;
        godot::Ref<godot::QuadMesh> quad;
        godot::Ref<godot::StandardMaterial3D> material;
        std::unordered_map<std::int32_t, godot::Ref<godot::ShaderMaterial>> shaderMaterials;
        OwnedRenderingRid instance;
        godot::Transform3D localTransform;
        bool submitted = false;
        bool modelBatchCandidate = false;
        bool batched = false;
    };
    struct CpuParticleDraw final {
        bool hasSpriteAnimation = false;
        ParticleSpriteAnimation spriteAnimation;
        ParticleAtlas atlas;
        std::vector<std::array<std::uint8_t, 16>> drawRows;
        std::array<std::uint8_t, 16> nodeId{};
        std::string nodeName;
        std::string assetIdentity;
        godot::Ref<godot::Texture2D> texture;
        bool additive = false;
        double drawScale = 1;
    };
    struct ModelBatchMember final {
        RenderInstance* renderable = nullptr;
        godot::Node3D* scope = nullptr;
        bool enabled = false;
    };
    struct ModelBatch final {
        godot::Ref<godot::Mesh> mesh;
        godot::PackedFloat32Array buffer;
        std::vector<ModelBatchMember> members;
        std::vector<godot::Transform3D> transforms;
        std::vector<godot::Transform3D> previous;
        std::uint32_t capacity = 0;
        bool uploaded = false;
        OwnedRenderingRid multiMesh;
        OwnedRenderingRid instance;
    };
    struct CpuParticleRenderInstance final {
        godot::Ref<godot::ShaderMaterial> spriteMaterial;
        std::size_t instanceStride = 16;
        godot::Ref<godot::QuadMesh> quad;
        godot::Ref<godot::StandardMaterial3D> material;
        godot::PackedFloat32Array buffer;
        std::uint32_t capacity = 0;
        std::uint32_t activeCount = 0;
        godot::Basis boundsBasis;
        godot::Transform3D drawWorld;
        bool hasDrawWorld = false;
        bool boundsValid = false;
        OwnedRenderingRid multiMesh;
        OwnedRenderingRid instance;
    };
    struct TemplateDraws final {
        std::array<std::uint8_t, 16> containerId{};
        std::vector<Draw> draws;
        std::vector<CpuParticleDraw> cpuParticles;
    };
    struct VisualScope final {
        VisualScope() = default;
        VisualScope(const VisualScope&) = delete;
        auto operator=(const VisualScope&) -> VisualScope& = delete;
        VisualScope(VisualScope&&) noexcept = default;
        auto operator=(VisualScope&&) noexcept -> VisualScope& = default;

        std::array<std::uint8_t, 16> containerId{};
        std::uint64_t instanceId = 0;
        double startTime = 0;
        godot::Node3D* node = nullptr;
        std::vector<RenderInstance> renderables;
        std::vector<CpuParticleRenderInstance> cpuRenderables;
        flexible_adapter::HistoryScope flexibleHistory;
        double lastFlexibleTime = 0;
        bool hasFlexibleTime = false;
        bool flexibleHidden = false;
        bool visibleBeforeFlexibleFailure = true;
    };

    PackageGpuRenderer* gpu_ = nullptr;
    std::unique_ptr<ProceduralPackageRenderer> procedural_;
    PackageRenderRenderer(const MeshBindings& meshBindings,
                          const TextureBindings& textureBindings);

    static auto readDraws(const std::vector<std::uint8_t>& bytes,
                          const MeshBindings& meshBindings,
                          const TextureBindings& textureBindings,
                          const MaterialBindings& materialBindings,
                          std::vector<Draw>& out,
                          std::vector<CpuParticleDraw>* cpuParticles = nullptr,
                          const std::vector<GeneratedGpuDrawTexture>* generated = nullptr) -> Result;
    static auto applyMaterials(godot::Ref<godot::Mesh>& mesh, const Draw& draw,
                               RenderInstance& out) -> Result;
    auto updateMaterials(SpawnableHost& host, const Draw& draw, const RenderInstance& renderable,
                         const VisualScope* child) const -> Result;
    static auto parseId(const godot::String& text, std::array<std::uint8_t, 16>& id) -> bool;
    static auto uuidString(const std::uint8_t* bytes) -> godot::String;
    auto makeRenderable(godot::Node3D& parent, const Draw& draw,
                        RenderInstance& out, const std::array<std::uint8_t, 16>& container = {},
                        std::uint64_t instance = 0) -> Result;
    auto makeCpuRenderable(godot::Node3D& parent, const CpuParticleDraw& draw,
                           CpuParticleRenderInstance& out) -> Result;
    auto makeScope(godot::Node& parent, const std::vector<Draw>& draws,
                   godot::Node3D*& node, std::vector<RenderInstance>& renderables,
                   const std::array<std::uint8_t, 16>& container, std::uint64_t instance) -> Result;
    auto updateScope(SpawnableHost& host, const std::vector<Draw>& draws,
                     std::vector<RenderInstance>& renderables,
                     const VisualScope* child, double localTime,
                     flexible_adapter::HistoryScope& flexibleHistory,
                     bool& flexibleHidden, bool& visibleBeforeFlexibleFailure) -> Result;
    auto updateCpuParticles(SpawnableHost& host, const SpawnableScope& scope,
        const std::vector<CpuParticleDraw>& draws,
        std::vector<CpuParticleRenderInstance>& renderables, godot::Node3D& node) -> Result;
    static auto updateCpuBounds(CpuParticleRenderInstance& renderable,
                                const godot::Basis& basis) -> bool;
    static void updateInstance(const OwnedRenderingRid& instance, godot::Node3D& scope,
                              const godot::Transform3D& localTransform, bool visible = true,
                              bool exiting = false);
    auto currentFlexibleCamera() const -> godot::Camera3D*;
    void synchronizeScope(godot::Node3D& scope, std::vector<RenderInstance>& renderables,
                          bool enabled, bool exiting = false);
    void synchronizeCpuScope(godot::Node3D& scope,
        std::vector<CpuParticleRenderInstance>& renderables, bool enabled, bool exiting);
    void synchronizeModelBatches(bool exiting);
    static auto opaqueBatchable(const godot::Ref<godot::Mesh>& mesh) -> bool;
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
    LibraRenderScopeNode* root_ = nullptr;
    godot::Transform3D coordinateFrame_;
    std::vector<RenderInstance> rootRenderables_;
    std::vector<CpuParticleRenderInstance> rootCpuRenderables_;
    std::vector<VisualScope> children_;
    std::vector<ModelBatch> modelBatches_;
    flexible_adapter::HistoryScope rootFlexibleHistory_;
    double rootLastFlexibleTime_ = 0;
    bool rootHasFlexibleTime_ = false;
    bool rootFlexibleHidden_ = false;
    bool rootVisibleBeforeFlexibleFailure_ = true;
    bool ownerVisible_ = true;
    std::uint64_t cpuBufferUploads_ = 0;
    std::uint64_t cpuActiveRecords_ = 0;
    std::uint64_t cpuRecordsTransferred_ = 0;
    std::uint64_t cpuBytesTransferred_ = 0;
};

}
