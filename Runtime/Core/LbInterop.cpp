#include "LbInteropInternal.h"
#include "liberafx/host/LbProjectNodes.h"

namespace liberafx::interop_detail {
static_assert(static_cast<int>(liberafx::Result::InstructionBudgetExceeded) == LIBRA_INSTRUCTION_BUDGET_EXCEEDED);
static_assert(static_cast<int>(liberafx::Result::CounterOverflow) == LIBRA_COUNTER_OVERFLOW);
static_assert(LIBRA_ARITHMETIC_OVERFLOW == 18);
static_assert(sizeof(LibraValue) == 40 && sizeof(LibraAtlasFrame) == 20 && sizeof(LibraSpriteVertex) == 36);
static_assert(sizeof(LibraGameIoEvent) == 64 && sizeof(LibraGameIoEventV2) == 160);
static_assert(sizeof(LibraSpawnableHostEvent) == 64);
static_assert(static_cast<std::uint32_t>(liberafx::SpawnableHostEventKind::ForceTerminated) ==
              LIBRA_SPAWNABLE_EVENT_FORCE_TERMINATED);
static_assert(static_cast<std::uint32_t>(liberafx::SpawnableScopeKind::Effect) == LIBRA_SPAWNABLE_SCOPE_EFFECT &&
              static_cast<std::uint32_t>(liberafx::SpawnableScopeKind::Instance) == LIBRA_SPAWNABLE_SCOPE_INSTANCE);
static_assert(std::is_standard_layout<LibraValue>::value && std::is_trivially_copyable<LibraValue>::value);
static_assert(std::is_standard_layout<LibraAtlasFrame>::value && std::is_trivially_copyable<LibraAtlasFrame>::value);
static_assert(std::is_standard_layout<LibraSpriteVertex>::value &&
              std::is_trivially_copyable<LibraSpriteVertex>::value);
auto toLibraResult(liberafx::Result result) noexcept -> LibraResult {
  return result == liberafx::Result::ArithmeticOverflow ? LIBRA_ARITHMETIC_OVERFLOW : static_cast<LibraResult>(result);
}

std::mutex registryMutex;
std::unordered_map<LibraProgram, std::shared_ptr<NativeProgram>> programs;
std::unordered_map<LibraInstance, std::unique_ptr<NativeInstance>> instances;
std::unordered_map<LibraGeneratorPool, std::shared_ptr<NativeGeneratorPool>> generatorPools;
std::unordered_map<LibraSpawnableHostHandle, NativeSpawnableHost> spawnableHosts;
std::uint64_t nextHandle = 1;

auto findInstance(LibraInstance handle) -> NativeInstance * {
  auto found = instances.find(handle);
  return found == instances.end() ? nullptr : found->second.get();
}
auto findSpawnableHost(LibraSpawnableHostHandle handle) -> liberafx::SpawnableHost * {
  auto found = spawnableHosts.find(handle);
  return found == spawnableHosts.end() ? nullptr : found->second.get();
}
auto ownedSpawnableHost(LibraSpawnableHostHandle handle, liberafx::SpawnableHost *&out) -> LibraResult {
  auto found = spawnableHosts.find(handle);
  if (found == spawnableHosts.end())
  {
    return LIBRA_INVALID_HANDLE;
  }
  if (found->second.ownerThread != std::this_thread::get_id())
  {
    return LIBRA_BUSY;
  }
  out = found->second.get();
  return LIBRA_OK;
}
auto readSpawnableScope(std::uint32_t kind, const std::uint8_t *container, LibraGeneratorInstance instance,
                        liberafx::SpawnableScope &out) -> bool {
  if (kind > LIBRA_SPAWNABLE_SCOPE_INSTANCE || (kind == LIBRA_SPAWNABLE_SCOPE_INSTANCE && !container))
  {
    return false;
  }
  out = {};
  out.kind = static_cast<liberafx::SpawnableScopeKind>(kind);
  out.instanceId = instance;
  if (container)
  {
    std::memcpy(out.containerId, container, 16);
  }
  return true; // Core validates zero/nonzero identities for each scope.
}
auto inlineEnqueue(void *, liberafx::Job job, void *data) noexcept -> bool {
  job(data);
  return true;
}
auto spawnSignal(liberafx::CustomNodeInvocation &call) noexcept -> liberafx::Result {
  auto &context = *static_cast<SpawnContext *>(call.context);
  auto captures = std::array<liberafx::GeneratorCapture, 16>{};
  auto count = std::size_t{};
  for (auto input = 0u; input < context.binding.inputCount; ++input)
  {
    for (auto lane = 0u; lane < context.binding.widths[input]; ++lane)
    {
      auto &capture = captures[count++];
      std::memcpy(capture.nodeId, context.binding.templateNodeIds[input], 16);
      capture.component = static_cast<std::uint8_t>(lane);
      capture.value = call.inputs[input * 4 + lane];
    }
  }
  call.selectedBranch = 0;
  return context.owner->pool->enqueueSpawn(call.time, captures.data(), count);
}
auto dispatchSpawnSignalInput(liberafx::CustomNodeInvocation &call) noexcept -> liberafx::Result {
  auto &context = *static_cast<SpawnSignalInputContext *>(call.context);
  call.selectedBranch = 0;
  return context.owner->pool->dispatchSignal(context.binding.signalNodeId, call.time, 10000);
}
auto checkValues(NativeInstance &native, const LibraBinding *bindings, std::uint32_t count, LibraValue *out)
    -> LibraResult {
  if (count && (!bindings || !out))
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  for (auto i = 0u; i < count; ++i)
  {
    if (!bindings[i] || bindings[i] > native.valueCount)
    {
      return LIBRA_INVALID_HANDLE;
    }
  }
  return LIBRA_OK;
}
auto readValues(NativeInstance &native, const LibraBinding *bindings, std::uint32_t count, LibraValue *out)
    -> LibraResult {
  for (auto i = 0u; i < count; ++i)
  {
    alignas(16) auto lanes = std::array<double, 4>{};
    const auto &handle = native.values[bindings[i] - 1];
    auto result = native.instance.value(handle, lanes.data());
    if (result != liberafx::Result::Ok)
    {
      return toLibraResult(result);
    }
    out[i] = {lanes[0], lanes[1], lanes[2], lanes[3], handle.componentCount, 0};
  }
  return LIBRA_OK;
}
auto parameterValueType(const NativeInstance &native, const liberafx::ParameterHandle &handle,
                        LibraValueType &outType) noexcept -> LibraResult {
  outType = LIBRA_VALUE_TYPE_UNKNOWN;
  const auto &program = native.owner->program;
  if (program.version() < 10)
  {
    return LIBRA_OK;
  }
  const auto &bytes = native.owner->bytes;
  auto instructionCount = program.nodeCount();
  auto tableSize = std::size_t{8} + instructionCount * 8;
  if (bytes.size() < tableSize || handle.nodeIndex >= instructionCount || handle.inputSlot >= 4)
  {
    return LIBRA_INVALID_BINARY;
  }
  auto tableOffset = bytes.size() - tableSize;
  auto table = bytes.data() + tableOffset;
  if (std::memcmp(table, "LFTY", 4) != 0 || table[4] != 1 || table[5] != 0 || table[6] != 8 || table[7] != 0)
  {
    return LIBRA_INVALID_BINARY;
  }
  auto record = table + 8 + std::size_t{handle.nodeIndex} * 8;
  if (handle.inputSlot >= record[1])
  {
    return LIBRA_INVALID_HANDLE;
  }
  auto type = record[2 + handle.inputSlot];
  if (type > LIBRA_VALUE_TYPE_ENUM)
  {
    return LIBRA_INVALID_BINARY;
  }
  outType = static_cast<LibraValueType>(type);
  return LIBRA_OK;
}
} // namespace liberafx::interop_detail

using namespace liberafx::interop_detail;

uint32_t LIBRA_INTEROP_CALL libra_abi_version(void) { return 1; }

LibraResult LIBRA_INTEROP_CALL libra_program_release(LibraProgram program) {
  return guarded([&]() -> LibraResult { return programs.erase(program) ? LIBRA_OK : LIBRA_INVALID_HANDLE; });
}
