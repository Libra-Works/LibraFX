#pragma once

#include "liberafx/LbBuiltinSignals.h"
#include "liberafx/LbEffectLifecycle.h"
#include "liberafx/LbSpawnableHost.h"
#include "liberafx/LbVfxRuntime.h"
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <optional>
#include <vector>

namespace liberafx::spawnable_host_detail {
inline auto nextHostGeneration(std::uint64_t& out) noexcept -> Result {
  static auto generation=std::atomic<std::uint64_t>{1};
  auto next=generation.load(std::memory_order_relaxed);
  do {
    if (next==UINT64_MAX) return Result::CounterOverflow;
  } while (!generation.compare_exchange_weak(next,next+1,std::memory_order_relaxed));
  out=next;
  return Result::Ok;
}
inline constexpr auto bytecodeHeaderBytes = std::size_t{16};
inline constexpr auto typedInstructionBytes = std::size_t{96};
inline constexpr auto customInstructionBytes = std::size_t{256};

inline auto isNonzero(const std::uint8_t id[16]) noexcept -> bool {
  for (auto index = 0u; index < 16; ++index) {
    if (id[index]) {
      return true;
    }
  }
  return false;
}

inline auto sameId(const std::uint8_t left[16],
                   const std::uint8_t right[16]) noexcept -> bool {
  return std::memcmp(left, right, 16) == 0;
}

struct OwnerOperation final {
  bool &busy;
  explicit OwnerOperation(bool &value) noexcept : busy(value) { busy = true; }
  ~OwnerOperation() { busy = false; }
};

struct OwnerTagOperation final {
  std::uint64_t &current;
  std::uint64_t previous;
  OwnerTagOperation(std::uint64_t &value, std::uint64_t next) noexcept
      : current(value), previous(value) {
    current = next;
  }
  ~OwnerTagOperation() { current = previous; }
};

struct CpuWorldOperation final {
  CpuParticleWorld *&current;
  CpuParticleWorld *previous;
  CpuWorldOperation(CpuParticleWorld *&value, CpuParticleWorld *next) noexcept
      : current(value), previous(value) {
    current = next;
  }
  ~CpuWorldOperation() { current = previous; }
};

inline auto validScope(const SpawnableScope &scope) noexcept -> bool {
  if (scope.kind == SpawnableScopeKind::Effect) {
    return !scope.instanceId && !isNonzero(scope.containerId);
  }
  return scope.kind == SpawnableScopeKind::Instance && scope.instanceId &&
         isNonzero(scope.containerId);
}

struct AlignedBuffer final {
  void *data = nullptr;
  std::size_t alignment = 16;
  ~AlignedBuffer() { release(); }
  void release() noexcept {
    if (data) {
      ::operator delete(data, std::align_val_t{alignment});
    }
    data = nullptr;
  }
  AlignedBuffer() = default;
  AlignedBuffer(const AlignedBuffer &) = delete;
  AlignedBuffer &operator=(const AlignedBuffer &) = delete;
  auto allocate(std::size_t bytes, std::size_t requested) noexcept -> Result {
    alignment = std::max<std::size_t>(requested, 16);
    if (!bytes) {
      return Result::Ok;
    }
    try {
      data = ::operator new(bytes, std::align_val_t{alignment});
      std::memset(data, 0, bytes);
      return Result::Ok;
    } catch (const std::bad_alloc &) {
      return Result::InsufficientWork;
    }
  }
};

struct SpawnCallbackContext final {
  GeneratorPool *pool = nullptr;
  std::uint64_t *ownerTag = nullptr;
  std::array<std::array<std::uint8_t, 16>, 4> nodeIds{};
  std::array<std::uint8_t, 4> widths{};
  std::uint8_t inputCount = 0;
};

struct SignalInputCallbackContext final {
  GeneratorPool *pool = nullptr;
  std::uint64_t *ownerTag = nullptr;
  std::array<std::uint8_t, 16> nodeId{};
};

struct CpuSignalCallbackContext final {
  CpuParticleWorld **activeWorld = nullptr;
  std::array<std::uint8_t, 16> nodeId{};
};

auto bytecodeNodeCount(const std::vector<std::uint8_t> &bytes,
                       std::size_t &count) noexcept -> Result;
auto loadProgram(const std::vector<std::uint8_t> &bytes,
                 const CustomNodeDescriptor *registry,
                 std::size_t registryCount,
                 std::vector<CustomNodeBinding> &bindings, Program &out)
    -> Result;
auto findSignalType(const std::vector<std::uint8_t> &bytes,
                    const std::uint8_t nodeId[16],
                    std::array<std::uint8_t, 16> &typeId) noexcept -> Result;
auto enqueueSpawn(CustomNodeInvocation &call) noexcept -> Result;
auto dispatchTemplateSignal(CustomNodeInvocation &call) noexcept -> Result;
auto dispatchCpuParticleSignal(CustomNodeInvocation &call) noexcept -> Result;
auto passthroughDescriptor(CustomNodeDescriptor &out) noexcept -> void;
} // namespace liberafx::spawnable_host_detail

namespace liberafx {
struct SpawnableHost::Impl final {
  struct InstanceInfo final {
    GeneratorInstanceId id = 0;
    double start = 0;
    double life = std::numeric_limits<double>::infinity();
    std::uint64_t ownerTag = 0;
    std::uint64_t scopeTag = 0;
    std::unique_ptr<CpuParticleWorld> cpuParticles;
    bool retiring = false;
    std::uint64_t retirementSeed = 0;
    bool destroyPublished = false;
    bool executionDebugAttached = false;
    double realStart = 0;
  };
  struct TemplateState final {
    const SpawnablePackageTemplate *data = nullptr;
    std::uint8_t depth = 1;
    Program program;
    std::vector<CustomNodeBinding> bindings;
    spawnable_host_detail::SpawnCallbackContext spawnContext;
    std::vector<std::array<std::uint8_t, 16>> outputIds;
    std::vector<CustomNodeDescriptor> descriptors;
    std::vector<spawnable_host_detail::SpawnCallbackContext> spawnCallbacks;
    std::vector<spawnable_host_detail::SignalInputCallbackContext> signalCallbacks;
    std::vector<std::array<std::uint8_t, 16>> signalTypes;
    std::vector<spawnable_host_detail::CpuSignalCallbackContext> cpuCallbacks;
    std::unique_ptr<CpuParticleWorld> cpuDescriptorWorld;
    std::unique_ptr<GeneratorPool> pool;
    std::vector<InstanceInfo> instances;
    std::vector<std::unique_ptr<CpuParticleWorld>> idleCpuWorlds;
  };
  struct SignalInputContext final {
    std::array<std::uint8_t, 16> typeId{};
    spawnable_host_detail::SignalInputCallbackContext callback;
  };
  struct SpawnContextEntry final {
    std::array<std::uint8_t, 16> typeId{};
    spawnable_host_detail::SpawnCallbackContext callback;
  };
  struct CapturedRoute final {
    const SpawnablePackageDestroyRoute *route = nullptr;
    std::vector<GeneratorCapture> captures;
    std::uint64_t ownerTag = 0;
  };
  struct ScheduledRoute final {
    std::uint64_t token = 0;
    CapturedRoute continuation;
    GeneratorInstanceId sourceInstanceId = 0;
    std::uint64_t ownerTag = 0;
  };
  struct OwnerNotice final {
    std::array<std::uint8_t, 16> containerId{};
    GeneratorInstanceId instanceId = 0;
  };
  struct RetiredCpuEvent final {
    std::array<std::uint8_t, 16> containerId{};
    GeneratorInstanceId instanceId = 0;
    CpuParticleEvent event{};
  };

  SpawnablePackage packageData;
  SpawnableHostOptions options;
  std::vector<CustomNodeDescriptor> projectDescriptors;
  bool executionDebugAttached = false;
  bool callbackReuseSafe = true, callbackWorkerSafe = true, callbackPreparationSafe = true;
  void configureExecutionSafety() noexcept;
  Result recycleCpuWorld(TemplateState &state, InstanceInfo &instance) noexcept;
  std::vector<std::unique_ptr<TemplateState>> templates;
  std::vector<SpawnContextEntry> spawnContexts;
  std::vector<SignalInputContext> signalInputs;
  std::vector<CustomNodeDescriptor> parentDescriptors;
  std::vector<CustomNodeBinding> parentBindings;
  Program parentProgram;
  spawnable_host_detail::AlignedBuffer rootWork;
  spawnable_host_detail::AlignedBuffer rootDefaults;
  std::optional<Instance> rootInstance;
  std::unique_ptr<CpuParticleWorld> cpuParticles;
  CpuParticleWorld *activeCpuParticles = nullptr;
  std::unique_ptr<NamedIo> gameIo;
  std::optional<EffectLifecycle> lifecycle;
  std::vector<NamedIoPort> gameIoPorts;
  std::vector<SpawnableHostEvent> events;
  std::size_t eventHead = 0;
  std::size_t eventCount = 0;
  std::vector<SpawnableHostEvent> pendingForceEvents;
  std::size_t pendingForceEventHead = 0;
  std::vector<NamedIoEvent> childGameEvents;
  std::size_t childGameHead = 0;
  std::size_t childGameCount = 0;
  std::vector<OwnerNotice> ownerNotices;
  std::vector<RetiredCpuEvent> retiredCpuEvents;
  std::vector<ScheduledRoute> scheduledRoutes;
  double time = -1;
  double rootAge = 0;
  CoordinateBasis coordinateBasis{};
  double frameDelta = 0;
  double realElapsed = 0, effectElapsed = 0, realFrameDelta = 0, timeScale = 1;
  auto realTimeAt(double effectAt) const noexcept -> double {
    return timeScale > 0 ? std::max(0.0, realElapsed - (effectElapsed - effectAt) / timeScale) : realElapsed;
  }
  HostQualitySnapshot quality{};
  std::uint64_t nextToken = 1;
  std::uint64_t nextScopeTag = 1;
  std::uint64_t currentOwnerTag = 0;
  std::uint64_t debugGeneration = 0;
  bool started = false;
  bool rootEnding = false;
  bool rootFinished = false;
  bool rootStopped = false;
  bool rootDestroySent = false;
  bool pendingPump = false;
  bool operationActive = false;
  bool rootForced = false;
  bool retainForPool = false;
  Result failure = Result::Ok;

  Impl(SpawnablePackage value, const SpawnableHostOptions &configuration)
      : packageData(std::move(value)), options(configuration),
        events(configuration.eventCapacity),
        childGameEvents(configuration.eventCapacity) {}

  auto findTemplate(const std::uint8_t id[16]) noexcept -> TemplateState * {
    for (auto &state : templates) {
      if (spawnable_host_detail::sameId(state->data->containerId.data(), id)) {
        return state.get();
      }
    }
    return nullptr;
  }

  auto findInstance(TemplateState &state, GeneratorInstanceId id) noexcept
      -> InstanceInfo * {
    for (auto &instance : state.instances) {
      if (instance.id == id) {
        return &instance;
      }
    }
    return nullptr;
  }

  auto findScope(std::uint64_t tag, TemplateState *&state,
                 InstanceInfo *&instance) noexcept -> bool {
    if (!tag) {
      state = nullptr;
      instance = nullptr;
      return true;
    }
    for (auto &candidate : templates) {
      for (auto &item : candidate->instances) {
        if (item.scopeTag == tag) {
          state = candidate.get();
          instance = &item;
          return true;
        }
      }
    }
    return false;
  }

  auto findPort(TemplateState &state, const char *name, NamedIoKind kind,
                ProgramGameIoPort &out) const noexcept -> bool {
    if (!name) {
      return false;
    }
    auto length = std::size_t{};
    while (length <= 128 && name[length]) {
      ++length;
    }
    if (!length || length > 128) {
      return false;
    }
    for (auto index = std::size_t{}; index < state.program.gameIoPortCount();
         ++index) {
      auto port = ProgramGameIoPort{};
      if (state.program.gameIoPort(index, port) &&
          port.kind == static_cast<std::uint8_t>(kind) &&
          std::strcmp(port.name, name) == 0) {
        out = port;
        return true;
      }
    }
    return false;
  }

  void discardChildGameEvents(const SpawnableScope &scope) noexcept {
    auto retained = std::size_t{};
    for (auto index = std::size_t{}; index < childGameCount; ++index) {
      const auto event =
          childGameEvents[(childGameHead + index) % childGameEvents.size()];
      if (scope.kind == SpawnableScopeKind::Effect ||
          (event.identity.instanceId == scope.instanceId &&
           spawnable_host_detail::sameId(event.identity.containerId,
                                         scope.containerId))) {
        continue;
      }
      childGameEvents[(childGameHead + retained++) % childGameEvents.size()] =
          event;
    }
    childGameCount = retained;
  }

  auto activeCount() const noexcept -> std::size_t {
    auto count = cpuParticles ? cpuParticles->activeCount() : std::size_t{};
    for (const auto &state : templates) {
      count += state->pool->activeCount();
    }
    return count;
  }

  auto appendEvent(SpawnableHostEventKind kind,
                   const std::uint8_t container[16],
                   GeneratorInstanceId instance, double eventTime,
                   std::uint64_t seed,
                   const std::uint8_t *nodeId = nullptr) noexcept -> Result {
    if (eventCount == events.size()) {
      return Result::QueueRejected;
    }

    auto &event = events[(eventHead + eventCount) % events.size()];
    event = {};
    event.kind = kind;
    if (container) {
      std::memcpy(event.containerId, container, 16);
    }
    event.instanceId = instance;
    event.time = eventTime;
    event.seed = seed;
    if (nodeId) {
      std::memcpy(event.signalNodeId, nodeId, 16);
    }
    ++eventCount;
    return Result::Ok;
  }

  auto appendForceEvent(const std::uint8_t container[16],
                        GeneratorInstanceId instance,
                        double eventTime) -> Result {
    auto event = SpawnableHostEvent{};
    event.kind = SpawnableHostEventKind::ForceTerminated;
    if (container) {
      std::memcpy(event.containerId, container, 16);
    }
    event.instanceId = instance;
    event.time = eventTime;
    if (eventCount < events.size()) {
      events[(eventHead + eventCount++) % events.size()] = event;
      return Result::Ok;
    }
    pendingForceEvents.push_back(event);
    return Result::Ok;
  }

  auto readRoot(const std::uint8_t nodeId[16], double out[4],
                std::uint8_t &width) -> Result {
    auto handle = ValueHandle{};
    auto result = rootInstance->bindValue(nodeId, handle);
    if (result != Result::Ok) {
      return result;
    }
    width = handle.componentCount;
    return rootInstance->value(handle, out);
  }

  auto readChild(TemplateState &state, GeneratorInstanceId instance,
                 const std::uint8_t nodeId[16], double out[4],
                 std::uint8_t &width) -> Result {
    return state.pool->value(instance, nodeId, out, width);
  }

  auto readOwner(std::uint64_t ownerTag,
                 const std::uint8_t *requiredContainer,
                 const std::uint8_t nodeId[16], double out[4],
                 std::uint8_t &width) -> Result {
    auto state = static_cast<TemplateState *>(nullptr);
    auto instance = static_cast<InstanceInfo *>(nullptr);
    if (!findScope(ownerTag, state, instance)) {
      return Result::InvalidHandle;
    }
    if (!state) {
      if (requiredContainer) {
        return Result::InvalidBinary;
      }
      return readRoot(nodeId, out, width);
    }
    if (requiredContainer &&
        !spawnable_host_detail::sameId(requiredContainer,
                                       state->data->containerId.data())) {
      return Result::InvalidBinary;
    }
    return readChild(*state, instance->id, nodeId, out, width);
  }

  auto appendCapture(std::vector<GeneratorCapture> &captures,
                     const SpawnablePackageCapture &binding,
                     const double value[4], std::uint8_t valueWidth) -> Result {
    if (binding.width < 1 || binding.width > 4 ||
        binding.sourceComponent > valueWidth ||
        binding.width > valueWidth - binding.sourceComponent) {
      return Result::InvalidBinary;
    }
    if (captures.size() > 256 - binding.width) {
      return Result::InvalidBinary;
    }
    for (auto lane = 0u; lane < binding.width; ++lane) {
      auto capture = GeneratorCapture{};
      std::memcpy(capture.nodeId, binding.targetNodeId.data(), 16);
      capture.component = static_cast<std::uint8_t>(lane);
      capture.value = value[binding.sourceComponent + lane];
      captures.push_back(capture);
    }
    return Result::Ok;
  }

  auto appendParentCapture(std::vector<GeneratorCapture> &captures,
                           const SpawnablePackageCapture &binding,
                           std::uint64_t ownerTag) -> Result {
    auto values = std::array<double, 4>{};
    auto width = std::uint8_t{};
    auto result = readOwner(ownerTag,
                            binding.sourceContainerId
                                ? binding.sourceContainerId->data()
                                : nullptr,
                            binding.sourceNodeId.data(), values.data(), width);
    if (result != Result::Ok) {
      return result;
    }
    return appendCapture(captures, binding, values.data(), width);
  }

  auto initialize() -> Result;
  auto startRoot(double initialFrameDelta) -> Result;
  auto evaluateParent(double at) -> Result;
  auto evaluateFrame(double at) -> Result;
  auto pump(double at) -> Result;
  auto processPoolEvent(TemplateState &state, const GeneratorEvent &event,
                        double at) -> Result;
  auto publishNaturalDestroy(TemplateState &state, const InstanceInfo &instance,
                             double at, std::uint64_t seed) -> Result;
  auto dispatchOwnerSignal(std::uint64_t ownerTag,
                           const std::uint8_t nodeId[16], double at) -> Result;
  auto spawnRoute(const CapturedRoute &captured, double at) -> Result;
  auto dispatchScheduled(double at) -> Result;
  auto nextDeferredTime(double &out) -> Result;
  auto nextChildDeferredTime(double &out) -> Result;
  auto nextScheduledTime(double &out) -> Result;
  auto notifyOwnerDestroy(double at) -> Result;
  auto beginAutoEnd(double at) -> Result;
  auto finishIfQuiescent() -> Result;
  auto finishRetiredInstances() -> Result;
  auto preserveCpuEvents(TemplateState &state,
                         InstanceInfo &instance) -> Result;
  auto applyCaptures(
      TemplateState &state, GeneratorInstanceId id,
      const std::optional<std::vector<SpawnablePackageCapture>> &inputs,
      bool live, std::uint64_t ownerTag) -> Result;
  auto forceTerminate(const SpawnableScope &scope) -> Result;
  auto applyAuthoredForce() -> Result;
};
} // namespace liberafx
