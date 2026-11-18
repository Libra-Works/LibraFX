#include "LbInteropInternal.h"

using namespace liberafx::interop_detail;

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_create(
    const uint8_t *bytes, uint64_t byteCount, uint32_t maxActivePerTemplate,
    uint64_t memoryBudgetBytesPerTemplate, uint32_t eventCapacity,
    uint32_t instructionBudget, LibraSpawnableHostHandle *outHost) {
  if (!outHost || !bytes || !byteCount ||
      memoryBudgetBytesPerTemplate > std::numeric_limits<std::size_t>::max())
    return LIBRA_INVALID_ARGUMENT;
  *outHost = 0;
  return guarded([&]() -> LibraResult {
    auto package = liberafx::SpawnablePackage{};
    auto result = liberafx::loadSpawnablePackage(
        bytes, static_cast<std::size_t>(byteCount), package);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    auto options = liberafx::SpawnableHostOptions{};
    options.maxActivePerTemplate = maxActivePerTemplate;
    options.memoryBudgetBytesPerTemplate =
        static_cast<std::size_t>(memoryBudgetBytesPerTemplate);
    options.eventCapacity = eventCapacity;
    options.instructionBudget = instructionBudget;
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    result = liberafx::SpawnableHost::create(std::move(package), options, host);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    if (!nextHandle)
      return LIBRA_INSUFFICIENT_WORK;
    const auto handle = nextHandle++;
    auto owner =
        NativeSpawnableHost{std::this_thread::get_id(), std::move(host)};
#if defined(_WIN32)
    owner.packageIdentity =
        liberafx::host::ExecutionDebugProtocol::programIdentity(
            bytes, static_cast<std::size_t>(byteCount));
#endif
    spawnableHosts.emplace(handle, std::move(owner));
    *outHost = handle;
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL
libra_spawnable_host_release(LibraSpawnableHostHandle host) {
  return guarded([&]() -> LibraResult {
    auto found = spawnableHosts.find(host);
    if (found == spawnableHosts.end())
      return LIBRA_INVALID_HANDLE;
    if (found->second.ownerThread != std::this_thread::get_id())
      return LIBRA_BUSY;
    spawnableHosts.erase(found);
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_debug_generation(
    LibraSpawnableHostHandle host, uint64_t *generation) {
  if (!generation)
    return LIBRA_INVALID_ARGUMENT;
  *generation = 0;
  return guardedSpawnable(host, [&]() -> LibraResult {
    *generation = findSpawnableHost(host)->debugGeneration();
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_debug_attach(
    LibraSpawnableHostHandle host, uint64_t generation,
    const uint8_t *container, LibraGeneratorInstance instance,
    const char *invitation) {
#if defined(_WIN32)
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto &owner = spawnableHosts.at(host);
    if (owner.ownerThread != std::this_thread::get_id())
      return LIBRA_BUSY;
    if (owner.debug)
      return LIBRA_BUSY;
    if (generation != owner.host->debugGeneration())
      return LIBRA_INVALID_HANDLE;
    auto scope = liberafx::SpawnableScope{};
    if (!readSpawnableScope(container ? 1 : 0, container, instance, scope))
      return LIBRA_INVALID_ARGUMENT;
    auto target = liberafx::SpawnableDebugTarget{};
    auto result = owner.host->debugTarget(scope, target);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    auto debug = std::make_unique<NativeSpawnableDebug>(*owner.host, target,
                                                        owner.packageIdentity);
    result = owner.host->setDebugHooks(target, debug->client.hooks());
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    debug->hooked = true;
    if (!debug->client.start(invitation))
      return LIBRA_INVALID_ARGUMENT;
    owner.debug = std::move(debug);
    return LIBRA_OK;
  });
#else
  (void)host;
  (void)generation;
  (void)container;
  (void)instance;
  (void)invitation;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_debug_submit(
    LibraSpawnableHostHandle host, uint8_t operation, double time,
    double frameDelta, const uint8_t *container,
    LibraGeneratorInstance instance, const uint8_t *node, const char *name) {
#if defined(_WIN32)
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto &owner = spawnableHosts.at(host);
    if (!owner.debug)
      return LIBRA_NOT_READY;
    auto request = liberafx::host::SpawnableDebugJob::Request{};
    if (operation > 4 || ((operation == 1 || operation == 4) && !node) ||
        (operation == 2 && (!name || std::strlen(name) >= request.name.size())))
      return LIBRA_INVALID_ARGUMENT;
    request.operation =
        static_cast<liberafx::host::SpawnableDebugOperation>(operation);
    request.time = time;
    request.frameDelta = frameDelta;
    if (node)
      std::memcpy(request.node.data(), node, 16);
    if (name) {
      if (std::strlen(name) >= request.name.size())
        return LIBRA_INVALID_ARGUMENT;
      std::memcpy(request.name.data(), name, std::strlen(name));
    }
    if (!readSpawnableScope(container ? 1 : 0, container, instance,
                            request.scope))
      return LIBRA_INVALID_ARGUMENT;
    return toLibraResult(owner.debug->job.submit(
        request, {&NativeSpawnableDebug::enqueue, owner.debug.get()}));
  });
#else
  (void)host;
  (void)operation;
  (void)time;
  (void)frameDelta;
  (void)container;
  (void)instance;
  (void)node;
  (void)name;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}

LibraResult LIBRA_INTEROP_CALL
libra_spawnable_debug_poll(LibraSpawnableHostHandle host, LibraResult *result) {
  if (!result)
    return LIBRA_INVALID_ARGUMENT;
  *result = LIBRA_NOT_READY;
#if defined(_WIN32)
  return guarded([&]() -> LibraResult {
    auto found = spawnableHosts.find(host);
    if (found == spawnableHosts.end())
      return LIBRA_INVALID_HANDLE;
    auto &owner = found->second;
    if (owner.ownerThread != std::this_thread::get_id())
      return LIBRA_BUSY;
    if (!owner.debug)
      return LIBRA_NOT_READY;
    auto work = liberafx::Result::NotReady;
    auto status = owner.debug->job.poll(work);
    if (status != liberafx::Result::Ok)
      return toLibraResult(status);
    if (owner.debug->worker.joinable())
      owner.debug->worker.join();
    *result = toLibraResult(work);
    return LIBRA_OK;
  });
#else
  (void)host;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}

LibraResult LIBRA_INTEROP_CALL
libra_spawnable_debug_detach(LibraSpawnableHostHandle host) {
#if defined(_WIN32)
  return guarded([&]() -> LibraResult {
    auto found = spawnableHosts.find(host);
    if (found == spawnableHosts.end())
      return LIBRA_INVALID_HANDLE;
    if (found->second.ownerThread != std::this_thread::get_id())
      return LIBRA_BUSY;
    found->second.debug.reset();
    return LIBRA_OK;
  });
#else
  (void)host;
  return LIBRA_UNSUPPORTED_BINARY;
#endif
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_advance(
    LibraSpawnableHostHandle host, double time, double frameDelta) {
  return guardedSpawnable(
      host,
      [&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? toLibraResult(native->advance(time, frameDelta))
                      : LIBRA_INVALID_HANDLE;
      },
      true);
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_dispatch_signal(
    LibraSpawnableHostHandle host, const uint8_t nodeId[16]) {
  if (!nodeId)
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(
      host,
      [&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? toLibraResult(native->dispatchSignal(nodeId))
                      : LIBRA_INVALID_HANDLE;
      },
      true);
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_fire_game_signal(
    LibraSpawnableHostHandle host, const char *name) {
  if (!name)
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(
      host,
      [&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? toLibraResult(native->fireGameSignal(name))
                      : LIBRA_INVALID_HANDLE;
      },
      true);
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_set_game_property(
    LibraSpawnableHostHandle host, const char *name, const double *values,
    uint32_t width) {
  if (!name || (width && !values))
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    return native ? toLibraResult(native->setGameProperty(name, values, width))
                  : LIBRA_INVALID_HANDLE;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_set_quality(
    LibraSpawnableHostHandle host, uint8_t availability, uint8_t tier,
    uint8_t source, uint64_t frameSerial) {
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    auto snapshot = liberafx::HostQualitySnapshot{
        static_cast<liberafx::HostQualityAvailability>(availability),
        static_cast<liberafx::HostQualityTier>(tier),
        static_cast<liberafx::HostQualitySource>(source), frameSerial};
    return native ? toLibraResult(native->setHostQuality(snapshot))
                  : LIBRA_INVALID_HANDLE;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_instance_target(
    LibraSpawnableHostHandle host, const uint8_t containerId[16],
    LibraGeneratorInstance instance, uint64_t *outGeneration) {
  if (!containerId || !outGeneration)
    return LIBRA_INVALID_ARGUMENT;
  *outGeneration = 0;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto scope = liberafx::SpawnableScope{};
    scope.kind = liberafx::SpawnableScopeKind::Instance;
    std::memcpy(scope.containerId, containerId, 16);
    scope.instanceId = instance;
    auto target = liberafx::SpawnableDebugTarget{};
    auto result = native->debugTarget(scope, target);
    if (result == liberafx::Result::Ok)
      *outGeneration = target.hostGeneration;
    return toLibraResult(result);
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_set_instance_property(
    LibraSpawnableHostHandle host, uint64_t generation,
    const uint8_t containerId[16], LibraGeneratorInstance instance,
    const char *name, const double *values, uint32_t width) {
  if (!containerId || !name || !values)
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto target = liberafx::SpawnableDebugTarget{};
    target.hostGeneration = generation;
    target.scope.kind = liberafx::SpawnableScopeKind::Instance;
    std::memcpy(target.scope.containerId, containerId, 16);
    target.scope.instanceId = instance;
    return toLibraResult(
        native->setInstanceProperty(target, name, values, width));
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_get_instance_property(
    LibraSpawnableHostHandle host, uint64_t generation,
    const uint8_t containerId[16], LibraGeneratorInstance instance,
    const char *name, double values[16], uint32_t *outWidth) {
  if (!containerId || !name || !values || !outWidth)
    return LIBRA_INVALID_ARGUMENT;
  *outWidth = 0;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto target = liberafx::SpawnableDebugTarget{};
    target.hostGeneration = generation;
    target.scope.kind = liberafx::SpawnableScopeKind::Instance;
    std::memcpy(target.scope.containerId, containerId, 16);
    target.scope.instanceId = instance;
    auto width = uint8_t{};
    auto result = native->instanceProperty(target, name, values, width);
    if (result == liberafx::Result::Ok)
      *outWidth = width;
    return toLibraResult(result);
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_fire_instance_signal(
    LibraSpawnableHostHandle host, uint64_t generation,
    const uint8_t containerId[16], LibraGeneratorInstance instance,
    const char *name) {
  if (!containerId || !name)
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(
      host,
      [&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native)
          return LIBRA_INVALID_HANDLE;
        auto target = liberafx::SpawnableDebugTarget{};
        target.hostGeneration = generation;
        target.scope.kind = liberafx::SpawnableScopeKind::Instance;
        std::memcpy(target.scope.containerId, containerId, 16);
        target.scope.instanceId = instance;
        return toLibraResult(native->fireInstanceSignal(target, name));
      },
      true);
}

LibraResult LIBRA_INTEROP_CALL
libra_spawnable_host_copy_instance_game_io_port_name(
    LibraSpawnableHostHandle host, const uint8_t containerId[16],
    uint32_t index, uint8_t *destination, uint64_t capacity,
    uint64_t *outByteCount) {
  if (!containerId || !outByteCount || (!destination && capacity))
    return LIBRA_INVALID_ARGUMENT;
  *outByteCount = 0;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto name = static_cast<const char *>(nullptr);
    auto result = native->instancePortName(containerId, index, name);
    if (result != liberafx::Result::Ok)
      return toLibraResult(result);
    const auto size = std::strlen(name);
    *outByteCount = size;
    if (!destination)
      return LIBRA_OK;
    if (capacity < size)
      return LIBRA_INSUFFICIENT_WORK;
    std::memcpy(destination, name, size);
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_set_owner_matrix(
    LibraSpawnableHostHandle host, const double values[16]) {
  if (!values)
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    return native ? toLibraResult(native->setOwnerMatrix(values, 16))
                  : LIBRA_INVALID_HANDLE;
  });
}

LibraResult LIBRA_INTEROP_CALL
libra_spawnable_host_request_natural_end(LibraSpawnableHostHandle host) {
  return guardedSpawnable(
      host,
      [&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? toLibraResult(native->requestNaturalEnd())
                      : LIBRA_INVALID_HANDLE;
      },
      true);
}

LibraResult LIBRA_INTEROP_CALL
libra_spawnable_host_stop(LibraSpawnableHostHandle host) {
  return guardedSpawnable(
      host,
      [&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? toLibraResult(native->stop()) : LIBRA_INVALID_HANDLE;
      },
      true);
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_force_terminate(
    LibraSpawnableHostHandle host, uint32_t scopeKind,
    const uint8_t containerId[16], LibraGeneratorInstance instance) {
  auto scope = liberafx::SpawnableScope{};
  if (!readSpawnableScope(scopeKind, containerId, instance, scope))
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = static_cast<liberafx::SpawnableHost *>(nullptr);
    auto result = ownedSpawnableHost(host, native);
    return result == LIBRA_OK ? toLibraResult(native->forceTerminate(scope))
                              : result;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_notify_end_request(
    LibraSpawnableHostHandle host, uint32_t scopeKind,
    const uint8_t containerId[16], LibraGeneratorInstance instance,
    const uint8_t userSignalNodeId[16]) {
  auto scope = liberafx::SpawnableScope{};
  if (!userSignalNodeId ||
      !readSpawnableScope(scopeKind, containerId, instance, scope))
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(
      host,
      [&]() -> LibraResult {
        auto native = static_cast<liberafx::SpawnableHost *>(nullptr);
        auto result = ownedSpawnableHost(host, native);
        return result == LIBRA_OK ? toLibraResult(native->notifyEndRequest(
                                        scope, userSignalNodeId))
                                  : result;
      },
      true);
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_root_value(
    LibraSpawnableHostHandle host, const uint8_t nodeId[16],
    LibraValue *outValue) {
  if (!nodeId || !outValue)
    return LIBRA_INVALID_ARGUMENT;
  *outValue = {};
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto values = std::array<double, 4>{};
    auto width = std::uint8_t{};
    const auto result = native->rootValue(nodeId, values.data(), width);
    if (result == liberafx::Result::Ok)
      *outValue = {values[0], values[1], values[2], values[3], width, 0};
    return toLibraResult(result);
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_child_value(
    LibraSpawnableHostHandle host, const uint8_t containerId[16],
    LibraGeneratorInstance instance, const uint8_t nodeId[16],
    LibraValue *outValue) {
  if (!containerId || !nodeId || !outValue)
    return LIBRA_INVALID_ARGUMENT;
  *outValue = {};
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto values = std::array<double, 4>{};
    auto width = std::uint8_t{};
    const auto result =
        native->childValue(containerId, instance, nodeId, values.data(), width);
    if (result == liberafx::Result::Ok)
      *outValue = {values[0], values[1], values[2], values[3], width, 0};
    return toLibraResult(result);
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_next_event(
    LibraSpawnableHostHandle host, LibraSpawnableHostEvent *outEvent,
    uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
    return LIBRA_INVALID_ARGUMENT;
  *outEvent = {};
  *hasEvent = 0;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto event = liberafx::SpawnableHostEvent{};
    if (!native->popEvent(event))
      return LIBRA_OK;
    outEvent->kind = static_cast<uint32_t>(event.kind);
    std::memcpy(outEvent->containerId, event.containerId, 16);
    outEvent->instanceId = event.instanceId;
    outEvent->time = event.time;
    outEvent->seed = event.seed;
    std::memcpy(outEvent->signalNodeId, event.signalNodeId, 16);
    *hasEvent = 1;
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_next_game_io_event(
    LibraSpawnableHostHandle host, LibraGameIoEventV2 *outEvent,
    uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
    return LIBRA_INVALID_ARGUMENT;
  *outEvent = {};
  *hasEvent = 0;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    auto event = liberafx::NamedIoEvent{};
    if (!native->popGameIoEvent(event))
      return LIBRA_OK;
    std::memcpy(outEvent->containerId, event.identity.containerId, 16);
    outEvent->instanceId = event.identity.instanceId;
    outEvent->portIndex = event.portIndex;
    outEvent->kind = static_cast<std::uint8_t>(event.kind);
    outEvent->width = event.width;
    outEvent->isColor = event.isColor ? 1 : 0;
    std::copy(std::begin(event.values), std::end(event.values),
              outEvent->values);
    *hasEvent = 1;
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_copy_game_io_port_name(
    LibraSpawnableHostHandle host, uint32_t index, uint8_t *destination,
    uint64_t capacity, uint64_t *outByteCount) {
  if (!outByteCount)
    return LIBRA_INVALID_ARGUMENT;
  *outByteCount = 0;
  if (!destination && capacity != 0)
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    const auto &ports = native->package().gameIoPorts;
    if (index >= ports.size())
      return LIBRA_INVALID_ARGUMENT;
    const auto &name = ports[index].name;
    *outByteCount = name.size();
    if (!destination)
      return LIBRA_OK;
    if (capacity < name.size())
      return LIBRA_INSUFFICIENT_WORK;
    if (!name.empty())
      std::memcpy(destination, name.data(), name.size());
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_active_count(
    LibraSpawnableHostHandle host, uint64_t *outCount) {
  if (!outCount)
    return LIBRA_INVALID_ARGUMENT;
  *outCount = 0;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    *outCount = native->activeCount();
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_root_finished(
    LibraSpawnableHostHandle host, uint32_t *outFinished) {
  if (!outFinished)
    return LIBRA_INVALID_ARGUMENT;
  *outFinished = 0;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    *outFinished = native->rootFinished() ? 1 : 0;
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_template_count(
    LibraSpawnableHostHandle host, uint64_t *outCount) {
  if (!outCount)
    return LIBRA_INVALID_ARGUMENT;
  *outCount = 0;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    *outCount = native->package().templates.size();
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_template_id(
    LibraSpawnableHostHandle host, uint64_t index, uint8_t outContainerId[16]) {
  if (!outContainerId)
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    const auto &templates = native->package().templates;
    if (index >= templates.size())
      return LIBRA_INVALID_ARGUMENT;
    const auto &id = templates[static_cast<std::size_t>(index)].containerId;
    std::copy(id.begin(), id.end(), outContainerId);
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_copy_template_payload(
    LibraSpawnableHostHandle host, const uint8_t containerId[16], uint32_t kind,
    uint8_t *destination, uint64_t capacity, uint64_t *outByteCount) {
  if (!outByteCount)
    return LIBRA_INVALID_ARGUMENT;
  *outByteCount = 0;
  if (!containerId || (!destination && capacity != 0) ||
      (kind != LIBRA_SPAWNABLE_RENDER_CONFIGURATION &&
       kind != LIBRA_SPAWNABLE_GPU_CONTAINERS &&
       kind != LIBRA_SPAWNABLE_GPU_PROGRAMS))
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    const auto &templates = native->package().templates;
    auto found = std::find_if(
        templates.begin(), templates.end(), [&](const auto &value) {
          return std::memcmp(value.containerId.data(), containerId, 16) == 0;
        });
    if (found == templates.end())
      return LIBRA_NODE_NOT_FOUND;
    const auto &payload = kind == LIBRA_SPAWNABLE_RENDER_CONFIGURATION
                              ? found->renderConfiguration
                          : kind == LIBRA_SPAWNABLE_GPU_CONTAINERS
                              ? found->gpuContainersJson
                              : found->gpuProgramsJson;
    *outByteCount = payload.size();
    if (!destination)
      return LIBRA_OK;
    if (capacity < payload.size())
      return LIBRA_INSUFFICIENT_WORK;
    if (!payload.empty())
      std::memcpy(destination, payload.data(), payload.size());
    return LIBRA_OK;
  });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_copy_root_payload(
    LibraSpawnableHostHandle host, uint32_t kind, uint8_t *destination,
    uint64_t capacity, uint64_t *outByteCount) {
  if (!outByteCount)
    return LIBRA_INVALID_ARGUMENT;
  *outByteCount = 0;
  if ((!destination && capacity != 0) ||
      (kind != LIBRA_SPAWNABLE_RENDER_CONFIGURATION &&
       kind != LIBRA_SPAWNABLE_GPU_CONTAINERS &&
       kind != LIBRA_SPAWNABLE_GPU_PROGRAMS))
    return LIBRA_INVALID_ARGUMENT;
  return guardedSpawnable(host, [&]() -> LibraResult {
    auto native = findSpawnableHost(host);
    if (!native)
      return LIBRA_INVALID_HANDLE;
    const auto &payload = kind == LIBRA_SPAWNABLE_RENDER_CONFIGURATION
                              ? native->package().rootRenderConfiguration
                          : kind == LIBRA_SPAWNABLE_GPU_CONTAINERS
                              ? native->package().rootGpuContainersJson
                              : native->package().rootGpuProgramsJson;
    *outByteCount = payload.size();
    if (!destination)
      return LIBRA_OK;
    if (capacity < payload.size())
      return LIBRA_INSUFFICIENT_WORK;
    if (!payload.empty())
      std::memcpy(destination, payload.data(), payload.size());
    return LIBRA_OK;
  });
}
