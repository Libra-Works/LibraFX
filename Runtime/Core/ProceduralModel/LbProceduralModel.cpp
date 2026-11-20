#include "ProceduralModelInternal.h"
#include <atomic>
#include <chrono>
#include <mutex>
#include <new>
#include <string>
#include <unordered_map>

namespace liberafx::procedural_model {

static auto cacheMutex = std::mutex{};
static auto definitions = std::unordered_map<std::string, std::weak_ptr<const Definition>>{};
static auto plans = std::unordered_map<std::string, std::weak_ptr<const Plan>>{};
static auto nextId = std::atomic<std::uint64_t>{1};
constexpr auto maximumCachedDefinitions = std::size_t{256};

template<typename T>
static void keyAppend(std::string& key, const T& value)
{
    key.append(reinterpret_cast<const char*>(&value), sizeof(value));
}

template<typename T>
static void prune(std::unordered_map<std::string, std::weak_ptr<const T>>& cache)
{
    if (cache.size() < maximumCachedDefinitions)
    {
        return;
    }
    for (auto item = cache.begin(); item != cache.end();)
    {
        if (item->second.expired())
        {
            item = cache.erase(item);
        }
        else
        {
            ++item;
        }
    }
}

static auto topologyKey(const LibraProceduralModelDescriptor& d,
    const LibraProceduralModelModifier* modifiers, std::uint32_t count) -> std::string
{
    auto key = std::string{};
    keyAppend(key, d.shape);
    keyAppend(key, d.segmentsU);
    keyAppend(key, d.segmentsV);
    keyAppend(key, d.flags & LIBRA_PROCEDURAL_CAPPED);
    for (auto i = 0u; i < count; ++i)
    {
        const auto& m = modifiers[i];
        keyAppend(key, m.kind);
        keyAppend(key, m.flags & (LIBRA_MOD_ENABLED | LIBRA_MOD_MIRROR_KEEP_ORIGINAL));
        keyAppend(key, m.count);
    }
    return key;
}

static auto usesTime(const LibraProceduralModelModifier* modifiers, std::size_t count) -> bool
{
    for (auto i = std::size_t{}; i < count; ++i)
    {
        const auto& m = modifiers[i];
        if ((m.flags & LIBRA_MOD_ENABLED) != 0 &&
            (m.kind == LIBRA_MOD_WAVE || m.kind == LIBRA_MOD_NOISE) && m.values[0] != 0 && m.values[3] != 0)
        {
            return true;
        }
    }
    return false;
}

static auto geometryBytes(const Geometry& geometry) -> std::uint64_t
{
    return geometry.vertices.capacity() * sizeof(LibraProceduralModelVertex) +
        geometry.indices.capacity() * sizeof(std::uint32_t);
}

static auto definitionBytes(const Definition& definition) -> std::uint64_t
{
    auto bytes = definition.plan->retainedBytes + definition.modifiers.capacity() * sizeof(LibraProceduralModelModifier);
    if (definition.baseGeometry)
    {
        bytes += geometryBytes(*definition.baseGeometry);
    }
    if (definition.prefix && definition.prefix != definition.baseGeometry && definition.prefix != definition.initial)
    {
        bytes += geometryBytes(*definition.prefix);
    }
    if (definition.initial != definition.baseGeometry)
    {
        bytes += geometryBytes(*definition.initial);
    }
    return bytes;
}

static auto obtainDefinition(const LibraProceduralModelDescriptor& descriptor,
    const LibraProceduralModelModifier* modifiers, std::uint32_t count,
    std::shared_ptr<const Definition>& result, bool& topologyBuilt, bool& geometryBuilt) -> LibraResult
{
    auto key = std::string{};
    keyAppend(key, descriptor);
    for (auto i = 0u; i < count; ++i)
    {
        keyAppend(key, modifiers[i]);
    }
    {
        const auto lock = std::lock_guard<std::mutex>{cacheMutex};
        const auto found = definitions.find(key);
        if (found != definitions.end())
        {
            result = found->second.lock();
            if (result)
            {
                return LIBRA_OK;
            }
        }
    }
    auto plan = std::shared_ptr<const Plan>{};
    const auto planKey = topologyKey(descriptor, modifiers, count);
    {
        const auto lock = std::lock_guard<std::mutex>{cacheMutex};
        const auto found = plans.find(planKey);
        if (found != plans.end())
        {
            plan = found->second.lock();
        }
    }
    if (!plan)
    {
        auto created = std::make_shared<Plan>();
        auto status = buildBase(descriptor, *created);
        if (status != LIBRA_OK)
        {
            return status;
        }
        status = compileStages(modifiers, count, *created);
        if (status != LIBRA_OK)
        {
            return status;
        }
        const auto lock = std::lock_guard<std::mutex>{cacheMutex};
        prune(plans);
        const auto found = plans.find(planKey);
        plan = found == plans.end() ? nullptr : found->second.lock();
        if (!plan)
        {
            plan = created;
            topologyBuilt = true;
            if (plans.size() < maximumCachedDefinitions)
            {
                plans[planKey] = plan;
            }
        }
    }
    auto definition = std::make_shared<Definition>();
    definition->descriptor = descriptor;
    if (count != 0)
    {
        definition->modifiers.assign(modifiers, modifiers + count);
    }
    definition->plan = plan;
    definition->usesTime = usesTime(modifiers, count);
    auto work = EvaluationWork{};
    auto initial = std::make_shared<Geometry>();
    allocateWork(*plan, work, *initial);
    auto base = std::make_shared<Geometry>();
    geometryBuilt = true;
    auto status = generateBase(descriptor, *plan, work, *base);
    if (status != LIBRA_OK)
    {
        return status;
    }
    definition->baseGeometry = base;
    if ((descriptor.flags & LIBRA_PROCEDURAL_ANIMATED_BASE) == 0)
    {
        while (definition->staticPrefix < plan->stages.size())
        {
            const auto& m = modifiers[plan->stages[definition->staticPrefix].modifierIndex];
            if ((m.flags & LIBRA_MOD_ANIMATED) != 0 ||
                ((m.kind == LIBRA_MOD_WAVE || m.kind == LIBRA_MOD_NOISE) && m.values[3] != 0))
            {
                break;
            }
            ++definition->staticPrefix;
        }
        if (definition->staticPrefix == 0)
        {
            definition->prefix = base;
        }
        else if (definition->staticPrefix < plan->stages.size())
        {
            auto prefix = std::make_shared<Geometry>();
            const auto& topology = plan->stages[definition->staticPrefix - 1].output;
            prefix->vertices.reserve(topology->vertexCount);
            prefix->indices.reserve(topology->indices.size());
            status = evaluate(*definition, descriptor, modifiers, 0, work, *prefix, definition->staticPrefix);
            if (status != LIBRA_OK)
            {
                return status;
            }
            definition->prefix = prefix;
        }
    }
    if (plan->stages.empty())
    {
        initial = base;
    }
    else
    {
        status = evaluate(*definition, descriptor, modifiers, 0, work, *initial);
        if (status != LIBRA_OK)
        {
            return status;
        }
    }
    definition->initial = initial;
    if ((descriptor.flags & LIBRA_PROCEDURAL_ANIMATED_BASE) == 0 && definition->staticPrefix == plan->stages.size())
    {
        definition->prefix = initial;
    }
    definition->id = nextId.fetch_add(1, std::memory_order_relaxed);
    if (definitionBytes(*definition) > LIBRA_PROCEDURAL_MAX_WORK_BYTES)
    {
        return LIBRA_INSUFFICIENT_WORK;
    }
    {
        const auto lock = std::lock_guard<std::mutex>{cacheMutex};
        prune(definitions);
        const auto found = definitions.find(key);
        result = found == definitions.end() ? nullptr : found->second.lock();
        if (!result)
        {
            result = definition;
            if (definitions.size() < maximumCachedDefinitions)
            {
                definitions[key] = result;
            }
        }
    }
    return LIBRA_OK;
}

static void initializeInstance(LibraProceduralModelInstance& instance, std::shared_ptr<const Definition> definition)
{
    instance.definition = std::move(definition);
    instance.sharedGeometry = instance.definition->initial;
    instance.resolvedPivot = instance.sharedGeometry->pivot;
    instance.previousDescriptor = instance.definition->descriptor;
    instance.previousModifiers = instance.definition->modifiers;
    instance.stats.sharedDefinitionBytes = definitionBytes(*instance.definition);
    if ((instance.previousDescriptor.flags & LIBRA_PROCEDURAL_ANIMATED) != 0)
    {
        allocateWork(*instance.definition->plan, instance.work, instance.published);
        allocateWork(*instance.definition->plan, instance.work, instance.staging);
    }
    instance.stats.retainedBytes = sizeof(instance) + instance.previousModifiers.capacity() * sizeof(LibraProceduralModelModifier) +
        geometryBytes(instance.published) + geometryBytes(instance.staging);
    for (const auto& vertices : instance.work.vertices)
    {
        instance.stats.retainedBytes += vertices.capacity() * sizeof(LibraProceduralModelVertex);
    }
    instance.stats.retainedBytes += (instance.work.normalSums.capacity() + instance.work.tangentSums.capacity() +
        instance.work.bitangentSums.capacity()) * sizeof(Vec3);
}

static auto geometry(const LibraProceduralModelInstance& instance) -> const Geometry&
{
    return instance.ownsGeometry ? instance.published : *instance.sharedGeometry;
}

static auto hasRootPivotDeformation(const std::vector<LibraProceduralModelModifier>& modifiers) -> bool
{
    for (const auto& m : modifiers)
    {
        if ((m.flags & LIBRA_MOD_ENABLED) != 0 && (m.flags & LIBRA_MOD_CUSTOM_PIVOT) == 0 &&
            m.kind != LIBRA_MOD_WAVE && m.kind != LIBRA_MOD_INFLATE && m.kind != LIBRA_MOD_SUBDIVIDE &&
            m.kind != LIBRA_MOD_SOLIDIFY)
        {
            return true;
        }
    }
    return false;
}

static auto samePivotValues(const LibraProceduralModelDescriptor& a, const LibraProceduralModelDescriptor& b) -> bool
{
    return a.pivotMode == b.pivotMode && a.pivotVertex == b.pivotVertex && std::memcmp(a.pivot, b.pivot, sizeof(a.pivot)) == 0;
}

static auto compatibleValues(const Definition& definition, const LibraProceduralModelDescriptor& descriptor,
    const LibraProceduralModelModifier* modifiers) -> bool
{
    if ((descriptor.flags & LIBRA_PROCEDURAL_ANIMATED_BASE) == 0 &&
        (!sameBaseValues(definition.descriptor, descriptor) ||
            (!samePivotValues(definition.descriptor, descriptor) && hasRootPivotDeformation(definition.modifiers))))
    {
        return false;
    }
    for (auto i = std::size_t{}; i < definition.modifiers.size(); ++i)
    {
        const auto& m = definition.modifiers[i];
        if ((m.flags & LIBRA_MOD_ANIMATED) == 0 &&
            (m.seed != modifiers[i].seed || std::memcmp(m.values, modifiers[i].values, sizeof(m.values)) != 0))
        {
            return false;
        }
    }
    return true;
}

}

extern "C" {

LibraResult LIBRA_INTEROP_CALL libra_procedural_model_predict(const LibraProceduralModelDescriptor* descriptor,
    const LibraProceduralModelModifier* modifiers, uint32_t count, LibraProceduralModelCounts* counts)
{
    if (!descriptor || !counts)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    auto result = LibraProceduralModelCounts{};
    const auto status = liberafx::procedural_model::predict(*descriptor, modifiers, count, result);
    if (status == LIBRA_OK)
    {
        *counts = result;
    }
    return status;
}

LibraResult LIBRA_INTEROP_CALL libra_procedural_model_create(const LibraProceduralModelDescriptor* descriptor,
    const LibraProceduralModelModifier* modifiers, uint32_t count, LibraProceduralModelInstance** instance)
{
    if (!descriptor || !instance)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    auto counts = LibraProceduralModelCounts{};
    const auto status = liberafx::procedural_model::predict(*descriptor, modifiers, count, counts);
    if (status != LIBRA_OK)
    {
        return status;
    }
    try
    {
        auto definition = std::shared_ptr<const liberafx::procedural_model::Definition>{};
        auto built = false;
        auto geometryBuilt = false;
        const auto created = liberafx::procedural_model::obtainDefinition(*descriptor, modifiers, count, definition, built, geometryBuilt);
        if (created != LIBRA_OK)
        {
            return created;
        }
        auto result = std::make_unique<LibraProceduralModelInstance>();
        liberafx::procedural_model::initializeInstance(*result, std::move(definition));
        result->stats.topologyBuilds = built ? 1 : 0;
        result->stats.vertexEvaluations = geometryBuilt ? 1 : 0;
        *instance = result.release();
        return LIBRA_OK;
    }
    catch (const std::bad_alloc&)
    {
        return LIBRA_INSUFFICIENT_WORK;
    }
    catch (...)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
}

LibraResult LIBRA_INTEROP_CALL libra_procedural_model_clone(const LibraProceduralModelInstance* source,
    LibraProceduralModelInstance** instance)
{
    if (!source || !instance)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    try
    {
        auto result = std::make_unique<LibraProceduralModelInstance>();
        liberafx::procedural_model::initializeInstance(*result, source->definition);
        *instance = result.release();
        return LIBRA_OK;
    }
    catch (const std::bad_alloc&)
    {
        return LIBRA_INSUFFICIENT_WORK;
    }
    catch (...)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
}

LibraResult LIBRA_INTEROP_CALL libra_procedural_model_evaluate(LibraProceduralModelInstance* instance,
    const LibraProceduralModelDescriptor* descriptor, const LibraProceduralModelModifier* modifiers,
    uint32_t count, double time, uint32_t* changed)
{
    using namespace liberafx::procedural_model;
    if (!instance || !descriptor || !changed || !std::isfinite(time) || std::abs(time) > 1.e9)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    const auto status = validate(*descriptor, modifiers, count);
    if (status != LIBRA_OK || !sameTopology(instance->definition->descriptor, *descriptor,
        instance->definition->modifiers, modifiers, count) || !compatibleValues(*instance->definition, *descriptor, modifiers))
    {
        return status == LIBRA_OK ? LIBRA_INVALID_ARGUMENT : status;
    }
    auto modifierChanged = false;
    for (auto i = 0u; i < count; ++i)
    {
        modifierChanged |= std::memcmp(&instance->previousModifiers[i], &modifiers[i], sizeof(modifiers[i])) != 0;
    }
    const auto baseChanged = !sameBaseValues(instance->previousDescriptor, *descriptor);
    const auto pivotChanged = !samePivotValues(instance->previousDescriptor, *descriptor);
    const auto timeChanged = time != instance->previousTime && usesTime(modifiers, count);
    const auto geometryChanged = baseChanged || modifierChanged || timeChanged ||
        (pivotChanged && hasRootPivotDeformation(instance->definition->modifiers));
    if (!geometryChanged)
    {
        if (pivotChanged)
        {
            const auto& base = *instance->definition->baseGeometry;
            auto pivot = Vec3{};
            const auto resolved = resolvePivot(*descriptor, base.vertices, base.bounds, pivot);
            if (resolved != LIBRA_OK)
            {
                return resolved;
            }
            instance->resolvedPivot = pivot;
        }
        instance->previousDescriptor = *descriptor;
        instance->previousTime = time;
        ++instance->stats.skippedEvaluations;
        *changed = pivotChanged ? LIBRA_PROCEDURAL_CHANGED_PIVOT : 0;
        return LIBRA_OK;
    }
    if ((descriptor->flags & LIBRA_PROCEDURAL_ANIMATED) == 0)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    const auto started = std::chrono::steady_clock::now();
    const auto evaluated = evaluate(*instance->definition, *descriptor, modifiers, time, instance->work, instance->staging);
    if (evaluated != LIBRA_OK)
    {
        return evaluated;
    }
    const auto& previous = geometry(*instance);
    const auto indexChanged = previous.indexSignature != instance->staging.indexSignature;
    const auto actualPivotChanged = instance->resolvedPivot.x != instance->staging.pivot.x ||
        instance->resolvedPivot.y != instance->staging.pivot.y || instance->resolvedPivot.z != instance->staging.pivot.z;
    instance->resolvedPivot = instance->staging.pivot;
    std::swap(instance->published, instance->staging);
    instance->ownsGeometry = true;
    instance->previousDescriptor = *descriptor;
    if (count != 0)
    {
        std::copy_n(modifiers, count, instance->previousModifiers.begin());
    }
    instance->previousTime = time;
    ++instance->vertexRevision;
    instance->indexRevision += indexChanged ? 1 : 0;
    ++instance->stats.vertexEvaluations;
    instance->stats.lastEvaluationMicroseconds = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - started).count();
    instance->stats.maximumEvaluationMicroseconds = std::max(instance->stats.maximumEvaluationMicroseconds, instance->stats.lastEvaluationMicroseconds);
    *changed = LIBRA_PROCEDURAL_CHANGED_VERTICES | LIBRA_PROCEDURAL_CHANGED_BOUNDS |
        (indexChanged ? LIBRA_PROCEDURAL_CHANGED_INDICES : 0) |
        (actualPivotChanged ? LIBRA_PROCEDURAL_CHANGED_PIVOT : 0);
    return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_procedural_model_view(const LibraProceduralModelInstance* instance,
    LibraProceduralModelView* view)
{
    if (!instance || !view)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    const auto& data = liberafx::procedural_model::geometry(*instance);
    auto result = LibraProceduralModelView{};
    result.vertices = data.vertices.data();
    result.indices = data.indices.data();
    result.vertexCount = static_cast<std::uint32_t>(data.vertices.size());
    result.indexCount = static_cast<std::uint32_t>(data.indices.size());
    result.definitionId = instance->definition->id;
    result.vertexRevision = instance->vertexRevision;
    result.indexRevision = instance->indexRevision;
    for (auto axis = 0u; axis < 3; ++axis)
    {
        result.boundsMin[axis] = data.bounds.minimum[axis];
        result.boundsMax[axis] = data.bounds.maximum[axis];
        result.pivot[axis] = instance->resolvedPivot[axis];
    }
    *view = result;
    return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_procedural_model_stats(const LibraProceduralModelInstance* instance,
    LibraProceduralModelStats* stats)
{
    if (!instance || !stats)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    *stats = instance->stats;
    return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_procedural_model_copy_vertices(const LibraProceduralModelInstance* instance,
    LibraProceduralModelVertex* vertices, uint32_t capacity)
{
    if (!instance || !vertices)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    const auto& data = liberafx::procedural_model::geometry(*instance);
    if (capacity < data.vertices.size())
    {
        return LIBRA_INSUFFICIENT_WORK;
    }
    std::copy(data.vertices.begin(), data.vertices.end(), vertices);
    return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_procedural_model_copy_indices(const LibraProceduralModelInstance* instance,
    uint32_t* indices, uint32_t capacity)
{
    if (!instance || !indices)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    const auto& data = liberafx::procedural_model::geometry(*instance);
    if (capacity < data.indices.size())
    {
        return LIBRA_INSUFFICIENT_WORK;
    }
    std::copy(data.indices.begin(), data.indices.end(), indices);
    return LIBRA_OK;
}

void LIBRA_INTEROP_CALL libra_procedural_model_release(LibraProceduralModelInstance* instance)
{
    delete instance;
}

}
