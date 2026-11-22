#include "LbSpawnableHostInternal.h"
#include <algorithm>
#include <cmath>
#include <limits>
#include <new>
#include <optional>
#include <utility>
#include <vector>

namespace liberafx {
using namespace spawnable_host_detail;
auto SpawnableHost::Impl::nextDeferredTime(double &out) -> Result {
  out = std::numeric_limits<double>::infinity();
  if (!started) {
    return Result::Ok;
  }
  auto pending = false;
  auto result = lifecycle->nextDeferredSignal(out, pending);
  if (result != Result::Ok) {
    return result;
  }
  if (!pending) {
    out = std::numeric_limits<double>::infinity();
  }
  return Result::Ok;
}

auto SpawnableHost::Impl::nextChildDeferredTime(double &out) -> Result {
  out = std::numeric_limits<double>::infinity();
  for (const auto &statePointer : templates) {
    const auto &state = *statePointer;
    for (const auto &instance : state.instances) {
      auto due = double{};
      auto pending = false;
      auto result = state.pool->nextDeferredSignal(instance.id, due, pending);
      if (result != Result::Ok) {
        return result;
      }
      if (pending) {
        out = std::min(out, due);
      }
    }
  }
  return Result::Ok;
}

auto SpawnableHost::Impl::nextScheduledTime(double &out) -> Result {
  out = std::numeric_limits<double>::infinity();
  auto result = Result::Ok;
  if (started) {
    auto scheduled = ScheduledEvent{};
    auto hasScheduled = false;
    result = lifecycle->nextScheduled(scheduled, hasScheduled);
    if (result != Result::Ok) {
      return result;
    }
    if (hasScheduled) {
      out = scheduled.time;
    }
  }
  auto due = double{};
  result = nextDeferredTime(due);
  if (result != Result::Ok) {
    return result;
  }
  out = std::min(out, due);
  result = nextChildDeferredTime(due);
  if (result != Result::Ok) {
    return result;
  }
  out = std::min(out, due);
  return Result::Ok;
}

auto SpawnableHost::Impl::dispatchScheduled(double at) -> Result {
  for (auto round = 0u; round < 128; ++round) {
    if (rootForced) {
      return Result::Ok;
    }
    auto scheduled = ScheduledEvent{};
    auto hasScheduled = false;
    auto result = lifecycle->takeScheduled(scheduled, hasScheduled);
    if (result != Result::Ok) {
      return result;
    }
    if (hasScheduled) {
      if (scheduled.time != at) {
        return Result::InvalidArgument;
      }
      auto found = std::find_if(
          scheduledRoutes.begin(), scheduledRoutes.end(),
          [&](const auto &item) { return item.token == scheduled.token; });
      if (found == scheduledRoutes.end()) {
        return Result::InvalidBinary;
      }
      auto continuation = std::move(found->continuation);
      scheduledRoutes.erase(found);
      result = spawnRoute(continuation, at);
      if (result != Result::Ok) {
        return result;
      }
    } else {
      auto due = double{};
      auto hasSignal = false;
      result = lifecycle->nextDeferredSignal(due, hasSignal);
      if (result != Result::Ok) {
        return result;
      }
      if (hasSignal && due == at) {
        result =
            lifecycle->dispatchDeferredSignal(at, options.instructionBudget);
        if (result != Result::Ok) {
          return result;
        }
      } else {
        auto childDue = double{};
        result = nextChildDeferredTime(childDue);
        if (result != Result::Ok) {
          return result;
        }
        if (childDue != at) {
          return Result::Ok;
        }
        for (auto &statePointer : templates) {
          auto &state = *statePointer;
          auto ids = std::vector<GeneratorInstanceId>{};
          for (const auto &instance : state.instances) {
            ids.push_back(instance.id);
          }
          for (auto id : ids) {
            auto dueTime = double{};
            auto pending = false;
            result = state.pool->nextDeferredSignal(id, dueTime, pending);
            if (result != Result::Ok) {
              return result;
            }
            if (!pending || dueTime != at) {
              continue;
            }
            auto instance = findInstance(state, id);
            if (!instance) {
              return Result::InvalidHandle;
            }
            auto ownerContext =
                OwnerTagOperation(currentOwnerTag, instance->scopeTag);
            result = state.pool->dispatchDeferredSignal(
                id, at, options.instructionBudget);
            if (result != Result::Ok) {
              return result;
            }
          }
        }
      }
    }
    result = pump(at);
    if (result != Result::Ok) {
      return result;
    }
  }
  auto next = double{};
  auto result = nextScheduledTime(next);
  if (result != Result::Ok) {
    return result;
  }
  return next == at ? Result::InstructionBudgetExceeded : Result::Ok;
}

auto SpawnableHost::Impl::notifyOwnerDestroy(double at) -> Result {
  if ((!rootEnding && std::none_of(
                          templates.begin(), templates.end(),
                          [](const auto &state) {
                            return std::any_of(
                                state->instances.begin(), state->instances.end(),
                                [](const auto &instance) {
                                  return instance.retiring;
                                });
                          })) ||
      rootForced) {
    return Result::Ok;
  }
  for (auto round = 0u; round < 128; ++round) {
    auto pending = std::vector<OwnerNotice>{};
    auto ownerTags = std::vector<std::uint64_t>{};
    if (rootEnding) {
      ownerTags.push_back(0);
    }
    for (const auto &statePointer : templates) {
      const auto &state = *statePointer;
      for (const auto &instance : state.instances) {
        if (instance.retiring) {
          ownerTags.push_back(instance.scopeTag);
        }
      }
    }
    for (const auto ownerTag : ownerTags) {
      for (const auto &statePointer : templates) {
        const auto &state = *statePointer;
        for (const auto &instance : state.instances) {
          if (instance.ownerTag != ownerTag || instance.retiring) {
            continue;
          }
          const auto notified = std::any_of(
              ownerNotices.begin(), ownerNotices.end(), [&](const auto &item) {
                return item.instanceId == instance.id &&
                       sameId(item.containerId.data(),
                              state.data->containerId.data());
              });
          if (!notified) {
            pending.push_back({state.data->containerId, instance.id});
          }
        }
      }
    }
    if (pending.empty()) {
      return Result::Ok;
    }
    for (const auto &item : pending) {
      auto state = findTemplate(item.containerId.data());
      if (!state) {
        return Result::InvalidBinary;
      }
      auto instance = findInstance(*state, item.instanceId);
      if (!instance) {
        continue;
      }
      if (state->data->lifecycle &&
          state->data->lifecycle->ownerDestroyNodeId) {
        auto ownerContext =
            OwnerTagOperation(currentOwnerTag, instance->scopeTag);
        auto result = state->pool->ownerDestroy(
            item.instanceId, state->data->lifecycle->ownerDestroyNodeId->data(),
            at, options.instructionBudget);
        if (result != Result::Ok) {
          return result;
        }
      }
      ownerNotices.push_back(item);
    }
    auto result = pump(at);
    if (result == Result::Ok) {
      result = dispatchScheduled(at);
    }
    if (result != Result::Ok) {
      return result;
    }
  }
  return Result::InstructionBudgetExceeded;
}

auto SpawnableHost::Impl::beginAutoEnd(double at) -> Result {
  if (rootEnding || rootFinished || !packageData.autoDestroyWhenIdle ||
      (templates.empty() && packageData.cpuPrograms.empty())) {
    return Result::Ok;
  }
  if (cpuParticles && (cpuParticles->canEmit() || cpuParticles->activeCount() != 0 ||
      cpuParticles->hasPendingRoutes()))
  {
    return Result::Ok;
  }
  for (const auto &state : templates) {
    if (state->pool->activeCount() != 0) {
      return Result::Ok;
    }
  }
  if (!scheduledRoutes.empty()) {
    return Result::Ok;
  }
  auto due = double{};
  auto result = nextDeferredTime(due);
  if (result != Result::Ok || std::isfinite(due)) {
    return result;
  }
  result = lifecycle->requestNaturalEnd();
  if (result != Result::Ok) {
    return result;
  }
  rootEnding = true;
  result = notifyOwnerDestroy(at);
  if (result == Result::Ok) {
    result = finishIfQuiescent();
  }
  return result;
}

auto SpawnableHost::Impl::finishIfQuiescent() -> Result {
  auto result = finishRetiredInstances();
  if (result != Result::Ok) {
    return result;
  }
  if (!rootEnding) {
    result = beginAutoEnd(rootAge);
    if (result != Result::Ok) {
      return result;
    }
  }
  if (!rootEnding || rootFinished || activeCount() != 0 ||
      !scheduledRoutes.empty() || (cpuParticles && cpuParticles->hasPendingRoutes())) {
    return Result::Ok;
  }
  auto due = double{};
  result = nextDeferredTime(due);
  if (result != Result::Ok) {
    return result;
  }
  if (std::isfinite(due)) {
    return Result::Ok;
  }
  if (!rootDestroySent && packageData.onDestroyNodeId) {
    result = lifecycle->destroy(packageData.onDestroyNodeId->data(),
                                options.instructionBudget);
    if (result != Result::Ok) {
      return result;
    }
    rootDestroySent = true;
    result = pump(rootAge);
    if (result == Result::Ok) {
      result = dispatchScheduled(rootAge);
    }
    if (result == Result::Ok) {
      result = notifyOwnerDestroy(rootAge);
    }
    if (result != Result::Ok) {
      return result;
    }
    if (activeCount() != 0 || !scheduledRoutes.empty()) {
      return Result::Ok;
    }
    result = nextDeferredTime(due);
    if (result != Result::Ok || std::isfinite(due)) {
      return result;
    }
  }
  result = lifecycle->finish();
  if (result == Result::Ok) {
    rootFinished = true;
  }
  return result;
}

auto SpawnableHost::Impl::finishRetiredInstances() -> Result {
  for (auto round = 0u; round < 128; ++round) {
    auto finalized = false;
    for (auto &statePointer : templates) {
      auto &state = *statePointer;
      for (const auto &instance : state.instances) {
        if (!instance.retiring) {
          continue;
        }
        auto hasDescendants = false;
        for (const auto &candidate : templates) {
          hasDescendants = hasDescendants ||
              std::any_of(candidate->instances.begin(), candidate->instances.end(),
                          [&](const auto &child) {
                            return child.ownerTag == instance.scopeTag;
                          });
        }
        const auto hasContinuations = std::any_of(
            scheduledRoutes.begin(), scheduledRoutes.end(),
            [&](const auto &route) { return route.ownerTag == instance.scopeTag; });
        if (hasDescendants || hasContinuations ||
            (instance.cpuParticles &&
             (instance.cpuParticles->activeCount() != 0 ||
              instance.cpuParticles->hasPendingRoutes()))) {
          continue;
        }
        auto mutableInstance = findInstance(state, instance.id);
        if (!mutableInstance) {
          return Result::InvalidHandle;
        }
        auto result = preserveCpuEvents(state, *mutableInstance);
        if (result != Result::Ok) {
          return result;
        }
        if (!mutableInstance->destroyPublished) {
          result = publishNaturalDestroy(state, *mutableInstance, rootAge,
                                         mutableInstance->retirementSeed);
          if (result != Result::Ok) {
            return result;
          }
          mutableInstance->destroyPublished = true;
        }
        result = state.pool->finalize(instance.id);
        if (result == Result::QueueRejected) {
          continue;
        }
        if (result != Result::Ok) {
          return result;
        }
        finalized = true;
      }
    }
    if (!finalized) {
      return Result::Ok;
    }
    auto result = pump(rootAge);
    if (result != Result::Ok) {
      return result;
    }
    auto due = double{};
    result = nextScheduledTime(due);
    if (result == Result::Ok && due == rootAge) {
      result = dispatchScheduled(rootAge);
    }
    if (result != Result::Ok) {
      return result;
    }
  }
  return Result::InstructionBudgetExceeded;
}

auto SpawnableHost::Impl::preserveCpuEvents(TemplateState &state,
                                            InstanceInfo &instance) -> Result {
  if (!instance.cpuParticles) {
    return Result::Ok;
  }
  const auto pending = instance.cpuParticles->pendingEventCount();
  if (pending > retiredCpuEvents.max_size() - retiredCpuEvents.size()) {
    return Result::InsufficientWork;
  }
  try {
    retiredCpuEvents.reserve(retiredCpuEvents.size() + pending);
    auto event = CpuParticleEvent{};
    while (instance.cpuParticles->popEvent(event)) {
      retiredCpuEvents.push_back(
          {state.data->containerId, instance.id, event});
    }
    return Result::Ok;
  } catch (const std::bad_alloc &) {
    return Result::InsufficientWork;
  }
}

auto SpawnableHost::Impl::applyAuthoredForce() -> Result {
  if (rootForced) {
    return Result::Ok;
  }
  auto forceScope = std::uint8_t{};
  auto result =
      rootInstance ? rootInstance->pendingForceRequest(forceScope) : Result::Ok;
  if (result != Result::Ok) {
    return result;
  }
  if (forceScope != 0) {
    return forceTerminate({});
  }
  for (auto &state : templates) {
    for (auto index = std::size_t{}; index < state->instances.size();) {
      auto id = state->instances[index].id;
      result = state->pool->pendingForceRequest(id, forceScope);
      // Reset retires slots before queued Stop events clear host bookkeeping.
      if (result == Result::InvalidHandle) {
        ++index;
        continue;
      }
      if (result != Result::Ok) {
        return result;
      }
      if (forceScope == 2) {
        return forceTerminate({});
      }
      if (forceScope == 0) {
        ++index;
        continue;
      }
      auto scope = SpawnableScope{};
      scope.kind = SpawnableScopeKind::Instance;
      std::memcpy(scope.containerId, state->data->containerId.data(), 16);
      scope.instanceId = id;
      result = forceTerminate(scope);
      if (result != Result::Ok) {
        return result;
      }
    }
  }
  return Result::Ok;
}

auto SpawnableHost::Impl::forceTerminate(const SpawnableScope &scope)
    -> Result {
  auto &host = *this;
  auto at = host.started ? host.rootAge : 0;
  if (scope.kind == SpawnableScopeKind::Effect) {
    if (host.rootForced) {
      return Result::Ok;
    }
    // No asynchronous pool job can exist here: pools are private and all
    // evaluations complete inline inside guarded owner operations.
    if (host.started && !host.rootFinished && !host.rootStopped) {
      auto result = host.lifecycle->stop();
      if (result != Result::Ok) {
        return result;
      }
    }
    for (auto &state : host.templates) {
      auto result = state->pool->forceTerminateAll();
      if (result != Result::Ok) {
        return result;
      }
      for (auto &instance : state->instances)
      {
          const auto recycled = host.recycleCpuWorld(*state, instance);
          if (recycled != Result::Ok)
          {
              return recycled;
          }
      }
      state->instances.clear();
    }
    host.scheduledRoutes.clear();
    host.ownerNotices.clear();
    host.retiredCpuEvents.clear();
    if (host.gameIo) {
      auto ignored = NamedIoEvent{};
      while (host.gameIo->popEvent(ignored)) {
      }
    }
    // Release facades before the instance and its caller-owned storage.
    // Even a failed first start can have initialized custom/deferred state.
    host.lifecycle.reset();
    if (host.retainForPool) {
      // Cleanup instance state immediately, retaining the preallocated facade
      // and slabs. Pool reset will rebind its handles before the next lease.
      if (host.gameIo) (void)host.rootInstance->setSignalObserver(nullptr,nullptr);
      const auto reset=host.rootInstance->deinitialize();
      if (reset!=Result::Ok) return reset;
    } else {
      host.gameIo.reset();
      host.rootInstance.reset();
      host.rootDefaults.release();
      host.rootWork.release();
    }

    host.eventHead = host.eventCount = 0;
    host.pendingForceEvents.clear();
    host.pendingForceEventHead = 0;
    host.discardChildGameEvents(scope);
    host.pendingPump = false;
    if (host.cpuParticles)
    {
      host.cpuParticles->clear();
    }
    host.rootStopped = host.rootFinished = host.rootForced = true;
    return host.appendEvent(SpawnableHostEventKind::ForceTerminated, nullptr, 0,
                            at, 0);
  }

  auto state = host.findTemplate(scope.containerId);
  if (!state) {
    return Result::InvalidHandle;
  }
  struct SubtreeItem final {
    TemplateState *state = nullptr;
    GeneratorInstanceId id = 0;
    std::uint64_t scopeTag = 0;
  };
  auto subtree = std::vector<SubtreeItem>{{state, scope.instanceId, 0}};
  if (auto instance = host.findInstance(*state, scope.instanceId)) {
    subtree[0].scopeTag = instance->scopeTag;
    for (auto cursor = std::size_t{}; cursor < subtree.size(); ++cursor) {
      const auto ownerTag = subtree[cursor].scopeTag;
      for (auto &candidate : host.templates) {
        for (const auto &child : candidate->instances) {
          if (child.ownerTag == ownerTag) {
            subtree.push_back({candidate.get(), child.id, child.scopeTag});
          }
        }
      }
      if (subtree.size() >
          host.pendingForceEvents.max_size() - host.pendingForceEvents.size()) {
        return Result::InsufficientWork;
      }
      host.pendingForceEvents.reserve(host.pendingForceEvents.size() + subtree.size());
    }
  }
  auto hasPending = false;
  auto sourceInSubtree = [&](const Impl::ScheduledRoute &item) {
    return std::any_of(subtree.begin(), subtree.end(), [&](const auto &owned) {
      return item.sourceInstanceId == owned.id &&
             item.continuation.route &&
             sameId(item.continuation.route->sourceContainerId.data(),
                    owned.state->data->containerId.data());
    });
  };
  auto ownerInSubtree = [&](std::uint64_t ownerTag) {
    return std::any_of(subtree.begin(), subtree.end(), [&](const auto &owned) {
      return ownerTag == owned.scopeTag && owned.scopeTag != 0;
    });
  };
  auto routeAffected = [&](const Impl::ScheduledRoute &item) {
    return sourceInSubtree(item) || ownerInSubtree(item.ownerTag);
  };
  hasPending = std::any_of(host.scheduledRoutes.begin(),
                           host.scheduledRoutes.end(), routeAffected);
  auto matches = [&](const SpawnableHostEvent &event) {
    return event.kind != SpawnableHostEventKind::ForceTerminated &&
           std::any_of(subtree.begin(), subtree.end(), [&](const auto &owned) {
             return event.instanceId == owned.id &&
                    sameId(event.containerId,
                           owned.state->data->containerId.data());
           });
  };
  auto removed = std::size_t{};
  for (auto index = std::size_t{}; index < host.eventCount; ++index) {
    if (matches(host.events[(host.eventHead + index) % host.events.size()])) {
      ++removed;
    }
  }
  const auto wasActive = std::any_of(
      subtree.begin(), subtree.end(), [&](const auto &owned) {
        return host.findInstance(*owned.state, owned.id) != nullptr;
      });

  for (auto &candidate : host.templates) {
    for (const auto &owned : subtree) {
      if (!owned.scopeTag) {
        continue;
      }
      auto result = candidate->pool->cancelSpawnsOwned(owned.scopeTag);
      if (result != Result::Ok) {
        return result;
      }
    }
  }
  for (const auto &item : host.scheduledRoutes) {
    if (!routeAffected(item)) {
      continue;
    }
    if (host.lifecycle) {
      const auto result = host.lifecycle->cancelScheduled(item.token);
      if (result != Result::Ok) {
        return result;
      }
    }
  }
  host.scheduledRoutes.erase(
      std::remove_if(host.scheduledRoutes.begin(), host.scheduledRoutes.end(),
                     routeAffected),
      host.scheduledRoutes.end());

  for (auto index = subtree.size(); index > 0; --index) {
    const auto &owned = subtree[index - 1];
    const auto result = owned.state->pool->forceTerminate(owned.id);
    if (result != Result::Ok) {
      return result;
    }
  }
  for (const auto &owned : subtree) {
    auto childScope = SpawnableScope{};
    childScope.kind = SpawnableScopeKind::Instance;
    std::memcpy(childScope.containerId, owned.state->data->containerId.data(), 16);
    childScope.instanceId = owned.id;
    host.discardChildGameEvents(childScope);
    if (auto *instance = host.findInstance(*owned.state, owned.id))
    {
        const auto recycled = host.recycleCpuWorld(*owned.state, *instance);
        if (recycled != Result::Ok)
        {
            return recycled;
        }
    }
    owned.state->instances.erase(
        std::remove_if(owned.state->instances.begin(),
                       owned.state->instances.end(), [&](const auto &item) {
                         return item.id == owned.id;
                       }),
        owned.state->instances.end());
    host.ownerNotices.erase(
        std::remove_if(host.ownerNotices.begin(), host.ownerNotices.end(),
                       [&](const auto &item) {
                         return item.instanceId == owned.id &&
                                sameId(item.containerId.data(),
                                       owned.state->data->containerId.data());
                       }),
        host.ownerNotices.end());
  }
  host.retiredCpuEvents.erase(
      std::remove_if(host.retiredCpuEvents.begin(), host.retiredCpuEvents.end(),
                     [&](const auto &item) {
                       return std::any_of(
                           subtree.begin(), subtree.end(), [&](const auto &owned) {
                             return item.instanceId == owned.id &&
                                    sameId(item.containerId.data(),
                                           owned.state->data->containerId.data());
                           });
                     }),
      host.retiredCpuEvents.end());
  auto kept = std::size_t{};
  for (auto index = std::size_t{}; index < host.eventCount; ++index) {
    const auto &event =
        host.events[(host.eventHead + index) % host.events.size()];
    if (matches(event)) {
      continue;
    }
    if (kept != index) {
      host.events[(host.eventHead + kept) % host.events.size()] = event;
    }
    ++kept;
  }
  host.eventCount = kept;
  // Do not pump: that could execute unrelated natural routes while cancelling.
  // Cleanup notices are retained independently of the bounded lifecycle ring.
  if (!wasActive && !removed && !hasPending) {
    return Result::Ok;
  }
  for (auto index = subtree.size(); index > 0; --index) {
    const auto &owned = subtree[index - 1];
    const auto result = host.appendForceEvent(
        owned.state->data->containerId.data(), owned.id, at);
    if (result != Result::Ok) {
      return result;
    }
  }
  return Result::Ok;
}
} // namespace liberafx
