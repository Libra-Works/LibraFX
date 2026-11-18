#include "LbInteropInternal.h"

namespace liberafx::interop_detail {
static_assert(static_cast<int>(liberafx::Result::InstructionBudgetExceeded) ==
              LIBRA_INSTRUCTION_BUDGET_EXCEEDED);
static_assert(static_cast<int>(liberafx::Result::CounterOverflow) ==
              LIBRA_COUNTER_OVERFLOW);
static_assert(LIBRA_ARITHMETIC_OVERFLOW == 18);
static_assert(sizeof(LibraValue) == 40 && sizeof(LibraAtlasFrame) == 20 &&
              sizeof(LibraSpriteVertex) == 36);
static_assert(sizeof(LibraGameIoEvent) == 64 &&
              sizeof(LibraGameIoEventV2) == 160);
static_assert(sizeof(LibraSpawnableHostEvent) == 64);
static_assert(static_cast<std::uint32_t>(
                  liberafx::SpawnableHostEventKind::ForceTerminated) ==
              LIBRA_SPAWNABLE_EVENT_FORCE_TERMINATED);
static_assert(
    static_cast<std::uint32_t>(liberafx::SpawnableScopeKind::Effect) ==
        LIBRA_SPAWNABLE_SCOPE_EFFECT &&
    static_cast<std::uint32_t>(liberafx::SpawnableScopeKind::Instance) ==
        LIBRA_SPAWNABLE_SCOPE_INSTANCE);
static_assert(std::is_standard_layout<LibraValue>::value &&
              std::is_trivially_copyable<LibraValue>::value);
static_assert(std::is_standard_layout<LibraAtlasFrame>::value &&
              std::is_trivially_copyable<LibraAtlasFrame>::value);
static_assert(std::is_standard_layout<LibraSpriteVertex>::value &&
              std::is_trivially_copyable<LibraSpriteVertex>::value);
auto toLibraResult(liberafx::Result result) noexcept -> LibraResult {
  return result == liberafx::Result::ArithmeticOverflow
             ? LIBRA_ARITHMETIC_OVERFLOW
             : static_cast<LibraResult>(result);
}

auto registryMutex = std::mutex{};
auto programs =
    std::unordered_map<LibraProgram, std::shared_ptr<NativeProgram>>{};
auto instances =
    std::unordered_map<LibraInstance, std::unique_ptr<NativeInstance>>{};
auto generatorPools =
    std::unordered_map<LibraGeneratorPool,
                       std::shared_ptr<NativeGeneratorPool>>{};
auto spawnableHosts =
    std::unordered_map<LibraSpawnableHostHandle, NativeSpawnableHost>{};
auto nextHandle = std::uint64_t{1};

auto findInstance(LibraInstance handle) -> NativeInstance * {
  auto found = instances.find(handle);
  return found == instances.end() ? nullptr : found->second.get();
}
auto findSpawnableHost(LibraSpawnableHostHandle handle)
    -> liberafx::SpawnableHost * {
  auto found = spawnableHosts.find(handle);
  return found == spawnableHosts.end() ? nullptr : found->second.host.get();
}
auto ownedSpawnableHost(LibraSpawnableHostHandle handle,
                        liberafx::SpawnableHost *&out) -> LibraResult {
  auto found = spawnableHosts.find(handle);
  if (found == spawnableHosts.end())
    return LIBRA_INVALID_HANDLE;
  if (found->second.ownerThread != std::this_thread::get_id()) {
    return LIBRA_BUSY;
  }
  out = found->second.host.get();
  return LIBRA_OK;
}
auto readSpawnableScope(std::uint32_t kind, const std::uint8_t *container,
                        LibraGeneratorInstance instance,
                        liberafx::SpawnableScope &out) -> bool {
  if (kind > LIBRA_SPAWNABLE_SCOPE_INSTANCE ||
      (kind == LIBRA_SPAWNABLE_SCOPE_INSTANCE && !container))
    return false;
  out = {};
  out.kind = static_cast<liberafx::SpawnableScopeKind>(kind);
  out.instanceId = instance;
  if (container)
    std::memcpy(out.containerId, container, 16);
  return true; // Core validates zero/nonzero identities for each scope.
}
auto inlineEnqueue(void *, liberafx::Job job, void *data) noexcept -> bool {
  job(data);
  return true;
}
auto spawnSignal(liberafx::CustomNodeInvocation &call) noexcept
    -> liberafx::Result {
  auto &context = *static_cast<SpawnContext *>(call.context);
  auto captures = std::array<liberafx::GeneratorCapture, 16>{};
  auto count = std::size_t{};
  for (auto input = 0u; input < context.binding.inputCount; ++input) {
    for (auto lane = 0u; lane < context.binding.widths[input]; ++lane) {
      auto &capture = captures[count++];
      std::memcpy(capture.nodeId, context.binding.templateNodeIds[input], 16);
      capture.component = static_cast<std::uint8_t>(lane);
      capture.value = call.inputs[input * 4 + lane];
    }
  }
  call.selectedBranch = 0;
  return context.owner->pool->enqueueSpawn(call.time, captures.data(), count);
}
auto dispatchSpawnSignalInput(liberafx::CustomNodeInvocation &call) noexcept
    -> liberafx::Result {
  auto &context = *static_cast<SpawnSignalInputContext *>(call.context);
  call.selectedBranch = 0;
  return context.owner->pool->dispatchSignal(context.binding.signalNodeId,
                                             call.time, 10000);
}
auto checkValues(NativeInstance &native, const LibraBinding *bindings,
                 std::uint32_t count, LibraValue *out) -> LibraResult {
  if (count && (!bindings || !out))
    return LIBRA_INVALID_ARGUMENT;
  for (auto i = 0u; i < count; ++i)
    if (!bindings[i] || bindings[i] > native.valueCount)
      return LIBRA_INVALID_HANDLE;
  return LIBRA_OK;
}
auto readValues(NativeInstance &native, const LibraBinding *bindings,
                std::uint32_t count, LibraValue *out) -> LibraResult {
  for (auto i = 0u; i < count; ++i) {
    alignas(16) auto lanes = std::array<double, 4>{};
    const auto &handle = native.values[bindings[i] - 1];
    auto result = native.instance.value(handle, lanes.data());
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    out[i] = {lanes[0], lanes[1], lanes[2], lanes[3], handle.componentCount, 0};
  }
  return LIBRA_OK;
}
auto parameterValueType(const NativeInstance &native,
                        const liberafx::ParameterHandle &handle,
                        LibraValueType &outType) noexcept -> LibraResult {
  outType = LIBRA_VALUE_TYPE_UNKNOWN;
  const auto &program = native.owner->program;
  if (program.version() < 10)
    return LIBRA_OK;
  const auto &bytes = native.owner->bytes;
  auto instructionCount = program.nodeCount();
  auto tableSize = std::size_t{8} + instructionCount * 8;
  if (bytes.size() < tableSize || handle.nodeIndex >= instructionCount ||
      handle.inputSlot >= 4)
    return LIBRA_INVALID_BINARY;
  auto tableOffset = bytes.size() - tableSize;
  auto table = bytes.data() + tableOffset;
  if (std::memcmp(table, "LFTY", 4) != 0 || table[4] != 1 || table[5] != 0 ||
      table[6] != 8 || table[7] != 0)
    return LIBRA_INVALID_BINARY;
  auto record = table + 8 + std::size_t{handle.nodeIndex} * 8;
  if (handle.inputSlot >= record[1])
    return LIBRA_INVALID_HANDLE;
  auto type = record[2 + handle.inputSlot];
  if (type > LIBRA_VALUE_TYPE_ENUM)
    return LIBRA_INVALID_BINARY;
  outType = static_cast<LibraValueType>(type);
  return LIBRA_OK;
}
} // namespace liberafx::interop_detail

using namespace liberafx::interop_detail;

uint32_t LIBRA_INTEROP_CALL libra_abi_version(void) { return 1; }

LibraResult LIBRA_INTEROP_CALL libra_program_create(const uint8_t *bytes,
                                                    uint64_t byteCount,
                                                    LibraProgram *outProgram) {
  if (!outProgram)
    return LIBRA_INVALID_ARGUMENT;
  *outProgram = 0;
  return guarded([&]() -> LibraResult {
    // Bound untrusted header-driven allocation before the core validates it.
    if (!bytes || byteCount < 12)
      return LIBRA_INVALID_ARGUMENT;
    if (byteCount > 256ull * 1024 * 1024 || std::memcmp(bytes, "LFXB", 4) != 0)
      return LIBRA_INVALID_BINARY;
    auto count = std::uint32_t{};
    for (auto i = 0u; i < 4; ++i)
      count |= std::uint32_t{bytes[8 + i]} << (i * 8);
    if (count > byteCount / 44)
      return LIBRA_INVALID_BINARY;
    auto native = std::make_shared<NativeProgram>();
    native->bytes.assign(bytes, bytes + static_cast<std::size_t>(byteCount));
    native->bindings.resize(count);
    auto result = liberafx::Program::load(
        native->bytes.data(), native->bytes.size(), nullptr, 0,
        native->bindings.data(), native->bindings.size(), native->program);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    if (!nextHandle)
      return LIBRA_INSUFFICIENT_WORK;
    auto handle = nextHandle++;
    programs.emplace(handle, std::move(native));
    *outProgram = handle;
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_program_release(LibraProgram program) {
  return guarded([&]() -> LibraResult {
    return programs.erase(program) ? LIBRA_OK : LIBRA_INVALID_HANDLE;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_program_create_spawnable(
    const uint8_t *bytes, uint64_t byteCount,
    const LibraSpawnableBinding *bindings, uint32_t bindingCount,
    LibraProgram *outProgram) {
  return libra_program_create_spawnable_v2(
      bytes, byteCount, bindings, bindingCount, nullptr, 0, outProgram);
}
LibraResult LIBRA_INTEROP_CALL libra_program_create_spawnable_v2(
    const uint8_t *bytes, uint64_t byteCount,
    const LibraSpawnableBinding *bindings, uint32_t bindingCount,
    const LibraSpawnableSignalInputBinding *signalInputs,
    uint32_t signalInputCount, LibraProgram *outProgram) {
  if (!outProgram || !bytes || (bindingCount && !bindings) ||
      (signalInputCount && !signalInputs) || bindingCount > 4096 ||
      signalInputCount > 4096)
    return LIBRA_INVALID_ARGUMENT;
  *outProgram = 0;
  return guarded([&]() -> LibraResult {
    if (byteCount < 16 || byteCount > 256ull * 1024 * 1024 ||
        std::memcmp(bytes, "LFXB", 4) != 0 || bytes[4] < 3 || bytes[4] > 11 ||
        bytes[5] != 0 || bytes[12] != 0 || bytes[13] != 1)
      return LIBRA_INVALID_BINARY;
    auto count = std::uint32_t{};
    for (auto i = 0u; i < 4; ++i)
      count |= std::uint32_t{bytes[8 + i]} << (i * 8);
    const auto instructionBytes = 16ull + 256ull * count;
    if (count > 10000 || byteCount < instructionBytes ||
        (bytes[4] == 3 && byteCount != instructionBytes) ||
        bindingCount + signalInputCount > count)
      return LIBRA_INVALID_BINARY;
    auto native = std::make_shared<NativeProgram>();
    native->bytes.assign(bytes, bytes + static_cast<std::size_t>(byteCount));
    native->bindings.resize(count);
    native->spawnContexts.resize(bindingCount);
    native->spawnSignalInputContexts.resize(signalInputCount);
    native->descriptors.resize(static_cast<std::size_t>(bindingCount) +
                               signalInputCount + 1);
    const auto &passthroughId = liberafx::signalPassthroughTypeId;
    auto &pass = native->descriptors.back();
    std::memcpy(pass.typeId, passthroughId.data(), 16);
    pass.abiVersion = 1;
    pass.execution = liberafx::CustomExecution::Signal;
    pass.branchCount = 1;
    pass.function = liberafx::signalPassthrough;
    for (auto i = 0u; i < bindingCount; ++i) {
      const auto &binding = bindings[i];
      if (binding.inputCount > 4 || binding.reserved ||
          std::memcmp(binding.signalTypeId, passthroughId.data(), 16) == 0)
        return LIBRA_INVALID_ARGUMENT;
      auto pool = generatorPools.find(binding.pool);
      if (pool == generatorPools.end())
        return LIBRA_INVALID_HANDLE;
      if (std::memcmp(pool->second->containerId.data(), binding.containerId,
                      16) != 0)
        return LIBRA_INVALID_ARGUMENT;
      auto matches = 0u;
      for (auto index = 0u; index < count; ++index) {
        auto record = bytes + 16 + std::size_t{index} * 256;
        if (record[16] == 41 &&
            std::memcmp(record + 24, binding.signalTypeId, 16) == 0) {
          if (std::memcmp(record, binding.containerId, 16) != 0)
            return LIBRA_INVALID_BINARY;
          ++matches;
        }
      }
      if (matches != 1)
        return LIBRA_INVALID_BINARY;
      for (auto j = 0u; j < i; ++j)
        if (std::memcmp(bindings[j].signalTypeId, binding.signalTypeId, 16) ==
                0 ||
            std::memcmp(bindings[j].containerId, binding.containerId, 16) == 0)
          return LIBRA_INVALID_ARGUMENT;
      auto &context = native->spawnContexts[i];
      context.owner = pool->second;
      context.binding = binding;
      auto &descriptor = native->descriptors[i];
      std::memcpy(descriptor.typeId, binding.signalTypeId, 16);
      descriptor.abiVersion = 1;
      descriptor.execution = liberafx::CustomExecution::Signal;
      descriptor.inputCount = static_cast<std::uint8_t>(binding.inputCount);
      descriptor.branchCount = 1;
      descriptor.function = spawnSignal;
      descriptor.context = &context;
      for (auto j = 0u; j < binding.inputCount; ++j) {
        if (binding.widths[j] < 1 || binding.widths[j] > 4 ||
            binding.colors[j] > 1 ||
            (binding.colors[j] && binding.widths[j] != 4))
          return LIBRA_INVALID_ARGUMENT;
        descriptor.inputWidths[j] = binding.widths[j];
        descriptor.inputIsColor[j] = binding.colors[j];
      }
    }
    for (auto i = 0u; i < signalInputCount; ++i) {
      const auto &binding = signalInputs[i];
      if (std::memcmp(binding.signalTypeId, passthroughId.data(), 16) == 0)
        return LIBRA_INVALID_ARGUMENT;
      auto pool = generatorPools.find(binding.pool);
      if (pool == generatorPools.end())
        return LIBRA_INVALID_HANDLE;
      if (std::memcmp(pool->second->containerId.data(), binding.containerId,
                      16) != 0)
        return LIBRA_INVALID_ARGUMENT;
      auto matches = 0u;
      for (auto index = 0u; index < count; ++index) {
        auto record = bytes + 16 + std::size_t{index} * 256;
        if (record[16] != 41 ||
            std::memcmp(record, binding.proxyNodeId, 16) != 0 ||
            std::memcmp(record + 24, binding.signalTypeId, 16) != 0)
          continue;
        ++matches;
      }
      if (matches != 1)
        return LIBRA_INVALID_BINARY;
      for (auto j = 0u; j < bindingCount; ++j)
        if (std::memcmp(bindings[j].signalTypeId, binding.signalTypeId, 16) ==
            0)
          return LIBRA_INVALID_ARGUMENT;
      for (auto j = 0u; j < i; ++j)
        if (std::memcmp(signalInputs[j].signalTypeId, binding.signalTypeId,
                        16) == 0 ||
            std::memcmp(signalInputs[j].proxyNodeId, binding.proxyNodeId, 16) ==
                0)
          return LIBRA_INVALID_ARGUMENT;
      auto &context = native->spawnSignalInputContexts[i];
      context.owner = pool->second;
      context.binding = binding;
      auto &descriptor =
          native->descriptors[static_cast<std::size_t>(bindingCount) + i];
      std::memcpy(descriptor.typeId, binding.signalTypeId, 16);
      descriptor.abiVersion = 1;
      descriptor.execution = liberafx::CustomExecution::Signal;
      descriptor.branchCount = 1;
      descriptor.function = dispatchSpawnSignalInput;
      descriptor.context = &context;
    }
    auto result = liberafx::Program::load(
        native->bytes.data(), native->bytes.size(), native->descriptors.data(),
        native->descriptors.size(), native->bindings.data(),
        native->bindings.size(), native->program);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    if (!nextHandle)
      return LIBRA_INSUFFICIENT_WORK;
    auto handle = nextHandle++;
    programs.emplace(handle, std::move(native));
    *outProgram = handle;
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_instance_create(LibraProgram program, LibraInstance *outInstance) {
  if (!outInstance)
    return LIBRA_INVALID_ARGUMENT;
  *outInstance = 0;
  return guarded([&]() -> LibraResult {
    auto found = programs.find(program);
    if (found == programs.end())
      return LIBRA_INVALID_HANDLE;
    auto native = std::make_unique<NativeInstance>();
    native->owner = found->second;
    const auto &core = native->owner->program;
    native->work =
        decltype(native->work){AlignedAllocator<double>{core.workAlignment()}};
    native->defaults = decltype(native->defaults){
        AlignedAllocator<double>{core.defaultsAlignment()}};
    native->work.resize((core.workBytes() + sizeof(double) - 1) /
                        sizeof(double));
    native->defaults.resize(core.parameterValueCount());
    if ((!native->work.empty() &&
         reinterpret_cast<std::uintptr_t>(native->work.data()) %
                 native->work.get_allocator().alignment !=
             0) ||
        (!native->defaults.empty() &&
         reinterpret_cast<std::uintptr_t>(native->defaults.data()) %
                 native->defaults.get_allocator().alignment !=
             0))
      return LIBRA_INTERNAL_ERROR;
    native->values.resize(core.nodeCount());
    native->parameters.resize(core.parameterValueCount());
    auto result = native->instance.initialize(
        core, native->work.data(), native->work.size(), native->defaults.data(),
        native->defaults.size());
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    if (!nextHandle)
      return LIBRA_INSUFFICIENT_WORK;
    auto handle = nextHandle++;
    instances.emplace(handle, std::move(native));
    *outInstance = handle;
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_instance_release(LibraInstance instance) {
  return guarded([&]() -> LibraResult {
    return instances.erase(instance) ? LIBRA_OK : LIBRA_INVALID_HANDLE;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_instance_set_time_context(
    LibraInstance instance, double effectTime, double frameDelta) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    return native ? toLibraResult(
                        native->instance.setTimeContext(effectTime, frameDelta))
                  : LIBRA_INVALID_HANDLE;
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_instance_set_quality(LibraInstance instance, uint8_t availability,
                           uint8_t tier, uint8_t source, uint64_t frameSerial) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    auto snapshot = liberafx::HostQualitySnapshot{
        static_cast<liberafx::HostQualityAvailability>(availability),
        static_cast<liberafx::HostQualityTier>(tier),
        static_cast<liberafx::HostQualitySource>(source), frameSerial};
    return native ? toLibraResult(native->instance.setHostQuality(snapshot))
                  : LIBRA_INVALID_HANDLE;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_bind_value(LibraInstance instance,
                                                const uint8_t nodeId[16],
                                                LibraBinding *outBinding) {
  if (!nodeId || !outBinding)
    return LIBRA_INVALID_ARGUMENT;
  *outBinding = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto handle = liberafx::ValueHandle{};
    auto result = native->instance.bindValue(nodeId, handle);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    for (auto i = std::size_t{}; i < native->valueCount; ++i)
      if (native->values[i].nodeIndex == handle.nodeIndex) {
        *outBinding = static_cast<LibraBinding>(i + 1);
        return LIBRA_OK;
      }
    if (native->valueCount == native->values.size())
      return LIBRA_INSUFFICIENT_WORK;
    native->values[native->valueCount++] = handle;
    *outBinding = static_cast<LibraBinding>(native->valueCount);
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_bind_parameter(LibraInstance instance,
                                                    const uint8_t nodeId[16],
                                                    uint32_t inputSlot,
                                                    uint32_t component,
                                                    LibraBinding *outBinding) {
  if (!nodeId || !outBinding || inputSlot > 3 || component > 3)
    return LIBRA_INVALID_ARGUMENT;
  *outBinding = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto handle = liberafx::ParameterHandle{};
    auto result =
        native->instance.bindParameter(nodeId, static_cast<uint8_t>(inputSlot),
                                       static_cast<uint8_t>(component), handle);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    for (auto i = std::size_t{}; i < native->parameterCount; ++i) {
      const auto &other = native->parameters[i];
      if (other.nodeIndex == handle.nodeIndex && other.inputSlot == inputSlot &&
          other.component == component) {
        *outBinding = static_cast<LibraBinding>(i + 1);
        return LIBRA_OK;
      }
    }
    if (native->parameterCount == native->parameters.size())
      return LIBRA_INSUFFICIENT_WORK;
    native->parameters[native->parameterCount++] = handle;
    *outBinding = static_cast<LibraBinding>(native->parameterCount);
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_value_type(LibraInstance instance,
                                                LibraBinding binding,
                                                LibraValueType *outType) {
  if (!outType)
    return LIBRA_INVALID_ARGUMENT;
  *outType = LIBRA_VALUE_TYPE_UNKNOWN;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->valueCount)
      return LIBRA_INVALID_HANDLE;
    auto type = liberafx::ValueType::None;
    auto result = native->owner->program.valueType(
        native->values[binding - 1].nodeIndex, type);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    *outType = static_cast<LibraValueType>(type);
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_type(LibraInstance instance,
                                                    LibraBinding binding,
                                                    LibraValueType *outType) {
  if (!outType)
    return LIBRA_INVALID_ARGUMENT;
  *outType = LIBRA_VALUE_TYPE_UNKNOWN;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
      return LIBRA_INVALID_HANDLE;
    return parameterValueType(*native, native->parameters[binding - 1],
                              *outType);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_value_read_int32(LibraInstance instance,
                                                      LibraBinding binding,
                                                      int32_t *outValue) {
  if (!outValue)
    return LIBRA_INVALID_ARGUMENT;
  *outValue = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->valueCount)
      return LIBRA_INVALID_HANDLE;
    auto value = std::int32_t{};
    auto result =
        native->instance.valueInt32(native->values[binding - 1], value);
    if (result == liberafx::Result::Ok)
      *outValue = value;
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_get_int32(LibraInstance instance,
                                                         LibraBinding binding,
                                                         int32_t *outValue) {
  if (!outValue)
    return LIBRA_INVALID_ARGUMENT;
  *outValue = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
      return LIBRA_INVALID_HANDLE;
    auto type = LIBRA_VALUE_TYPE_UNKNOWN;
    auto result =
        parameterValueType(*native, native->parameters[binding - 1], type);
    if (result != LIBRA_OK)
      return result;
    if (type != LIBRA_VALUE_TYPE_INT32)
      return LIBRA_INVALID_VALUE_TYPE;
    auto value = 0.0;
    result = toLibraResult(
        native->instance.parameter(native->parameters[binding - 1], value));
    if (result != LIBRA_OK)
      return result;
    if (!std::isfinite(value))
      return LIBRA_NON_FINITE;
    if (std::trunc(value) != value ||
        value < std::numeric_limits<std::int32_t>::min() ||
        value > std::numeric_limits<std::int32_t>::max())
      return LIBRA_INVALID_VALUE_TYPE;
    *outValue = static_cast<std::int32_t>(value);
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_set_int32(LibraInstance instance,
                                                         LibraBinding binding,
                                                         int32_t value) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
      return LIBRA_INVALID_HANDLE;
    return toLibraResult(native->instance.setParameterInt32(
        native->parameters[binding - 1], value));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_get(LibraInstance instance,
                                                   LibraBinding binding,
                                                   double *outValue) {
  if (!outValue)
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
      return LIBRA_INVALID_HANDLE;
    return toLibraResult(
        native->instance.parameter(native->parameters[binding - 1], *outValue));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_set(LibraInstance instance,
                                                   LibraBinding binding,
                                                   double value) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native || !binding || binding > native->parameterCount)
      return LIBRA_INVALID_HANDLE;
    return toLibraResult(
        native->instance.setParameter(native->parameters[binding - 1], value));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_values_read(LibraInstance instance,
                                                 const LibraBinding *bindings,
                                                 uint32_t count,
                                                 LibraValue *outValues) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto result = checkValues(*native, bindings, count, outValues);
    return result == LIBRA_OK ? readValues(*native, bindings, count, outValues)
                              : result;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_evaluate(LibraInstance instance,
                                              double time,
                                              const LibraBinding *bindings,
                                              uint32_t count,
                                              LibraValue *outValues) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
#if defined(_WIN32)
    if (native->executionDebug)
      return LIBRA_BUSY;
#endif
    if (native->lifecycle)
      return LIBRA_NOT_READY;
    auto result = checkValues(*native, bindings, count, outValues);
    if (result != LIBRA_OK)
      return result;
    auto update = native->instance.updateAsync(time, inlineEnqueue, nullptr);
    if (update != liberafx::Result::Ok)
      return toLibraResult(update);
    native->rawStarted = true;
    update = native->instance.result();
    if (update == liberafx::Result::Ok)
      update = native->instance.dispatchGeneratedSignals(10000);
    return update == liberafx::Result::Ok
               ? readValues(*native, bindings, count, outValues)
               : toLibraResult(update);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_debug_attach(LibraInstance instance,
                                                  const char *invitation) {
#if defined(_WIN32)
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!invitation)
      return LIBRA_INVALID_ARGUMENT;
    if (native->lifecycle || native->gameIo)
      return LIBRA_NOT_READY;
    if (native->executionDebug)
      return LIBRA_BUSY;
    auto &bytes = native->owner->bytes;
    auto candidate =
        std::make_unique<liberafx::host::WindowsExecutionDebugWorker>(
            native->instance,
            liberafx::host::ExecutionDebugProtocol::programIdentity(
                bytes.data(), bytes.size()));
    auto result = candidate->start(invitation);
    if (result == liberafx::Result::Ok)
      native->executionDebug = std::move(candidate);
    return toLibraResult(result);
  });
#else
  (void)instance;
  (void)invitation;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}
LibraResult LIBRA_INTEROP_CALL libra_debug_schedule(LibraInstance instance,
                                                    double time) {
#if defined(_WIN32)
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->executionDebug || native->lifecycle || native->gameIo)
      return LIBRA_NOT_READY;
    auto result = native->executionDebug->schedule(time);
    if (result == liberafx::Result::Ok)
      native->rawStarted = true;
    return toLibraResult(result);
  });
#else
  (void)instance;
  (void)time;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}
LibraResult LIBRA_INTEROP_CALL libra_debug_finish(LibraInstance instance,
                                                  const LibraBinding *bindings,
                                                  uint32_t count,
                                                  LibraValue *outValues) {
#if defined(_WIN32)
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->executionDebug)
      return LIBRA_NOT_READY;
    auto validated = checkValues(*native, bindings, count, outValues);
    if (validated != LIBRA_OK)
      return validated;
    auto result = native->executionDebug->finish();
    return result == liberafx::Result::Ok
               ? readValues(*native, bindings, count, outValues)
               : toLibraResult(result);
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
      return LIBRA_INVALID_HANDLE;
    native->executionDebug.reset();
    return LIBRA_OK;
  });
#else
  (void)instance;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}
LibraResult LIBRA_INTEROP_CALL
libra_effect_start(LibraInstance instance, double duration, uint32_t infinite,
                   const uint8_t onStartNodeId[16], uint32_t instructionBudget,
                   LibraLifetimeSample *outSample) {
  if (!outSample || infinite > 1 || (onStartNodeId && !instructionBudget))
    return LIBRA_INVALID_ARGUMENT;
  auto lifetime = liberafx::Lifetime::infinite();
  if (!infinite &&
      liberafx::Lifetime::finite(duration, lifetime) != liberafx::Result::Ok)
    return LIBRA_INVALID_ARGUMENT;
  *outSample = {};
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (native->rawStarted || native->gameIoStarted)
      return LIBRA_NOT_READY;
    if (!native->lifecycle) {
      if (native->gameIo)
        native->lifecycle.emplace(native->instance, *native->gameIo);
      else
        native->lifecycle.emplace(native->instance);
    }
    auto sample = liberafx::LifetimeSample{};
    auto result = native->lifecycle->start(lifetime, onStartNodeId,
                                           instructionBudget, sample);
    if (result == liberafx::Result::Ok)
      *outSample = {sample.age, sample.remaining, sample.normalizedAge,
                    sample.expired ? 1u : 0u,
                    std::isinf(sample.remaining) ? 1u : 0u};
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_start_captured(
    LibraInstance instance, const uint8_t lifeNodeId[16],
    const uint8_t onStartNodeId[16], uint32_t instructionBudget,
    LibraLifetimeSample *outSample) {
  if (!outSample || !lifeNodeId || (onStartNodeId && !instructionBudget))
    return LIBRA_INVALID_ARGUMENT;
  *outSample = {};
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (native->rawStarted || native->gameIoStarted)
      return LIBRA_NOT_READY;
    if (!native->lifecycle) {
      if (native->gameIo)
        native->lifecycle.emplace(native->instance, *native->gameIo);
      else
        native->lifecycle.emplace(native->instance);
    }
    auto sample = liberafx::LifetimeSample{};
    auto result = native->lifecycle->startCaptured(lifeNodeId, onStartNodeId,
                                                   instructionBudget, sample);
    if (result == liberafx::Result::Ok)
      *outSample = {sample.age, sample.remaining, sample.normalizedAge,
                    sample.expired ? 1u : 0u,
                    std::isinf(sample.remaining) ? 1u : 0u};
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_advance(
    LibraInstance instance, double elapsed, LibraLifetimeSample *outSample) {
  if (!outSample)
    return LIBRA_INVALID_ARGUMENT;
  *outSample = {};
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->lifecycle)
      return LIBRA_NOT_READY;
    auto sample = liberafx::LifetimeSample{};
    auto result = native->lifecycle->advance(elapsed, sample);
    if (result == liberafx::Result::Ok)
      *outSample = {sample.age, sample.remaining, sample.normalizedAge,
                    sample.expired ? 1u : 0u,
                    std::isinf(sample.remaining) ? 1u : 0u};
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_drain(LibraInstance instance,
                                                  double elapsed) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    return native->lifecycle ? toLibraResult(native->lifecycle->drain(elapsed))
                             : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_schedule(LibraInstance instance,
                                                     double time,
                                                     uint64_t token) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    return native->lifecycle
               ? toLibraResult(native->lifecycle->schedule(time, token))
               : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_next_scheduled(
    LibraInstance instance, LibraScheduledEvent *outEvent, uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
    return LIBRA_INVALID_ARGUMENT;
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->lifecycle)
      return LIBRA_NOT_READY;
    auto event = liberafx::ScheduledEvent{};
    auto has = false;
    auto result = native->lifecycle->nextScheduled(event, has);
    if (result == liberafx::Result::Ok && has) {
      *outEvent = {event.time, event.token};
      *hasEvent = 1;
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_take_scheduled(
    LibraInstance instance, LibraScheduledEvent *outEvent, uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
    return LIBRA_INVALID_ARGUMENT;
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->lifecycle)
      return LIBRA_NOT_READY;
    auto event = liberafx::ScheduledEvent{};
    auto has = false;
    auto result = native->lifecycle->takeScheduled(event, has);
    if (result == liberafx::Result::Ok && has) {
      *outEvent = {event.time, event.token};
      *hasEvent = 1;
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_next_deferred_signal(
    LibraInstance instance, double *outTime, uint32_t *hasSignal) {
  if (!outTime || !hasSignal)
    return LIBRA_INVALID_ARGUMENT;
  *outTime = 0;
  *hasSignal = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->lifecycle)
      return LIBRA_NOT_READY;
    auto time = double{};
    auto has = false;
    auto result = native->lifecycle->nextDeferredSignal(time, has);
    if (result == liberafx::Result::Ok && has) {
      *outTime = time;
      *hasSignal = 1;
    }
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_dispatch_deferred_signal(
    LibraInstance instance, double time, uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    return native->lifecycle
               ? toLibraResult(native->lifecycle->dispatchDeferredSignal(
                     time, instructionBudget))
               : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_effect_request_natural_end(LibraInstance instance) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    return native->lifecycle
               ? toLibraResult(native->lifecycle->requestNaturalEnd())
               : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_effect_destroy(LibraInstance instance, const uint8_t nodeId[16],
                     uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    return native->lifecycle ? toLibraResult(native->lifecycle->destroy(
                                   nodeId, instructionBudget))
                             : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_finish(LibraInstance instance) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    return native->lifecycle ? toLibraResult(native->lifecycle->finish())
                             : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_stop(LibraInstance instance) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    return native->lifecycle ? toLibraResult(native->lifecycle->stop())
                             : LIBRA_NOT_READY;
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_dispatch_signal(LibraInstance instance, const uint8_t nodeId[16],
                      double time, uint32_t instructionBudget) {
  if (!nodeId || !instructionBudget)
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (native && native->lifecycle)
      return LIBRA_NOT_READY;
    return native ? toLibraResult(native->instance.dispatchSignal(
                        nodeId, time, instructionBudget))
                  : LIBRA_INVALID_HANDLE;
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_effect_dispatch_signal(LibraInstance instance, const uint8_t nodeId[16],
                             double time, uint32_t instructionBudget) {
  if (!nodeId || !instructionBudget || instructionBudget > 10000)
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->lifecycle)
      return LIBRA_NOT_READY;
    auto result = native->lifecycle->validateSignalTime(time);
    return result == liberafx::Result::Ok
               ? toLibraResult(native->instance.dispatchSignal(
                     nodeId, time, instructionBudget))
               : toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_game_io_configure(LibraInstance instance, uint32_t eventCapacity,
                        const uint8_t containerId[16], uint64_t instanceId) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (native->lifecycle || native->gameIo ||
        native->owner->program.version() < 6 ||
        native->owner->program.version() > 11)
      return LIBRA_INVALID_VALUE_TYPE;
    auto converted = std::vector<liberafx::NamedIoPort>{};
    auto count = native->owner->program.gameIoPortCount();
    converted.reserve(count);
    for (auto index = std::size_t{}; index < count; ++index) {
      auto port = liberafx::ProgramGameIoPort{};
      if (!native->owner->program.gameIoPort(index, port) || port.kind > 4)
        return LIBRA_INVALID_BINARY;
      auto item = liberafx::NamedIoPort{};
      item.name = port.name;
      item.kind = static_cast<liberafx::NamedIoKind>(port.kind);
      std::memcpy(item.nodeId, port.nodeId, 16);
      std::memcpy(item.additionalNodeIds, port.additionalNodeIds,
                  sizeof(item.additionalNodeIds));
      item.width = port.width;
      item.isColor = port.isColor != 0;
      item.nodeCount = port.nodeCount;
      converted.push_back(item);
    }
    auto identity = liberafx::NamedIoIdentity{};
    if (containerId)
      std::memcpy(identity.containerId, containerId, 16);
    identity.instanceId = instanceId;
    auto result = liberafx::NamedIo::create(native->instance, converted.data(),
                                            converted.size(), eventCapacity,
                                            native->gameIo, identity);
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_game_io_set_property(LibraInstance instance, const char *name,
                           const double *values, uint32_t width) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->gameIo)
      return LIBRA_NOT_READY;
    return toLibraResult(native->gameIo->setProperty(name, values, width));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_set_owner_matrix(
    LibraInstance instance, const double values[16]) {
  if (!values)
    return LIBRA_INVALID_ARGUMENT;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->gameIo)
      return LIBRA_NOT_READY;
    return toLibraResult(native->gameIo->setOwnerMatrix(values, 16));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_start(
    LibraInstance instance, double time, const uint8_t onStartNodeId[16],
    uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->gameIo)
      return LIBRA_NOT_READY;
    if (native->lifecycle)
      return LIBRA_NOT_READY;
    native->gameIoStarted = true;
    return toLibraResult(
        native->gameIo->start(time, onStartNodeId, instructionBudget));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_update(LibraInstance instance,
                                                    double time) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->gameIo)
      return LIBRA_NOT_READY;
    if (native->lifecycle)
      return LIBRA_NOT_READY;
    return toLibraResult(native->gameIo->update(time));
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_game_io_fire_signal(LibraInstance instance, const char *name, double time,
                          uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->gameIo)
      return LIBRA_NOT_READY;
    if (native->lifecycle) {
      auto status = native->lifecycle->validateSignalTime(time);
      if (status != liberafx::Result::Ok)
        return toLibraResult(status);
    }
    return toLibraResult(
        native->gameIo->fireSignal(name, time, instructionBudget));
  });
}
LibraResult LIBRA_INTEROP_CALL
libra_game_io_publish_properties(LibraInstance instance) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->gameIo)
      return LIBRA_NOT_READY;
    return toLibraResult(native->gameIo->publishProperties());
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_next_event(
    LibraInstance instance, LibraGameIoEvent *outEvent, uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
    return LIBRA_INVALID_ARGUMENT;
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->gameIo)
      return LIBRA_NOT_READY;
    auto event = liberafx::NamedIoEvent{};
    if (!native->gameIo->peekEvent(event))
      return LIBRA_OK;
    if (event.width > 4)
      return LIBRA_INVALID_VALUE_TYPE;
    if (!native->gameIo->popEvent(event))
      return LIBRA_INTERNAL_ERROR;
    std::memcpy(outEvent->containerId, event.identity.containerId, 16);
    outEvent->instanceId = event.identity.instanceId;
    outEvent->portIndex = event.portIndex;
    outEvent->kind = static_cast<uint8_t>(event.kind);
    outEvent->width = event.width;
    outEvent->isColor = event.isColor ? 1 : 0;
    std::copy_n(std::begin(event.values), event.width, outEvent->values);
    *hasEvent = 1;
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_next_event_v2(
    LibraInstance instance, LibraGameIoEventV2 *outEvent, uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
    return LIBRA_INVALID_ARGUMENT;
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    if (!native->gameIo)
      return LIBRA_NOT_READY;
    auto event = liberafx::NamedIoEvent{};
    if (!native->gameIo->popEvent(event))
      return LIBRA_OK;
    std::memcpy(outEvent->containerId, event.identity.containerId, 16);
    outEvent->instanceId = event.identity.instanceId;
    outEvent->portIndex = event.portIndex;
    outEvent->kind = static_cast<uint8_t>(event.kind);
    outEvent->width = event.width;
    outEvent->isColor = event.isColor ? 1 : 0;
    std::copy(std::begin(event.values), std::end(event.values),
              outEvent->values);
    *hasEvent = 1;
    return LIBRA_OK;
  });
}
LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_atlas(
    double frame, uint32_t columns, uint32_t rows, LibraAtlasFrame *outFrame) {
  if (!outFrame)
    return LIBRA_INVALID_ARGUMENT;
  *outFrame = {};
  auto atlas = liberafx::AtlasFrame{};
  if (!liberafx::sampleSpriteAtlas(frame, columns, rows, atlas))
    return LIBRA_INVALID_ARGUMENT;
  *outFrame = {atlas.frameIndex, atlas.uvRect[0], atlas.uvRect[1],
               atlas.uvRect[2], atlas.uvRect[3]};
  return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL
libra_sample_sprite_rect(uint32_t frameIndex, float u, float v, float width,
                         float height, LibraAtlasFrame *outFrame) {
  if (!outFrame)
    return LIBRA_INVALID_ARGUMENT;
  *outFrame = {};
  auto atlas = liberafx::AtlasFrame{};
  if (!liberafx::sampleSpriteRect(frameIndex, u, v, width, height, atlas))
    return LIBRA_INVALID_ARGUMENT;
  *outFrame = {atlas.frameIndex, atlas.uvRect[0], atlas.uvRect[1],
               atlas.uvRect[2], atlas.uvRect[3]};
  return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_sequence(
    double frameInput, double elapsedSeconds, double fps,
    const double *durationFrames, uint32_t frameCount, uint32_t startFrame,
    uint32_t endFrame, uint32_t loop, uint32_t playing,
    uint32_t *outFrameIndex) {
  if (!outFrameIndex || loop > 1 || playing > 1)
    return LIBRA_INVALID_ARGUMENT;
  *outFrameIndex = 0;
  auto frame = std::uint32_t{};
  if (!liberafx::sampleSpriteSequence(frameInput, elapsedSeconds, fps,
                                      durationFrames, frameCount, startFrame,
                                      endFrame, loop != 0, playing != 0, frame))
    return LIBRA_INVALID_ARGUMENT;
  *outFrameIndex = frame;
  return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_composition(
    double elapsedSeconds, double fps, double durationFrames, uint32_t loop,
    const double *cellDurations, uint32_t frameCount, const double *keys,
    uint32_t keyCount, uint32_t *outFrameIndex, double outPose[5]) {
  if (!outFrameIndex || !outPose || loop > 1 || keyCount > 256 ||
      (keyCount && !keys))
    return LIBRA_INVALID_ARGUMENT;
  auto frame = std::uint32_t{};
  auto pose = liberafx::SpritePose{};
  if (!liberafx::sampleSpriteComposition(
          elapsedSeconds, fps, durationFrames, loop != 0, cellDurations,
          frameCount, keys, keyCount, frame, pose,
          [](const double *input, std::uint32_t index) {
            const auto *key = input + index * 6;
            return liberafx::SpritePoseKey{
                key[0], {key[1], key[2], key[3], key[4], key[5]}};
          }))
    return LIBRA_INVALID_ARGUMENT;
  *outFrameIndex = frame;
  outPose[0] = pose.x;
  outPose[1] = pose.y;
  outPose[2] = pose.rotationDegrees;
  outPose[3] = pose.scaleX;
  outPose[4] = pose.scaleY;
  return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL
libra_make_sprite_quad(const LibraAtlasFrame *frame, float width, float height,
                       LibraSpriteVertex outVertices[4]) {
  if (!frame || !outVertices)
    return LIBRA_INVALID_ARGUMENT;
  auto atlas = liberafx::AtlasFrame{
      frame->frameIndex, {frame->u, frame->v, frame->width, frame->height}};
  liberafx::RenderVertex vertices[4]{};
  if (!liberafx::makeSpriteQuad(atlas, width, height, vertices))
    return LIBRA_INVALID_ARGUMENT;
  for (auto i = 0u; i < 4; ++i) {
    const auto &vertex = vertices[i];
    outVertices[i] = {
        vertex.position[0], vertex.position[1], vertex.position[2],
        vertex.uv[0],       vertex.uv[1],       vertex.color[0],
        vertex.color[1],    vertex.color[2],    vertex.color[3]};
  }
  return LIBRA_OK;
}
