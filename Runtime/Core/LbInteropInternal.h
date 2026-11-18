#pragma once

#include "liberafx/LbExecutionDebugInterop.h"
#include "liberafx/LbInterop.h"
#include "liberafx/host/LbSpawnableDebugJob.h"
#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#endif
#if defined(_WIN32)
#include "liberafx/host/LbWindowsExecutionDebugWorker.h"
#endif
#include "liberafx/LbBuiltinSignals.h"
#include "liberafx/LbEffectLifecycle.h"
#include "liberafx/LbGeneratorPool.h"
#include "liberafx/LbNamedIo.h"
#include "liberafx/LbSceneBindings.h"
#include "liberafx/LbSpawnableHost.h"
#include "liberafx/LbSpawnablePackage.h"
#include "liberafx/LbVfxRuntime.h"
#include <cmath>

#include <algorithm>
#include <array>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <optional>
#include <thread>
#include <type_traits>
#include <unordered_map>
#include <vector>

namespace liberafx::interop_detail {
template <class T> struct AlignedAllocator {
  using value_type = T;
  using propagate_on_container_move_assignment = std::true_type;
  using is_always_equal = std::false_type;
  static constexpr auto minimumAlignment = alignof(T) > 16 ? alignof(T) : 16;
  std::size_t alignment = minimumAlignment;
  AlignedAllocator() noexcept = default;
  explicit AlignedAllocator(std::size_t required) noexcept
      : alignment(required > minimumAlignment ? required : minimumAlignment) {}
  template <class U>
  AlignedAllocator(const AlignedAllocator<U> &other) noexcept
      : AlignedAllocator(other.alignment) {}
  auto allocate(std::size_t count) -> T * {
    if (count > std::numeric_limits<std::size_t>::max() / sizeof(T))
      throw std::bad_array_new_length{};
    return static_cast<T *>(
        ::operator new(count * sizeof(T), std::align_val_t{alignment}));
  }
  void deallocate(T *memory, std::size_t) noexcept {
    ::operator delete(memory, std::align_val_t{alignment});
  }
  template <class U>
  auto operator==(const AlignedAllocator<U> &other) const noexcept -> bool {
    return alignment == other.alignment;
  }
  template <class U>
  auto operator!=(const AlignedAllocator<U> &other) const noexcept -> bool {
    return !(*this == other);
  }
};
struct NativeGeneratorPool;
struct SpawnContext {
  std::shared_ptr<NativeGeneratorPool> owner;
  LibraSpawnableBinding binding{};
};
struct SpawnSignalInputContext {
  std::shared_ptr<NativeGeneratorPool> owner;
  LibraSpawnableSignalInputBinding binding{};
};
struct NativeProgram {
  std::vector<std::uint8_t> bytes;
  std::vector<liberafx::CustomNodeBinding> bindings;
  std::vector<SpawnContext> spawnContexts;
  std::vector<SpawnSignalInputContext> spawnSignalInputContexts;
  std::vector<liberafx::CustomNodeDescriptor> descriptors;
  liberafx::Program program;
};
struct NativeInstance {
  std::shared_ptr<NativeProgram> owner;
  std::vector<double, AlignedAllocator<double>> work, defaults;
  std::vector<liberafx::ValueHandle> values;
  std::vector<liberafx::ParameterHandle> parameters;
  std::size_t valueCount = 0, parameterCount = 0;
  // Destroy core state before its backing buffers and program.
  liberafx::Instance instance;
#if defined(_WIN32)
  std::unique_ptr<liberafx::host::WindowsExecutionDebugWorker> executionDebug;
#endif
  std::unique_ptr<liberafx::NamedIo> gameIo;
  std::optional<liberafx::EffectLifecycle> lifecycle;
  bool rawStarted = false;
  bool gameIoStarted = false;
};
struct NativeGeneratorPool {
  std::shared_ptr<NativeProgram> owner;
  std::unique_ptr<liberafx::GeneratorPool> pool;
  std::array<std::uint8_t, 16> containerId{};
};
#if defined(_WIN32)
struct NativeSpawnableDebug final {
  liberafx::SpawnableHost &host;
  liberafx::SpawnableDebugTarget target;
  liberafx::host::SpawnableDebugJob job;
  liberafx::host::WindowsExecutionDebugClient client;
  std::thread worker;
  bool hooked = false;
  NativeSpawnableDebug(liberafx::SpawnableHost &owner,
                       liberafx::SpawnableDebugTarget selected,
                       std::uint64_t identity)
      : host(owner), target(selected), job(owner),
        client(identity, selected.hostGeneration, selected.scope.containerId,
               selected.scope.instanceId) {}
  ~NativeSpawnableDebug() {
    client.stop();
    if (worker.joinable())
      worker.join();
    if (hooked)
      host.setDebugHooks(target, {});
  }
  static bool enqueue(void *context, liberafx::Job run,
                      void *argument) noexcept {
    try {
      static_cast<NativeSpawnableDebug *>(context)->worker =
          std::thread(run, argument);
      return true;
    } catch (...) {
      return false;
    }
  }
};
#endif
struct NativeSpawnableHost final {
  std::thread::id ownerThread;
  std::unique_ptr<liberafx::SpawnableHost> host;
  std::uint64_t packageIdentity = 0;
#if defined(_WIN32)
  std::unique_ptr<NativeSpawnableDebug> debug;
#endif
};

extern std::mutex registryMutex;
extern std::unordered_map<LibraProgram, std::shared_ptr<NativeProgram>>
    programs;
extern std::unordered_map<LibraInstance, std::unique_ptr<NativeInstance>>
    instances;
extern std::unordered_map<LibraGeneratorPool,
                          std::shared_ptr<NativeGeneratorPool>>
    generatorPools;
extern std::unordered_map<LibraSpawnableHostHandle, NativeSpawnableHost>
    spawnableHosts;
extern std::uint64_t nextHandle;

auto toLibraResult(liberafx::Result result) noexcept -> LibraResult;
auto findInstance(LibraInstance handle) -> NativeInstance *;
auto findSpawnableHost(LibraSpawnableHostHandle handle)
    -> liberafx::SpawnableHost *;
auto ownedSpawnableHost(LibraSpawnableHostHandle handle,
                        liberafx::SpawnableHost *&out) -> LibraResult;
auto readSpawnableScope(std::uint32_t kind, const std::uint8_t *container,
                        LibraGeneratorInstance instance,
                        liberafx::SpawnableScope &out) -> bool;
auto inlineEnqueue(void *context, liberafx::Job job, void *data) noexcept -> bool;
auto checkValues(NativeInstance &native, const LibraBinding *bindings,
                 std::uint32_t count, LibraValue *out) -> LibraResult;
auto readValues(NativeInstance &native, const LibraBinding *bindings,
                std::uint32_t count, LibraValue *out) -> LibraResult;
auto parameterValueType(const NativeInstance &native,
                        const liberafx::ParameterHandle &handle,
                        LibraValueType &outType) noexcept -> LibraResult;

template <class Function>
auto guarded(Function function) noexcept -> LibraResult {
  try {
    auto lock = std::lock_guard<std::mutex>(registryMutex);
    return function();
  } catch (const std::bad_alloc &) {
    return LIBRA_OUT_OF_MEMORY;
  } catch (...) {
    return LIBRA_INTERNAL_ERROR;
  }
}
template <class Function>
auto guardedSpawnable(LibraSpawnableHostHandle handle, Function function,
                      bool evaluates = false) noexcept -> LibraResult {
  return guarded([&]() -> LibraResult {
    auto found = spawnableHosts.find(handle);
    if (found == spawnableHosts.end())
      return LIBRA_INVALID_HANDLE;
    auto &owner = found->second;
    if (owner.ownerThread != std::this_thread::get_id()) {
      return LIBRA_BUSY;
    }
#if defined(_WIN32)
    if (owner.debug && (owner.debug->job.pending() || evaluates)) {
      return LIBRA_BUSY;
    }
#else
    (void)evaluates;
#endif
    return function();
  });
}

} // namespace liberafx::interop_detail
