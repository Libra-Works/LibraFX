#include "LibraProceduralGeometry.h"
#include "LocalVertexFactory.h"
#include "StaticMeshResources.h"
#include "RawIndexBuffer.h"
#include "PrimitiveSceneProxy.h"
#include "PrimitiveUniformShaderParametersBuilder.h"
#include "SceneManagement.h"

struct FLibraProceduralGeometry::FState
{
    FStaticMeshVertexBuffers Vertices;
    FRawStaticIndexBuffer Indices;
    FLocalVertexFactory Factory;
    uint32 VertexCount = 0, IndexCount = 0;
    explicit FState(ERHIFeatureLevel::Type featureLevel) : Factory(featureLevel, "LibraProcedural") {}
    void Release()
    {
        Factory.ReleaseResource(); Indices.ReleaseResource();
        Vertices.PositionVertexBuffer.ReleaseResource(); Vertices.StaticMeshVertexBuffer.ReleaseResource();
        Vertices.ColorVertexBuffer.ReleaseResource();
    }
};
FLibraProceduralGeometry::FLibraProceduralGeometry(ERHIFeatureLevel::Type featureLevel)
    : State(MakeShared<FState, ESPMode::ThreadSafe>(featureLevel)) {}
FLibraProceduralGeometry::~FLibraProceduralGeometry()
{
    const auto state = State;
    if (IsInRenderingThread())
    {
        state->Release();
    }
    else
    {
        ENQUEUE_RENDER_COMMAND(LibraProceduralGeometryRelease)([state](FRHICommandListImmediate&) { state->Release(); });
    }
}
void FLibraProceduralGeometry::Submit(TArray<FDynamicMeshVertex>& vertices, TArray<uint32>& indices,
    bool indicesChanged, FRenderCommandFence& fence)
{
    const auto state = State;
    ENQUEUE_RENDER_COMMAND(LibraProceduralGeometryUpdate)([state, &vertices, &indices, indicesChanged](FRHICommandListImmediate& commandList)
    {
        if (state->VertexCount == 0)
        {
            state->VertexCount = vertices.Num(); state->IndexCount = indices.Num();
            state->Vertices.InitFromDynamicVertex(commandList, &state->Factory, vertices, 2);
            state->Indices.SetIndices(indices, EIndexBufferStride::Force32Bit);
            state->Indices.InitResource(commandList);
            return;
        }
        if (vertices.Num() != state->VertexCount || indices.Num() != state->IndexCount)
        {
            return;
        }
        for (auto i = 0; i < vertices.Num(); ++i)
        {
            const auto& vertex = vertices[i];
            state->Vertices.PositionVertexBuffer.VertexPosition(i) = vertex.Position;
            state->Vertices.StaticMeshVertexBuffer.SetVertexTangents(i, vertex.TangentX.ToFVector3f(),
                vertex.GetTangentY(), vertex.TangentZ.ToFVector3f());
            state->Vertices.StaticMeshVertexBuffer.SetVertexUV(i, 0, vertex.TextureCoordinate[0]);
            state->Vertices.StaticMeshVertexBuffer.SetVertexUV(i, 1, vertex.TextureCoordinate[1]);
            state->Vertices.ColorVertexBuffer.VertexColor(i) = vertex.Color;
        }
        const auto copy = [&commandList](const FBufferRHIRef& buffer, const void* source, uint32 bytes)
        {
            void* target = commandList.LockBuffer(buffer, 0, bytes, RLM_WriteOnly);
            FMemory::Memcpy(target, source, bytes); commandList.UnlockBuffer(buffer);
        };
        auto& buffers = state->Vertices;
        copy(buffers.PositionVertexBuffer.VertexBufferRHI, buffers.PositionVertexBuffer.GetVertexData(),
            buffers.PositionVertexBuffer.GetStride() * state->VertexCount);
        copy(buffers.StaticMeshVertexBuffer.TangentsVertexBuffer.VertexBufferRHI,
            buffers.StaticMeshVertexBuffer.GetTangentData(), buffers.StaticMeshVertexBuffer.GetTangentSize());
        copy(buffers.StaticMeshVertexBuffer.TexCoordVertexBuffer.VertexBufferRHI,
            buffers.StaticMeshVertexBuffer.GetTexCoordData(), buffers.StaticMeshVertexBuffer.GetTexCoordSize());
        copy(buffers.ColorVertexBuffer.VertexBufferRHI, buffers.ColorVertexBuffer.GetVertexData(),
            buffers.ColorVertexBuffer.GetStride() * state->VertexCount);
        if (indicesChanged)
        {
            copy(state->Indices.IndexBufferRHI, indices.GetData(), state->IndexCount * sizeof(uint32));
        }
    });
    fence.BeginFence();
}
void FLibraProceduralGeometry::Draw(const FPrimitiveSceneProxy& proxy, const FMaterialRenderProxy* material,
    int32 viewIndex, FMeshElementCollector& collector) const
{
    if (State->VertexCount == 0 || State->IndexCount == 0)
    {
        return;
    }
    auto& mesh = collector.AllocateMesh(); auto& element = mesh.Elements[0];
    mesh.VertexFactory = &State->Factory; mesh.MaterialRenderProxy = material;
    element.IndexBuffer = &State->Indices; element.FirstIndex = 0; element.NumPrimitives = State->IndexCount / 3;
    element.MinVertexIndex = 0; element.MaxVertexIndex = State->VertexCount - 1;
    auto& uniform = collector.AllocateOneFrameResource<FDynamicPrimitiveUniformBuffer>();
    auto builder = FPrimitiveUniformShaderParametersBuilder{};
    proxy.BuildUniformShaderParameters(builder); uniform.Set(collector.GetRHICommandList(), builder);
    element.PrimitiveUniformBufferResource = &uniform.UniformBuffer;
    mesh.ReverseCulling = proxy.IsLocalToWorldDeterminantNegative(); mesh.Type = PT_TriangleList;
    mesh.DepthPriorityGroup = SDPG_World; mesh.bCanApplyViewModeOverrides = false;
    collector.AddMesh(viewIndex, mesh);
}
