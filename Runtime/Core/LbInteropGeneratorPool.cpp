#include "LbInteropInternal.h"

using namespace liberafx::interop_detail;

LibraResult LIBRA_INTEROP_CALL libra_generator_pool_create(
    LibraProgram program, const uint8_t containerId[16], uint32_t maxActive,
    uint64_t memoryBudgetBytes, uint32_t eventCapacity,
    LibraGeneratorPool *outPool) {
  return libra_generator_pool_create_ex(program, containerId, maxActive,
                                        memoryBudgetBytes, eventCapacity, 16,
                                        outPool);
}
LibraResult LIBRA_INTEROP_CALL libra_generator_pool_create_ex(
    LibraProgram program, const uint8_t containerId[16], uint32_t maxActive,
    uint64_t memoryBudgetBytes, uint32_t eventCapacity,
    uint32_t maxCaptureLanes, LibraGeneratorPool *outPool) {
  if (!outPool || !containerId ||
      memoryBudgetBytes > std::numeric_limits<std::size_t>::max())
    return LIBRA_INVALID_ARGUMENT;
  *outPool = 0;
  return guarded([&]() -> LibraResult {
    auto found = programs.find(program);
    if (found == programs.end())
      return LIBRA_INVALID_HANDLE;
    auto native = std::make_shared<NativeGeneratorPool>();
    native->owner = found->second;
    std::memcpy(native->containerId.data(), containerId, 16);
    auto result = liberafx::GeneratorPool::createWithCaptureLimit(
        native->owner->program, containerId, maxActive,
        static_cast<std::size_t>(memoryBudgetBytes), eventCapacity,
        maxCaptureLanes, native->pool);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    if (!nextHandle)
      return LIBRA_INSUFFICIENT_WORK;
    auto handle = nextHandle++;
    generatorPools.emplace(handle, std::move(native));
    *outPool = handle;
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_generator_pool_release(LibraGeneratorPool pool) {
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    if (found->second.use_count() != 1)
      return LIBRA_BUSY;
    generatorPools.erase(found);
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_spawn(
    LibraGeneratorPool pool, double time, const LibraGeneratorCapture *captures,
    uint32_t count, LibraGeneratorInstance *outInstance) {
  if (!outInstance || count > 256 || (count && !captures))
    return LIBRA_INVALID_ARGUMENT;
  *outInstance = 0;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto converted = std::array<liberafx::GeneratorCapture, 256>{};
    for (auto i = 0u; i < count; ++i) {
      if (captures[i].inputSlot > 3 || captures[i].component > 3)
        return LIBRA_INVALID_ARGUMENT;
      std::memcpy(converted[i].nodeId, captures[i].nodeId, 16);
      converted[i].inputSlot = static_cast<std::uint8_t>(captures[i].inputSlot);
      converted[i].component = static_cast<std::uint8_t>(captures[i].component);
      converted[i].value = captures[i].value;
    }
    return toLibraResult(found->second->pool->spawn(time, converted.data(),
                                                    count, *outInstance));
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_generator_stop(LibraGeneratorPool pool, LibraGeneratorInstance instance) {
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(found->second->pool->stop(instance));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_enqueue_spawn(
    LibraGeneratorPool pool, double time, const LibraGeneratorCapture *captures,
    uint32_t count) {
  if (count > 16 || (count && !captures))
    return LIBRA_INVALID_ARGUMENT;
  return libra_generator_enqueue_spawn_ex(pool, time, captures, count);
}
LibraResult LIBRA_INTEROP_CALL libra_generator_enqueue_spawn_ex(
    LibraGeneratorPool pool, double time, const LibraGeneratorCapture *captures,
    uint32_t count) {
  if (count > 256 || (count && !captures))
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto converted = std::array<liberafx::GeneratorCapture, 256>{};
    for (auto i = 0u; i < count; ++i) {
      if (captures[i].inputSlot > 3 || captures[i].component > 3)
        return LIBRA_INVALID_ARGUMENT;
      std::memcpy(converted[i].nodeId, captures[i].nodeId, 16);
      converted[i].inputSlot = static_cast<std::uint8_t>(captures[i].inputSlot);
      converted[i].component = static_cast<std::uint8_t>(captures[i].component);
      converted[i].value = captures[i].value;
    }
    return toLibraResult(
        found->second->pool->enqueueSpawn(time, converted.data(), count));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_configure_lifecycle(
    LibraGeneratorPool pool, const LibraGeneratorLifecycle *lifecycle) {
  if (!lifecycle ||
      (lifecycle->flags & ~(LIBRA_LIFECYCLE_ON_START | LIBRA_LIFECYCLE_LIFE |
                            LIBRA_LIFECYCLE_DEFERRED_SIGNALS)))
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto config = liberafx::GeneratorLifecycle{};
    std::memcpy(config.onStartNodeId, lifecycle->onStartNodeId, 16);
    std::memcpy(config.lifeNodeId, lifecycle->lifeNodeId, 16);
    config.hasOnStart = (lifecycle->flags & LIBRA_LIFECYCLE_ON_START) != 0;
    config.hasLife = (lifecycle->flags & LIBRA_LIFECYCLE_LIFE) != 0;
    config.enableDeferredSignals =
        (lifecycle->flags & LIBRA_LIFECYCLE_DEFERRED_SIGNALS) != 0;
    config.instructionBudget = lifecycle->instructionBudget;
    return toLibraResult(found->second->pool->configureLifecycle(config));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_start(
    LibraGeneratorPool pool, LibraGeneratorInstance instance) {
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(found->second->pool->start(instance));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_finalize(
    LibraGeneratorPool pool, LibraGeneratorInstance instance) {
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(found->second->pool->finalize(instance));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_process_spawns(
    LibraGeneratorPool pool, uint32_t maxCommands, uint32_t *processed) {
  if (!processed)
    return LIBRA_INVALID_ARGUMENT;
  *processed = 0;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto completed = std::size_t{};
    auto result = found->second->pool->processSpawns(maxCommands, completed);
    *processed = static_cast<uint32_t>(completed);
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_reset(LibraGeneratorPool pool) {
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(found->second->pool->reset());
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_set_live(
    LibraGeneratorPool pool, LibraGeneratorInstance instance,
    const LibraGeneratorCapture *values, uint32_t count) {
  if (count > 256 || (count && !values))
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto converted = std::array<liberafx::GeneratorCapture, 256>{};
    for (auto i = 0u; i < count; ++i) {
      if (values[i].inputSlot > 3 || values[i].component > 3)
        return LIBRA_INVALID_ARGUMENT;
      std::memcpy(converted[i].nodeId, values[i].nodeId, 16);
      converted[i].inputSlot = static_cast<std::uint8_t>(values[i].inputSlot);
      converted[i].component = static_cast<std::uint8_t>(values[i].component);
      converted[i].value = values[i].value;
    }
    return toLibraResult(
        found->second->pool->setLive(instance, converted.data(), count));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_set_captured(
    LibraGeneratorPool pool, LibraGeneratorInstance instance,
    const LibraGeneratorCapture *values, uint32_t count) {
  if (count > 256 || (count && !values))
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto converted = std::array<liberafx::GeneratorCapture, 256>{};
    for (auto i = 0u; i < count; ++i) {
      if (values[i].inputSlot > 3 || values[i].component > 3)
        return LIBRA_INVALID_ARGUMENT;
      std::memcpy(converted[i].nodeId, values[i].nodeId, 16);
      converted[i].inputSlot = static_cast<std::uint8_t>(values[i].inputSlot);
      converted[i].component = static_cast<std::uint8_t>(values[i].component);
      converted[i].value = values[i].value;
    }
    return toLibraResult(
        found->second->pool->setCaptured(instance, converted.data(), count));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_set_signal_outputs(
    LibraGeneratorPool pool, const uint8_t *nodeIds, uint32_t count) {
  if (count && !nodeIds)
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(
                     found->second->pool->setSignalOutputs(nodeIds, count));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_dispatch_signal(
    LibraGeneratorPool pool, const uint8_t nodeId[16], double time,
    uint32_t instructionBudget) {
  if (!nodeId)
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(found->second->pool->dispatchSignal(
                     nodeId, time, instructionBudget));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_next_signal_event(
    LibraGeneratorPool pool, LibraGeneratorSignalEvent *outEvent,
    uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
    return LIBRA_INVALID_ARGUMENT;
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto event = liberafx::GeneratorSignalEvent{};
    if (!found->second->pool->popSignalEvent(event))
      return LIBRA_OK;
    std::memcpy(outEvent->containerId, event.containerId, 16);
    outEvent->instanceId = event.instanceId;
    std::memcpy(outEvent->nodeId, event.nodeId, 16);
    outEvent->time = event.time;
    outEvent->seed = event.seed;
    *hasEvent = 1;
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_set_time_context(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double effectTime,
    double frameDelta) {
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(found->second->pool->setTimeContext(
                     instance, effectTime, frameDelta));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_evaluate(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double time) {
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(found->second->pool->evaluate(instance, time));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_next_deferred_signal(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double *outTime,
    uint32_t *hasSignal) {
  if (!outTime || !hasSignal) {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outTime = 0;
  *hasSignal = 0;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end()) {
      return LIBRA_INVALID_HANDLE;
    }
    auto has = false;
    auto result =
        found->second->pool->nextDeferredSignal(instance, *outTime, has);
    if (result == liberafx::Result::Ok) {
      *hasSignal = has ? 1u : 0u;
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_dispatch_deferred_signal(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double time,
    uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(found->second->pool->dispatchDeferredSignal(
                     instance, time, instructionBudget));
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_generator_elapsed(LibraGeneratorPool pool,
                        LibraGeneratorInstance instance, double *outElapsed) {
  if (!outElapsed) {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outElapsed = 0;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(
                     found->second->pool->elapsed(instance, *outElapsed));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_owner_destroy(
    LibraGeneratorPool pool, LibraGeneratorInstance instance,
    const uint8_t nodeId[16], double time, uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    return found == generatorPools.end()
               ? LIBRA_INVALID_HANDLE
               : toLibraResult(found->second->pool->ownerDestroy(
                     instance, nodeId, time, instructionBudget));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_lifetime(
    LibraGeneratorPool pool, LibraGeneratorInstance instance,
    LibraLifetimeSample *outSample) {
  if (!outSample)
    return LIBRA_INVALID_ARGUMENT;
  *outSample = {};
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto sample = liberafx::LifetimeSample{};
    auto result = found->second->pool->lifetime(instance, sample);
    if (result == liberafx::Result::Ok)
      *outSample = {sample.age, sample.remaining, sample.normalizedAge,
                    sample.expired ? 1u : 0u,
                    std::isinf(sample.remaining) ? 1u : 0u};
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_generator_value(LibraGeneratorPool pool, LibraGeneratorInstance instance,
                      const uint8_t nodeId[16], LibraValue *outValue) {
  if (!nodeId || !outValue)
    return LIBRA_INVALID_ARGUMENT;
  *outValue = {};
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto lanes = std::array<double, 4>{};
    auto width = std::uint8_t{};
    auto result =
        found->second->pool->value(instance, nodeId, lanes.data(), width);
    if (result == liberafx::Result::Ok)
      *outValue = {lanes[0], lanes[1], lanes[2], lanes[3], width, 0};
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_generator_next_event(LibraGeneratorPool pool,
                           LibraGeneratorEvent *outEvent, uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
    return LIBRA_INVALID_ARGUMENT;
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto found = generatorPools.find(pool);
    if (found == generatorPools.end())
      return LIBRA_INVALID_HANDLE;
    auto event = liberafx::GeneratorEvent{};
    if (found->second->pool->popEvent(event)) {
      outEvent->kind = static_cast<uint32_t>(event.kind);
      std::memcpy(outEvent->containerId, event.containerId, 16);
      outEvent->instanceId = event.instanceId;
      outEvent->startTime = event.startTime;
      outEvent->seed = event.seed;
      *hasEvent = 1;
    }
    return LIBRA_OK;
  });
}
