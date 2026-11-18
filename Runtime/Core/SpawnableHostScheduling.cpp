#include "SpawnableHostInternal.h"
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
  if (started) {
    auto scheduled = ScheduledEvent{};
    auto hasScheduled = false;
    auto result = lifecycle->nextScheduled(scheduled, hasScheduled);
    if (result != Result::Ok) {
      return result;
    }
    if (hasScheduled) {
      out = scheduled.time;
    }
  }
  auto due = double{};
  auto result = nextDeferredTime(due);
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
  if (!rootEnding || rootForced) {
    return Result::Ok;
  }
  for (auto round = 0u; round < 128; ++round) {
    auto pending = std::vector<OwnerNotice>{};
    for (const auto &statePointer : templates) {
      const auto &state = *statePointer;
      for (const auto &instance : state.instances) {
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
      templates.empty()) {
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
  if (!rootEnding || rootFinished || activeCount() != 0 ||
      !scheduledRoutes.empty()) {
    return Result::Ok;
  }
  auto due = double{};
  auto result = nextDeferredTime(due);
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
      state->instances.clear();
    }
    host.scheduledRoutes.clear();
    host.ownerNotices.clear();
    if (host.gameIo) {
      auto ignored = NamedIoEvent{};
      while (host.gameIo->popEvent(ignored)) {
      }
    }
    // Release facades before the instance and its caller-owned storage.
    // Even a failed first start can have initialized custom/deferred state.
    host.lifecycle.reset();
    host.gameIo.reset();
    host.rootInstance.reset();
    host.rootDefaults.release();
    host.rootWork.release();
    host.eventHead = host.eventCount = 0;
    host.discardChildGameEvents(scope);
    host.pendingPump = false;
    host.rootStopped = host.rootFinished = host.rootForced = true;
    return host.appendEvent(SpawnableHostEventKind::ForceTerminated, nullptr, 0,
                            at, 0);
  }

  auto state = host.findTemplate(scope.containerId);
  if (!state) {
    return Result::InvalidHandle;
  }
  auto active = host.findInstance(*state, scope.instanceId) != nullptr;
  auto ownsPending = [&](const Impl::ScheduledRoute &item) {
    return item.sourceInstanceId == scope.instanceId &&
           item.continuation.route &&
           sameId(item.continuation.route->sourceContainerId.data(),
                  scope.containerId);
  };
  auto hasPending = std::any_of(host.scheduledRoutes.begin(),
                                host.scheduledRoutes.end(), ownsPending);
  auto matches = [&](const SpawnableHostEvent &event) {
    return event.instanceId == scope.instanceId &&
           sameId(event.containerId, scope.containerId) &&
           event.kind != SpawnableHostEventKind::ForceTerminated;
  };
  auto removed = std::size_t{};
  for (auto index = std::size_t{}; index < host.eventCount; ++index) {
    if (matches(host.events[(host.eventHead + index) % host.events.size()])) {
      ++removed;
    }
  }
  if ((active || removed || hasPending) &&
      host.eventCount - removed == host.events.size()) {
    return Result::QueueRejected;
  }
  auto result = state->pool->forceTerminate(scope.instanceId);
  if (result != Result::Ok) {
    return result;
  }
  host.discardChildGameEvents(scope);
  for (const auto &item : host.scheduledRoutes) {
    if (!ownsPending(item)) {
      continue;
    }
    // Host's guard establishes the lifecycle job boundary before any purge.
    result = host.lifecycle->cancelScheduled(item.token);
    if (result != Result::Ok) {
      return result;
    }
  }
  host.scheduledRoutes.erase(std::remove_if(host.scheduledRoutes.begin(),
                                            host.scheduledRoutes.end(),
                                            ownsPending),
                             host.scheduledRoutes.end());
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
  state->instances.erase(std::remove_if(state->instances.begin(),
                                        state->instances.end(),
                                        [&](const auto &item) {
                                          return item.id == scope.instanceId;
                                        }),
                         state->instances.end());
  host.ownerNotices.erase(
      std::remove_if(host.ownerNotices.begin(), host.ownerNotices.end(),
                     [&](const auto &item) {
                       return item.instanceId == scope.instanceId &&
                              sameId(item.containerId.data(),
                                     scope.containerId);
                     }),
      host.ownerNotices.end());
  // Do not pump: that could execute unrelated natural routes while cancelling.
  // Root auto-end is deliberately left to a subsequent normal advance.
  return active || removed || hasPending
             ? host.appendEvent(SpawnableHostEventKind::ForceTerminated,
                                scope.containerId, scope.instanceId, at, 0)
             : Result::Ok;
}
} // namespace liberafx
