#include "LbInteropInternal.h"

using namespace liberafx::interop_detail;

LibraResult LIBRA_INTEROP_CALL libra_effect_start(LibraInstance instance, double duration, uint32_t infinite,
                                                  const uint8_t onStartNodeId[16], uint32_t instructionBudget,
                                                  LibraLifetimeSample *outSample) {
  if (!outSample || infinite > 1 || (onStartNodeId && !instructionBudget))
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  auto lifetime = liberafx::Lifetime::infinite();
  if (!infinite && liberafx::Lifetime::finite(duration, lifetime) != liberafx::Result::Ok)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outSample = {};
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (native->rawStarted || native->gameIoStarted)
    {
      return LIBRA_NOT_READY;
    }
    if (!native->lifecycle)
    {
      if (native->gameIo)
      {
        native->lifecycle.emplace(native->instance, *native->gameIo);
      } else
      {
        native->lifecycle.emplace(native->instance);
      }
    }
    auto sample = liberafx::LifetimeSample{};
    auto result = native->lifecycle->start(lifetime, onStartNodeId, instructionBudget, sample);
    if (result == liberafx::Result::Ok)
    {
      *outSample = {sample.age, sample.remaining, sample.normalizedAge, sample.expired ? 1u : 0u,
                    std::isinf(sample.remaining) ? 1u : 0u};
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_start_captured(LibraInstance instance, const uint8_t lifeNodeId[16],
                                                           const uint8_t onStartNodeId[16], uint32_t instructionBudget,
                                                           LibraLifetimeSample *outSample) {
  if (!outSample || !lifeNodeId || (onStartNodeId && !instructionBudget))
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outSample = {};
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (native->rawStarted || native->gameIoStarted)
    {
      return LIBRA_NOT_READY;
    }
    if (!native->lifecycle)
    {
      if (native->gameIo)
      {
        native->lifecycle.emplace(native->instance, *native->gameIo);
      } else
      {
        native->lifecycle.emplace(native->instance);
      }
    }
    auto sample = liberafx::LifetimeSample{};
    auto result = native->lifecycle->startCaptured(lifeNodeId, onStartNodeId, instructionBudget, sample);
    if (result == liberafx::Result::Ok)
    {
      *outSample = {sample.age, sample.remaining, sample.normalizedAge, sample.expired ? 1u : 0u,
                    std::isinf(sample.remaining) ? 1u : 0u};
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_advance(LibraInstance instance, double elapsed,
                                                    LibraLifetimeSample *outSample) {
  if (!outSample)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outSample = {};
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->lifecycle)
    {
      return LIBRA_NOT_READY;
    }
    auto sample = liberafx::LifetimeSample{};
    auto result = native->lifecycle->advance(elapsed, sample);
    if (result == liberafx::Result::Ok)
    {
      *outSample = {sample.age, sample.remaining, sample.normalizedAge, sample.expired ? 1u : 0u,
                    std::isinf(sample.remaining) ? 1u : 0u};
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_drain(LibraInstance instance, double elapsed) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return native->lifecycle ? toLibraResult(native->lifecycle->drain(elapsed)) : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_schedule(LibraInstance instance, double time, uint64_t token) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return native->lifecycle ? toLibraResult(native->lifecycle->schedule(time, token)) : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_next_scheduled(LibraInstance instance, LibraScheduledEvent *outEvent,
                                                           uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->lifecycle)
    {
      return LIBRA_NOT_READY;
    }
    auto event = liberafx::ScheduledEvent{};
    auto has = false;
    auto result = native->lifecycle->nextScheduled(event, has);
    if (result == liberafx::Result::Ok && has)
    {
      *outEvent = {event.time, event.token};
      *hasEvent = 1;
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_take_scheduled(LibraInstance instance, LibraScheduledEvent *outEvent,
                                                           uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->lifecycle)
    {
      return LIBRA_NOT_READY;
    }
    auto event = liberafx::ScheduledEvent{};
    auto has = false;
    auto result = native->lifecycle->takeScheduled(event, has);
    if (result == liberafx::Result::Ok && has)
    {
      *outEvent = {event.time, event.token};
      *hasEvent = 1;
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_next_deferred_signal(LibraInstance instance, double *outTime,
                                                                 uint32_t *hasSignal) {
  if (!outTime || !hasSignal)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outTime = 0;
  *hasSignal = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->lifecycle)
    {
      return LIBRA_NOT_READY;
    }
    auto time = double{};
    auto has = false;
    auto result = native->lifecycle->nextDeferredSignal(time, has);
    if (result == liberafx::Result::Ok && has)
    {
      *outTime = time;
      *hasSignal = 1;
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_dispatch_deferred_signal(LibraInstance instance, double time,
                                                                     uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return native->lifecycle ? toLibraResult(native->lifecycle->dispatchDeferredSignal(time, instructionBudget))
                             : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_request_natural_end(LibraInstance instance) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return native->lifecycle ? toLibraResult(native->lifecycle->requestNaturalEnd()) : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_destroy(LibraInstance instance, const uint8_t nodeId[16],
                                                    uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return native->lifecycle ? toLibraResult(native->lifecycle->destroy(nodeId, instructionBudget)) : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_finish(LibraInstance instance) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return native->lifecycle ? toLibraResult(native->lifecycle->finish()) : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_stop(LibraInstance instance) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return native->lifecycle ? toLibraResult(native->lifecycle->stop()) : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_dispatch_signal(LibraInstance instance, const uint8_t nodeId[16], double time,
                                                     uint32_t instructionBudget) {
  if (!nodeId || !instructionBudget)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (native && native->lifecycle)
    {
      return LIBRA_NOT_READY;
    }
    return native ? toLibraResult(native->instance.dispatchSignal(nodeId, time, instructionBudget))
                  : LIBRA_INVALID_HANDLE;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_dispatch_signal(LibraInstance instance, const uint8_t nodeId[16],
                                                            double time, uint32_t instructionBudget) {
  if (!nodeId || !instructionBudget || instructionBudget > 10000)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->lifecycle)
    {
      return LIBRA_NOT_READY;
    }
    auto result = native->lifecycle->validateSignalTime(time);
    return result == liberafx::Result::Ok
               ? toLibraResult(native->instance.dispatchSignal(nodeId, time, instructionBudget))
               : toLibraResult(result);
  });
}
