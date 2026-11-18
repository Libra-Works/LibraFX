#include "LbInteropInternal.h"

using namespace liberafx::interop_detail;

LibraResult LIBRA_INTEROP_CALL libra_instance_create(LibraProgram program, LibraInstance *outInstance) {
  if (!outInstance)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outInstance = 0;
  return guarded([&]() -> LibraResult {
    auto found = programs.find(program);
    if (found == programs.end())
    {
      return LIBRA_INVALID_HANDLE;
    }
    auto native = std::make_unique<NativeInstance>();
    native->owner = found->second;
    const auto &core = native->owner->program;
    native->work = decltype(native->work){AlignedAllocator<double>{core.workAlignment()}};
    native->defaults = decltype(native->defaults){AlignedAllocator<double>{core.defaultsAlignment()}};
    native->work.resize((core.workBytes() + sizeof(double) - 1) / sizeof(double));
    native->defaults.resize(core.parameterValueCount());
    if ((!native->work.empty() &&
         reinterpret_cast<std::uintptr_t>(native->work.data()) % native->work.get_allocator().alignment != 0) ||
        (!native->defaults.empty() &&
         reinterpret_cast<std::uintptr_t>(native->defaults.data()) % native->defaults.get_allocator().alignment != 0))
    {
      return LIBRA_INTERNAL_ERROR;
    }
    native->values.resize(core.nodeCount());
    native->parameters.resize(core.parameterValueCount());
    auto result = native->instance.initialize(core, native->work.data(), native->work.size(), native->defaults.data(),
                                              native->defaults.size());
    if (result != liberafx::Result::Ok)
    {
      return toLibraResult(result);
    }
    if (!nextHandle)
    {
      return LIBRA_INSUFFICIENT_WORK;
    }
    auto handle = nextHandle++;
    instances.emplace(handle, std::move(native));
    *outInstance = handle;
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_instance_release(LibraInstance instance) {
  return guarded([&]() -> LibraResult { return instances.erase(instance) ? LIBRA_OK : LIBRA_INVALID_HANDLE; });
}
LibraResult LIBRA_INTEROP_CALL libra_instance_set_time_context(LibraInstance instance, double effectTime,
                                                               double frameDelta) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    return native ? toLibraResult(native->instance.setTimeContext(effectTime, frameDelta)) : LIBRA_INVALID_HANDLE;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_instance_set_quality(LibraInstance instance, uint8_t availability, uint8_t tier,
                                                          uint8_t source, uint64_t frameSerial) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    auto snapshot = liberafx::HostQualitySnapshot{static_cast<liberafx::HostQualityAvailability>(availability),
                                                  static_cast<liberafx::HostQualityTier>(tier),
                                                  static_cast<liberafx::HostQualitySource>(source), frameSerial};
    return native ? toLibraResult(native->instance.setHostQuality(snapshot)) : LIBRA_INVALID_HANDLE;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_bind_value(LibraInstance instance, const uint8_t nodeId[16],
                                                LibraBinding *outBinding) {
  if (!nodeId || !outBinding)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outBinding = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    auto handle = liberafx::ValueHandle{};
    auto result = native->instance.bindValue(nodeId, handle);
    if (result != liberafx::Result::Ok)
    {
      return toLibraResult(result);
    }
    for (auto i = std::size_t{}; i < native->valueCount; ++i)
    {
      if (native->values[i].nodeIndex == handle.nodeIndex)
      {
        *outBinding = static_cast<LibraBinding>(i + 1);
        return LIBRA_OK;
      }
    }
    if (native->valueCount == native->values.size())
    {
      return LIBRA_INSUFFICIENT_WORK;
    }
    native->values[native->valueCount++] = handle;
    *outBinding = static_cast<LibraBinding>(native->valueCount);
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_bind_parameter(LibraInstance instance, const uint8_t nodeId[16],
                                                    uint32_t inputSlot, uint32_t component, LibraBinding *outBinding) {
  if (!nodeId || !outBinding || inputSlot > 3 || component > 3)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outBinding = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    auto handle = liberafx::ParameterHandle{};
    auto result = native->instance.bindParameter(nodeId, static_cast<uint8_t>(inputSlot),
                                                 static_cast<uint8_t>(component), handle);
    if (result != liberafx::Result::Ok)
    {
      return toLibraResult(result);
    }
    for (auto i = std::size_t{}; i < native->parameterCount; ++i)
    {
      const auto &other = native->parameters[i];
      if (other.nodeIndex == handle.nodeIndex && other.inputSlot == inputSlot && other.component == component)
      {
        *outBinding = static_cast<LibraBinding>(i + 1);
        return LIBRA_OK;
      }
    }
    if (native->parameterCount == native->parameters.size())
    {
      return LIBRA_INSUFFICIENT_WORK;
    }
    native->parameters[native->parameterCount++] = handle;
    *outBinding = static_cast<LibraBinding>(native->parameterCount);
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_value_type(LibraInstance instance, LibraBinding binding, LibraValueType *outType) {
  if (!outType)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outType = LIBRA_VALUE_TYPE_UNKNOWN;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->valueCount)
    {
      return LIBRA_INVALID_HANDLE;
    }
    auto type = liberafx::ValueType::None;
    auto result = native->owner->program.valueType(native->values[binding - 1].nodeIndex, type);
    if (result != liberafx::Result::Ok)
    {
      return toLibraResult(result);
    }
    *outType = static_cast<LibraValueType>(type);
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_type(LibraInstance instance, LibraBinding binding,
                                                    LibraValueType *outType) {
  if (!outType)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outType = LIBRA_VALUE_TYPE_UNKNOWN;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return parameterValueType(*native, native->parameters[binding - 1], *outType);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_value_read_int32(LibraInstance instance, LibraBinding binding, int32_t *outValue) {
  if (!outValue)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outValue = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->valueCount)
    {
      return LIBRA_INVALID_HANDLE;
    }
    auto value = std::int32_t{};
    auto result = native->instance.valueInt32(native->values[binding - 1], value);
    if (result == liberafx::Result::Ok)
    {
      *outValue = value;
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_get_int32(LibraInstance instance, LibraBinding binding,
                                                         int32_t *outValue) {
  if (!outValue)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outValue = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
    {
      return LIBRA_INVALID_HANDLE;
    }
    auto type = LIBRA_VALUE_TYPE_UNKNOWN;
    auto result = parameterValueType(*native, native->parameters[binding - 1], type);
    if (result != LIBRA_OK)
    {
      return result;
    }
    if (type != LIBRA_VALUE_TYPE_INT32)
    {
      return LIBRA_INVALID_VALUE_TYPE;
    }
    auto value = 0.0;
    result = toLibraResult(native->instance.parameter(native->parameters[binding - 1], value));
    if (result != LIBRA_OK)
    {
      return result;
    }
    if (!std::isfinite(value))
    {
      return LIBRA_NON_FINITE;
    }
    if (std::trunc(value) != value || value < std::numeric_limits<std::int32_t>::min() ||
        value > std::numeric_limits<std::int32_t>::max())
    {
      return LIBRA_INVALID_VALUE_TYPE;
    }
    *outValue = static_cast<std::int32_t>(value);
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_set_int32(LibraInstance instance, LibraBinding binding, int32_t value) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return toLibraResult(native->instance.setParameterInt32(native->parameters[binding - 1], value));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_get(LibraInstance instance, LibraBinding binding, double *outValue) {
  if (!outValue)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return toLibraResult(native->instance.parameter(native->parameters[binding - 1], *outValue));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_set(LibraInstance instance, LibraBinding binding, double value) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
    {
      return LIBRA_INVALID_HANDLE;
    }
    return toLibraResult(native->instance.setParameter(native->parameters[binding - 1], value));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_values_read(LibraInstance instance, const LibraBinding *bindings, uint32_t count,
                                                 LibraValue *outValues) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    auto result = checkValues(*native, bindings, count, outValues);
    return result == LIBRA_OK ? readValues(*native, bindings, count, outValues) : result;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_evaluate(LibraInstance instance, double time, const LibraBinding *bindings,
                                              uint32_t count, LibraValue *outValues) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
#if defined(_WIN32)
    if (native->executionDebug)
    {
      return LIBRA_BUSY;
    }
#endif
    if (native->lifecycle)
    {
      return LIBRA_NOT_READY;
    }
    auto result = checkValues(*native, bindings, count, outValues);
    if (result != LIBRA_OK)
    {
      return result;
    }
    auto update = native->instance.updateAsync(time, inlineEnqueue, nullptr);
    if (update != liberafx::Result::Ok)
    {
      return toLibraResult(update);
    }
    native->rawStarted = true;
    update = native->instance.result();
    if (update == liberafx::Result::Ok)
    {
      update = native->instance.dispatchGeneratedSignals(10000);
    }
    return update == liberafx::Result::Ok ? readValues(*native, bindings, count, outValues) : toLibraResult(update);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_debug_attach(LibraInstance instance, const char *invitation) {
#if defined(_WIN32)
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!invitation)
    {
      return LIBRA_INVALID_ARGUMENT;
    }
    if (native->lifecycle || native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    if (native->executionDebug)
    {
      return LIBRA_BUSY;
    }
    auto &bytes = native->owner->bytes;
    auto candidate = std::make_unique<liberafx::host::WindowsExecutionDebugWorker>(
        native->instance, liberafx::host::ExecutionDebugProtocol::programIdentity(bytes.data(), bytes.size()));
    auto result = candidate->start(invitation);
    if (result == liberafx::Result::Ok)
    {
      native->executionDebug = std::move(candidate);
    }
    return toLibraResult(result);
  });
#else
  (void)instance;
  (void)invitation;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}
LibraResult LIBRA_INTEROP_CALL libra_debug_schedule(LibraInstance instance, double time) {
#if defined(_WIN32)
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->executionDebug || native->lifecycle || native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    auto result = native->executionDebug->schedule(time);
    if (result == liberafx::Result::Ok)
    {
      native->rawStarted = true;
    }
    return toLibraResult(result);
  });
#else
  (void)instance;
  (void)time;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}
LibraResult LIBRA_INTEROP_CALL libra_debug_finish(LibraInstance instance, const LibraBinding *bindings, uint32_t count,
                                                  LibraValue *outValues) {
#if defined(_WIN32)
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->executionDebug)
    {
      return LIBRA_NOT_READY;
    }
    auto validated = checkValues(*native, bindings, count, outValues);
    if (validated != LIBRA_OK)
    {
      return validated;
    }
    auto result = native->executionDebug->finish();
    return result == liberafx::Result::Ok ? readValues(*native, bindings, count, outValues) : toLibraResult(result);
  });
#else
  (void)instance;
  (void)bindings;
  (void)count;
  (void)outValues;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}
LibraResult LIBRA_INTEROP_CALL libra_debug_detach(LibraInstance instance) {
#if defined(_WIN32)
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    native->executionDebug.reset();
    return LIBRA_OK;
  });
#else
  (void)instance;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}
