#include "LbSpawnableHostInternal.h"
#include "liberafx/host/LbPackagePreviewContract.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <optional>
#include <utility>
#include <vector>

namespace liberafx {
using namespace spawnable_host_detail;
auto SpawnableHost::advance(double at, double delta) -> Result {
  auto &host = *impl_;
  if (host.operationActive) {
    return Result::Busy;
  }
  auto operation = OwnerOperation(host.operationActive);
  const auto previousRealElapsed = host.realElapsed;
  if (!std::isfinite(at) || at < host.realElapsed || !std::isfinite(delta) || delta < 0) return Result::InvalidArgument;
  const auto realAt = at;
  const auto scaledAt = host.effectElapsed + (at - host.realElapsed) * host.timeScale;
  const auto scaledDelta = delta * host.timeScale;
  if (!std::isfinite(scaledAt) || !std::isfinite(scaledDelta)) return Result::NonFinite;
  at = scaledAt;
  if (!std::isfinite(at) || at < 0 || (host.time >= 0 && at < host.time) ||
      at < host.rootAge || !std::isfinite(delta) || delta < 0) {
    return Result::InvalidArgument;
  }
  if (host.rootForced) {
    return Result::NotReady;
  }
  if (host.failure != Result::Ok) {
    return host.failure;
  }
  if (host.rootStopped) {
    return Result::NotReady;
  }
  try {
    host.realElapsed = realAt;
    host.effectElapsed = scaledAt;
    host.realFrameDelta = delta;
    delta = scaledDelta;
    host.frameDelta = delta;
    auto result = Result::Ok;
    if (host.pendingPump) {
      result = host.pump(host.rootAge);
      if (result == Result::Ok) {
        result = host.dispatchScheduled(host.rootAge);
      }
      if (result == Result::Ok) {
        result = host.pump(host.rootAge);
      }
      if (result == Result::Ok) {
        result = host.beginAutoEnd(host.rootAge);
      }
      if (result == Result::Ok) {
        result = host.notifyOwnerDestroy(host.rootAge);
      }
      if (result == Result::Ok) {
        result = host.finishIfQuiescent();
      }
      if (result != Result::Ok) {
        if (result != Result::QueueRejected) {
          host.failure = result;
        }
        return result;
      }
    }
    result = host.startRoot(delta);
    if (result != Result::Ok) {
      if (result == Result::QueueRejected) {
        host.pendingPump = true;
      } else {
        host.failure = result;
      }
      return result;
    }
    if (!host.rootFinished && at == host.rootAge && host.realElapsed != previousRealElapsed) {
      result = host.rootInstance->setTimeContext(at, delta, host.realTimeAt(at), host.realTimeAt(at), host.realFrameDelta);
      if (result == Result::Ok) result = host.rootInstance->refreshTimeContext();
      if (result != Result::Ok) return result;
      struct RealRefresh { double effect, delta, real, age, realDelta; };
      for (auto& state : host.templates) for (auto& instance : state->instances) {
        if (instance.retiring) continue;
        auto context = RealRefresh{at, delta, host.realElapsed, std::max(0.0, host.realElapsed - instance.realStart), host.realFrameDelta};
        result = state->pool->withInstance(instance.id, [](void* pointer, Instance& value) noexcept -> Result {
          const auto& clock = *static_cast<RealRefresh*>(pointer);
          const auto status = value.setTimeContext(clock.effect, clock.delta, clock.real, clock.age, clock.realDelta);
          return status == Result::Ok ? value.refreshTimeContext() : status;
        }, &context);
        if (result != Result::Ok) return result;
      }
    }
    for (auto round = 0u; !host.rootFinished && round < 128; ++round) {
      auto nextDue = std::numeric_limits<double>::infinity();
      result = host.nextScheduledTime(nextDue);
      if (result != Result::Ok) {
        break;
      }
      auto childExpiry = std::numeric_limits<double>::infinity();
      for (const auto &state : host.templates) {
        for (const auto &instance : state->instances) {
          if (!std::isfinite(instance.life)) {
            continue;
          }
          const auto expiry = instance.start + instance.life;
          if (expiry > host.rootAge && expiry <= at) {
            childExpiry = std::min(childExpiry, expiry);
          }
        }
      }
      const auto next = std::min(at, std::min(nextDue, childExpiry));
      if (next <= host.rootAge) {
        break;
      }
      result = host.evaluateFrame(next);
      if (result == Result::Ok) {
        result = host.dispatchScheduled(next);
      }
      if (result == Result::Ok) {
        result = host.pump(next);
      }
      if (result == Result::Ok) {
        result = host.beginAutoEnd(next);
      }
      if (result == Result::Ok) {
        result = host.notifyOwnerDestroy(next);
      }
      if (result == Result::Ok) {
        result = host.finishIfQuiescent();
      }
      if (result != Result::Ok) {
        break;
      }
      if (next >= at) {
        break;
      }
      if (round == 127) {
        result = Result::InstructionBudgetExceeded;
      }
    }
    if (result == Result::Ok) {
      host.time = at;
    } else if (result == Result::QueueRejected) {
      host.pendingPump = true;
    } else {
      host.failure = result;
    }
    return result;
  } catch (const std::bad_alloc &) {
    host.failure = Result::InsufficientWork;
    return host.failure;
  }
}

auto SpawnableHost::setHostQuality(const HostQualitySnapshot &snapshot)
    -> Result {
  if (impl_->operationActive) {
    return Result::Busy;
  }
  if (impl_->rootForced || !impl_->rootInstance) {
    return Result::NotReady;
  }
  if (static_cast<unsigned>(snapshot.availability) >
          static_cast<unsigned>(HostQualityAvailability::Unsupported) ||
      (snapshot.availability == HostQualityAvailability::Known &&
       (static_cast<unsigned>(snapshot.tier) >
            static_cast<unsigned>(HostQualityTier::Cinematic) ||
        static_cast<unsigned>(snapshot.source) >
            static_cast<unsigned>(HostQualitySource::PlatformPolicy)))) {
    return Result::InvalidValueType;
  }
  auto result = impl_->rootInstance->setHostQuality(snapshot);
  if (result != Result::Ok) {
    return result;
  }
  for (auto &state : impl_->templates) {
    for (const auto &instance : state->instances) {
      result = state->pool->setHostQuality(instance.id, snapshot);
      if (result != Result::Ok) {
        return result;
      }
    }
  }
  impl_->quality = snapshot;
  return Result::Ok;
}

auto SpawnableHost::setNodeParameter(const std::uint8_t containerId[16],
                                     const std::uint8_t nodeId[16],
                                     std::uint32_t inputSlot,
                                     std::uint32_t component,
                                     bool isInt32,
                                     std::int32_t intValue,
                                     double floatValue) -> Result
{
  if (!nodeId)
  {
    return Result::InvalidArgument;
  }
  auto update = host::ParameterDeltaUpdate{};
  if (containerId)
  {
    std::memcpy(update.containerId.data(), containerId, 16);
  }
  std::memcpy(update.nodeId.data(), nodeId, 16);
  update.inputSlot = inputSlot;
  update.component = component;
  update.isInt32 = isInt32;
  update.intValue = intValue;
  update.floatValue = floatValue;
  return setNodeParameters(&update, 1);
}

auto SpawnableHost::setNodeParameters(const host::ParameterDeltaUpdate *updates,
                                      std::size_t count) -> Result
{
  if (impl_->operationActive)
  {
    return Result::Busy;
  }
  if (impl_->rootForced || !impl_->rootInstance)
  {
    return Result::NotReady;
  }
  if (count != 0 && !updates)
  {
    return Result::InvalidArgument;
  }
  auto operation = OwnerOperation(impl_->operationActive);
  using TemplateState = Impl::TemplateState;
  struct StagedOverrides final
  {
    TemplateState *state = nullptr;
    std::vector<TemplateState::ParameterOverride> values;
  };
  struct StagedCurves final
  {
    TemplateState *state = nullptr;
    spawnable_host_detail::CurveStore values;
  };
  struct CurveWrite final
  {
    StagedCurves *staged = nullptr;
    GeneratorInstanceId instanceId = 0;
  };
  struct Write final
  {
    TemplateState *state = nullptr;
    GeneratorInstanceId instanceId = 0;
    ParameterHandle handle{};
    double previous = 0;
    bool isInt32 = false;
    std::int32_t intValue = 0;
    double floatValue = 0;
  };
  try
  {
    auto staged = std::vector<StagedOverrides>{};
    auto curveStaged = std::vector<StagedCurves>{};
    auto curveWrites = std::vector<CurveWrite>{};
    auto writes = std::vector<Write>{};
    for (auto index = std::size_t{}; index < count; ++index)
    {
      const auto &update = updates[index];
      if (update.inputSlot > 255 || update.component > 255)
      {
        return Result::InvalidArgument;
      }
      if (!update.reset && !update.isInt32 && !std::isfinite(update.floatValue))
      {
        return Result::NonFinite;
      }
      auto *state = static_cast<TemplateState *>(nullptr);
      if (isNonzero(update.containerId.data()))
      {
        for (auto &candidate : impl_->templates)
        {
          if (candidate->data->containerId == update.containerId)
          {
            state = candidate.get();
            break;
          }
        }
        if (!state)
        {
          return Result::NodeNotFound;
        }
      }
      const auto &program = state ? state->program : impl_->parentProgram;
      if (update.isCurve)
      {
        auto nodeIndex = std::uint32_t{};
        auto result = Instance::findCurve(program, update.nodeId.data(), nodeIndex);
        if (result != Result::Ok)
        {
          return result;
        }
        if (update.reset && !update.curveKeys.empty())
        {
          return Result::InvalidArgument;
        }
        result = Instance::validateCurveKeys(update.curveKeys.data(), update.curveKeys.size());
        if (result != Result::Ok)
        {
          return result;
        }
        auto stagedItem = std::find_if(curveStaged.begin(), curveStaged.end(),
            [state](const auto &item) { return item.state == state; });
        if (stagedItem == curveStaged.end())
        {
          auto &original = state ? state->curveOverrides : impl_->rootCurveOverrides;
          curveStaged.push_back({state, {}});
          stagedItem = curveStaged.end() - 1;
          stagedItem->values.items = original.items;
        }
        auto &items = stagedItem->values.items;
        auto existing = std::find_if(items.begin(), items.end(),
            [&](const auto &item) { return item.nodeId == update.nodeId; });
        if (update.reset)
        {
          if (existing != items.end())
          {
            items.erase(existing);
          }
        }
        else if (existing == items.end())
        {
          items.push_back({update.nodeId, nodeIndex, update.curveKeys});
        }
        else
        {
          existing->keys = update.curveKeys;
        }
        continue;
      }
      auto resetValue = double{};
      auto result = update.reset
          ? Instance::defaultParameter(program, update.nodeId.data(),
                static_cast<std::uint8_t>(update.inputSlot),
                static_cast<std::uint8_t>(update.component), resetValue)
          : Instance::validateParameter(program, update.nodeId.data(),
                static_cast<std::uint8_t>(update.inputSlot),
                static_cast<std::uint8_t>(update.component), update.isInt32,
                update.intValue, update.floatValue);
      if (result != Result::Ok)
      {
        return result;
      }
      auto capture = [&](Instance &instance, Write &write) noexcept -> Result
      {
        auto status = instance.bindParameter(update.nodeId.data(),
            static_cast<std::uint8_t>(update.inputSlot),
            static_cast<std::uint8_t>(update.component), write.handle);
        if (status != Result::Ok)
        {
          return status;
        }
        return instance.parameter(write.handle, write.previous);
      };
      auto write = Write{};
      write.state = state;
      write.isInt32 = !update.reset && update.isInt32;
      write.intValue = update.intValue;
      write.floatValue = update.reset ? resetValue : update.floatValue;
      if (!state)
      {
        result = capture(*impl_->rootInstance, write);
        if (result != Result::Ok)
        {
          return result;
        }
        writes.push_back(write);
        continue;
      }
      auto stagedItem = std::find_if(staged.begin(), staged.end(),
          [state](const auto &item) { return item.state == state; });
      if (stagedItem == staged.end())
      {
        staged.push_back({state, state->parameterOverrides});
        stagedItem = staged.end() - 1;
      }
      auto overrideValue = TemplateState::ParameterOverride{};
      overrideValue.nodeId = update.nodeId;
      overrideValue.inputSlot = update.inputSlot;
      overrideValue.component = update.component;
      overrideValue.isInt32 = update.isInt32;
      overrideValue.intValue = update.intValue;
      overrideValue.floatValue = update.floatValue;
      auto existing = std::find_if(stagedItem->values.begin(), stagedItem->values.end(),
          [&](const auto &item)
          {
            return item.nodeId == overrideValue.nodeId && item.inputSlot == overrideValue.inputSlot &&
                item.component == overrideValue.component;
          });
      if (update.reset)
      {
        if (existing != stagedItem->values.end())
        {
          stagedItem->values.erase(existing);
        }
      }
      else if (existing == stagedItem->values.end())
      {
        stagedItem->values.push_back(overrideValue);
      }
      else
      {
        *existing = overrideValue;
      }
      for (const auto &child : state->instances)
      {
        write.instanceId = child.id;
        struct CaptureContext final
        {
          decltype(capture) *function;
          Write *write;
        } context{&capture, &write};
        result = state->pool->withInstance(child.id,
            [](void *context, Instance &instance) noexcept -> Result
            {
              auto *captureContext = static_cast<CaptureContext *>(context);
              return (*captureContext->function)(instance, *captureContext->write);
            }, &context);
        if (result != Result::Ok)
        {
          return result;
        }
        writes.push_back(write);
      }
    }
    for (auto &item : curveStaged)
    {
      item.values.rebuildViews();
      if (!item.state)
      {
        curveWrites.push_back({&item, 0});
      }
      else
      {
        for (const auto &child : item.state->instances)
        {
          curveWrites.push_back({&item, child.id});
        }
      }
    }
    auto applyCurve = [&](const CurveWrite &write, bool rollback) noexcept -> Result
    {
      auto &item = *write.staged;
      auto &store = rollback ? (item.state ? item.state->curveOverrides : impl_->rootCurveOverrides)
                             : item.values;
      auto update = [&](Instance &instance) noexcept -> Result
      {
        return instance.setCurveOverrides(store.views.data(), store.views.size());
      };
      if (!item.state)
      {
        return update(*impl_->rootInstance);
      }
      return item.state->pool->withInstance(write.instanceId,
          [](void *context, Instance &instance) noexcept -> Result
          {
            return (*static_cast<decltype(update) *>(context))(instance);
          }, &update);
    };
    auto apply = [&](const Write &write, bool rollback) noexcept -> Result
    {
      auto applyInstance = [&](Instance &instance) noexcept -> Result
      {
        if (rollback)
        {
          return instance.setParameter(write.handle, write.previous);
        }
        return write.isInt32 ? instance.setParameterInt32(write.handle, write.intValue)
                             : instance.setParameter(write.handle, write.floatValue);
      };
      if (!write.state)
      {
        return applyInstance(*impl_->rootInstance);
      }
      return write.state->pool->withInstance(write.instanceId,
          [](void *context, Instance &instance) noexcept -> Result
          {
            return (*static_cast<decltype(applyInstance) *>(context))(instance);
          }, &applyInstance);
    };
    for (auto index = std::size_t{}; index < writes.size(); ++index)
    {
      auto result = apply(writes[index], false);
      if (result != Result::Ok)
      {
        while (index != 0)
        {
          --index;
          (void)apply(writes[index], true);
        }
        return result;
      }
    }
    for (auto index = std::size_t{}; index < curveWrites.size(); ++index)
    {
      auto result = applyCurve(curveWrites[index], false);
      if (result != Result::Ok)
      {
        while (index != 0)
        {
          --index;
          (void)applyCurve(curveWrites[index], true);
        }
        for (auto previous = writes.size(); previous != 0;)
        {
          --previous;
          (void)apply(writes[previous], true);
        }
        return result;
      }
    }
    for (auto &item : staged)
    {
      item.state->parameterOverrides.swap(item.values);
    }
    for (auto &item : curveStaged)
    {
      auto &store = item.state ? item.state->curveOverrides : impl_->rootCurveOverrides;
      store.items.swap(item.values.items);
      store.views.swap(item.values.views);
    }
    return Result::Ok;
  }
  catch (const std::bad_alloc &)
  {
    return Result::InsufficientWork;
  }
}

auto SpawnableHost::setTimeScale(double ratio) -> Result {
  if (impl_->operationActive) return Result::Busy;
  if (!std::isfinite(ratio) || ratio < 0 || ratio > 1000000) return Result::InvalidArgument;
  impl_->timeScale = ratio;
  return Result::Ok;
}
auto SpawnableHost::timeSnapshot() const noexcept -> EffectTimeSnapshot {
  return {impl_->realElapsed, impl_->rootAge, impl_->realFrameDelta, impl_->frameDelta, impl_->timeScale};
}

auto SpawnableHost::dispatchSignal(const std::uint8_t nodeId[16]) -> Result {
  auto &host = *impl_;
  if (host.operationActive) {
    return Result::Busy;
  }
  auto operation = OwnerOperation(host.operationActive);
  if (!nodeId || !isNonzero(nodeId)) {
    return Result::InvalidArgument;
  }
  if (!host.started || host.rootEnding || host.rootFinished ||
      host.rootStopped) {
    return Result::NotReady;
  }
  if (host.failure != Result::Ok) {
    return host.failure;
  }
  auto result =
      host.rootInstance->setTimeContext(host.rootAge, host.frameDelta, host.realTimeAt(host.rootAge),
          host.realTimeAt(host.rootAge), host.realFrameDelta);
  if (result == Result::Ok) {
    result = host.lifecycle->validateSignalTime(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.rootInstance->dispatchSignal(nodeId, host.rootAge,
                                               host.options.instructionBudget);
  }
  if (result == Result::Ok) {
    result = host.pump(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.dispatchScheduled(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.beginAutoEnd(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.notifyOwnerDestroy(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.finishIfQuiescent();
  }
  if (result != Result::Ok) {
    host.failure = result;
  }
  return result;
}

auto SpawnableHost::fireGameSignal(const char *name) -> Result {
  auto &host = *impl_;
  if (host.operationActive) {
    return Result::Busy;
  }
  auto operation = OwnerOperation(host.operationActive);
  if (!host.gameIo) {
    return Result::NotReady;
  }
  if (!host.started || host.rootEnding || host.rootFinished ||
      host.rootStopped) {
    return Result::NotReady;
  }
  if (host.failure != Result::Ok) {
    return host.failure;
  }
  auto result =
      host.rootInstance->setTimeContext(host.rootAge, host.frameDelta);
  if (result == Result::Ok) {
    result = host.lifecycle->validateSignalTime(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.gameIo->fireSignal(name, host.rootAge,
                                     host.options.instructionBudget);
  }
  if (result == Result::Ok) {
    result = host.pump(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.dispatchScheduled(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.beginAutoEnd(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.notifyOwnerDestroy(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.finishIfQuiescent();
  }
  if (result != Result::Ok) {
    host.failure = result;
  }
  return result;
}

auto SpawnableHost::setGameProperty(const char *name, const double *values,
                                    std::size_t width) -> Result {
  if (impl_->rootForced) return Result::NotReady;
  return impl_->gameIo ? impl_->gameIo->setProperty(name, values, width)
                       : Result::NotReady;
}

auto SpawnableHost::setInstanceProperty(const SpawnableDebugTarget &target,
                                        const char *name, const double *values,
                                        std::size_t width) -> Result {
  auto &host = *impl_;
  if (host.operationActive) {
    return Result::Busy;
  }
  if (!target.hostGeneration || target.hostGeneration != host.debugGeneration ||
      !validScope(target.scope) ||
      target.scope.kind != SpawnableScopeKind::Instance) {
    return Result::InvalidHandle;
  }
  auto state = host.findTemplate(target.scope.containerId);
  if (!state || !host.findInstance(*state, target.scope.instanceId)) {
    return Result::InvalidHandle;
  }
  auto port = ProgramGameIoPort{};
  if (!host.findPort(*state, name, NamedIoKind::InputProperty, port)) {
    return Result::NodeNotFound;
  }
  if (!values || width != port.width) {
    return Result::InvalidValueType;
  }
  auto captures = std::array<GeneratorCapture, 16>{};
  for (auto lane = std::size_t{}; lane < width; ++lane) {
    auto row = width == 16 ? lane / 4 : 0;
    const auto *id = row ? port.additionalNodeIds[row - 1] : port.nodeId;
    std::memcpy(captures[lane].nodeId, id, 16);
    captures[lane].component =
        static_cast<std::uint8_t>(width == 16 ? lane % 4 : lane);
    captures[lane].value = values[lane];
  }
  return state->pool->setLive(target.scope.instanceId, captures.data(), width);
}

auto SpawnableHost::instanceProperty(const SpawnableDebugTarget &target,
                                     const char *name, double values[16],
                                     std::uint8_t &width) -> Result {
  if (impl_->operationActive) {
    return Result::Busy;
  }
  if (!values) {
    return Result::InvalidArgument;
  }
  if (!target.hostGeneration ||
      target.hostGeneration != impl_->debugGeneration ||
      !validScope(target.scope) ||
      target.scope.kind != SpawnableScopeKind::Instance) {
    return Result::InvalidHandle;
  }
  auto state = impl_->findTemplate(target.scope.containerId);
  if (!state || !impl_->findInstance(*state, target.scope.instanceId)) {
    return Result::InvalidHandle;
  }
  auto port = ProgramGameIoPort{};
  if (!impl_->findPort(*state, name, NamedIoKind::OutputProperty, port) &&
      !impl_->findPort(*state, name, NamedIoKind::InputProperty, port)) {
    return Result::NodeNotFound;
  }
  auto resultValues = std::array<double, 16>{};
  auto rows = port.width == 16 ? 4u : 1u;
  for (auto row = 0u; row < rows; ++row) {
    auto rowWidth = std::uint8_t{};
    const auto *id = row ? port.additionalNodeIds[row - 1] : port.nodeId;
    auto result = state->pool->value(target.scope.instanceId, id,
                                     resultValues.data() + row * 4, rowWidth);
    if (result != Result::Ok) {
      return result;
    }
    if (rowWidth != (port.width == 16 ? 4 : port.width)) {
      return Result::InvalidValueType;
    }
  }
  std::copy_n(resultValues.data(), port.width, values);
  width = port.width;
  return Result::Ok;
}

auto SpawnableHost::fireInstanceSignal(const SpawnableDebugTarget &target,
                                       const char *name) -> Result {
  if (impl_->operationActive) {
    return Result::Busy;
  }
  if (!target.hostGeneration ||
      target.hostGeneration != impl_->debugGeneration ||
      !validScope(target.scope) ||
      target.scope.kind != SpawnableScopeKind::Instance) {
    return Result::InvalidHandle;
  }
  auto state = impl_->findTemplate(target.scope.containerId);
  if (!state || !impl_->findInstance(*state, target.scope.instanceId)) {
    return Result::InvalidHandle;
  }
  auto port = ProgramGameIoPort{};
  if (!impl_->findPort(*state, name, NamedIoKind::InputSignal, port)) {
    return Result::NodeNotFound;
  }
  return notifyEndRequest(target.scope, port.nodeId);
}

auto SpawnableHost::instancePortName(const std::uint8_t containerId[16],
                                     std::size_t index,
                                     const char *&name) const noexcept
    -> Result {
  if (!containerId) {
    return Result::InvalidArgument;
  }
  auto state = impl_->findTemplate(containerId);
  if (!state) {
    return Result::InvalidHandle;
  }
  auto port = ProgramGameIoPort{};
  if (!state->program.gameIoPort(index, port)) {
    return Result::InvalidArgument;
  }
  name = port.name;
  return Result::Ok;
}

auto SpawnableHost::setOwnerMatrix(const double *values, std::size_t width)
    -> Result {
  auto converted = std::array<double, 16>{};
  const auto result = convertCoordinates(impl_->coordinateBasis, SpatialValueKind::Transform, false, values, width, converted.data());
  if (result != Result::Ok) return result;
  if (impl_->rootForced) return Result::NotReady;
  return impl_->gameIo ? impl_->gameIo->setOwnerMatrix(converted.data(), width) : Result::NotReady;
}

auto SpawnableHost::setCoordinateBasis(const CoordinateBasis& basis) -> Result {
  if (impl_->operationActive || impl_->started) return Result::Busy;
  if (!validCoordinateBasis(basis)) return Result::InvalidArgument;
  impl_->coordinateBasis = basis;
  return Result::Ok;
}
auto SpawnableHost::coordinateBasis() const noexcept -> CoordinateBasis { return impl_->coordinateBasis; }
auto SpawnableHost::setGameSpatialProperty(const char* name, SpatialValueKind kind,
    const double* values, std::size_t width) -> Result {
  auto converted = std::array<double, 16>{};
  const auto result = convertCoordinates(impl_->coordinateBasis, kind, false, values, width, converted.data());
  return result == Result::Ok ? setGameProperty(name, converted.data(), width) : result;
}

auto SpawnableHost::requestNaturalEnd() -> Result {
  auto &host = *impl_;
  if (host.operationActive) {
    return Result::Busy;
  }
  auto operation = OwnerOperation(host.operationActive);
  if (!host.started || host.rootStopped) {
    return Result::NotReady;
  }
  if (host.failure != Result::Ok) {
    return host.failure;
  }
  if (host.rootEnding || host.rootFinished) {
    return Result::Ok;
  }
  auto result = host.lifecycle->requestNaturalEnd();
  if (result == Result::Ok) {
    host.rootEnding = true;
    result = host.notifyOwnerDestroy(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.finishIfQuiescent();
  }
  if (result != Result::Ok) {
    host.failure = result;
  }
  return result;
}

auto SpawnableHost::stop() -> Result {
  auto &host = *impl_;
  if (host.operationActive) {
    return Result::Busy;
  }
  auto operation = OwnerOperation(host.operationActive);
  if (host.rootStopped) {
    return Result::NotReady;
  }
  for (const auto &state : host.templates) {
    auto result = state->pool->reset();
    if (result != Result::Ok) {
      return result;
    }
  }
  auto result = host.pump(host.started ? host.rootAge : 0);
  if (result != Result::Ok) {
    return result;
  }
  if (host.started && !host.rootFinished) {
    result = host.lifecycle->stop();
    if (result != Result::Ok) {
      return result;
    }
  }
  host.scheduledRoutes.clear();
  host.ownerNotices.clear();
  host.discardChildGameEvents({});
  if (host.cpuParticles)
  {
    host.cpuParticles->clear();
  }
  host.rootStopped = true;
  return Result::Ok;
}

auto SpawnableHost::notifyEndRequest(const SpawnableScope &scope,
                                     const std::uint8_t nodeId[16]) -> Result {
  auto &host = *impl_;
  if (!validScope(scope) || !nodeId || !isNonzero(nodeId)) {
    return Result::InvalidArgument;
  }
  if (host.operationActive) {
    return Result::Busy;
  }
  // The existing root signal API owns its own guard and routes actual work.
  if (scope.kind == SpawnableScopeKind::Effect) {
    return dispatchSignal(nodeId);
  }
  auto operation = OwnerOperation(host.operationActive);
  if (!host.started || host.rootStopped || host.rootFinished) {
    return Result::NotReady;
  }
  if (host.failure != Result::Ok) {
    return host.failure;
  }
  auto state = host.findTemplate(scope.containerId);
  if (!state || !host.findInstance(*state, scope.instanceId)) {
    return Result::InvalidHandle;
  }
  auto result = state->pool->dispatchSignal(
      scope.instanceId, nodeId, host.rootAge, host.options.instructionBudget);
  if (result == Result::Ok) {
    result = host.pump(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.dispatchScheduled(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.beginAutoEnd(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.notifyOwnerDestroy(host.rootAge);
  }
  if (result == Result::Ok) {
    result = host.finishIfQuiescent();
  }
  if (result == Result::QueueRejected) {
    host.pendingPump = true;
  } else if (result != Result::Ok && result != Result::InvalidHandle &&
             result != Result::InvalidArgument && result != Result::NotReady) {
    host.failure = result;
  }
  return result;
}

auto SpawnableHost::forceTerminate(const SpawnableScope &scope) -> Result {
  auto &host = *impl_;
  if (!validScope(scope)) {
    return Result::InvalidArgument;
  }
  if (host.operationActive) {
    return Result::Busy;
  }
  auto operation = OwnerOperation(host.operationActive);
  try {
    return host.forceTerminate(scope);
  } catch (const std::bad_alloc &) {
    return Result::InsufficientWork;
  } catch (...) {
    return Result::NotReady;
  }
}

auto SpawnableHost::debugGeneration() const noexcept -> std::uint64_t {
  return impl_->debugGeneration;
}

auto SpawnableHost::debugTarget(const SpawnableScope &scope,
                                SpawnableDebugTarget &out) noexcept -> Result {
  out = {};
  if (!validScope(scope)) {
    return Result::InvalidArgument;
  }
  if (scope.kind == SpawnableScopeKind::Effect) {
    if (!impl_->rootInstance || impl_->rootFinished || impl_->rootForced) {
      return Result::NotReady;
    }
  } else {
    auto state = impl_->findTemplate(scope.containerId);
    if (!state || !impl_->findInstance(*state, scope.instanceId)) {
      return Result::InvalidHandle;
    }
  }
  out = {impl_->debugGeneration, scope};
  return Result::Ok;
}

auto SpawnableHost::setDebugHooks(const SpawnableDebugTarget &target,
                                  const ExecutionDebugHooks &hooks) noexcept
    -> Result {
  if (!target.hostGeneration ||
      target.hostGeneration != impl_->debugGeneration) {
    return Result::InvalidHandle;
  }
  if (!validScope(target.scope)) {
    return Result::InvalidArgument;
  }
  if (target.scope.kind == SpawnableScopeKind::Effect) {
    if (!impl_->rootInstance ||
        (hooks.beforeNode && (impl_->rootFinished || impl_->rootForced))) {
      return Result::NotReady;
    }
    const auto result = impl_->rootInstance->setDebugHooks(hooks);
    if (result == Result::Ok) { impl_->executionDebugAttached = hooks.beforeNode != nullptr; }
    return result;
  }
  auto state = impl_->findTemplate(target.scope.containerId);
  if (!state || !impl_->findInstance(*state, target.scope.instanceId)) {
    return Result::InvalidHandle;
  }
  const auto result = state->pool->setDebugHooks(target.scope.instanceId, hooks);
  if (result == Result::Ok)
  {
      impl_->findInstance(*state, target.scope.instanceId)->executionDebugAttached = hooks.beforeNode != nullptr;
  }
  return result;
}

auto SpawnableHost::rootValue(const std::uint8_t nodeId[16], double out[4],
                              std::uint8_t &width) const -> Result {
  if (!nodeId || !out) {
    return Result::InvalidArgument;
  }
  if (impl_->rootForced) {
    return Result::NotReady;
  }
  return impl_->readRoot(nodeId, out, width);
}

auto SpawnableHost::childValue(const std::uint8_t containerId[16],
                               GeneratorInstanceId instanceId,
                               const std::uint8_t nodeId[16], double out[4],
                               std::uint8_t &width) const -> Result {
  if (!containerId || !nodeId || !out) {
    return Result::InvalidArgument;
  }
  auto state = impl_->findTemplate(containerId);
  if (!state || !impl_->findInstance(*state, instanceId)) {
    return Result::InvalidHandle;
  }
  return impl_->readChild(*state, instanceId, nodeId, out, width);
}

auto SpawnableHost::popEvent(SpawnableHostEvent &out) noexcept -> bool {
  auto &host = *impl_;
  if (!host.eventCount) {
    if (host.pendingForceEventHead >= host.pendingForceEvents.size()) {
      return false;
    }
    out = host.pendingForceEvents[host.pendingForceEventHead++];
    if (host.pendingForceEventHead == host.pendingForceEvents.size()) {
      host.pendingForceEvents.clear();
      host.pendingForceEventHead = 0;
    }
    return true;
  }
  out = host.events[host.eventHead];
  host.eventHead = (host.eventHead + 1) % host.events.size();
  --host.eventCount;
  if (host.eventCount < host.events.size() &&
      host.pendingForceEventHead < host.pendingForceEvents.size()) {
    host.events[(host.eventHead + host.eventCount++) % host.events.size()] =
        host.pendingForceEvents[host.pendingForceEventHead++];
    if (host.pendingForceEventHead == host.pendingForceEvents.size()) {
      host.pendingForceEvents.clear();
      host.pendingForceEventHead = 0;
    }
  }
  return true;
}

auto SpawnableHost::popGameIoEvent(NamedIoEvent &out) noexcept -> bool {
  if (impl_->gameIo && impl_->gameIo->popEvent(out)) {
    return true;
  }
  if (!impl_->childGameCount) {
    return false;
  }
  out = impl_->childGameEvents[impl_->childGameHead];
  impl_->childGameHead =
      (impl_->childGameHead + 1) % impl_->childGameEvents.size();
  --impl_->childGameCount;
  return true;
}

auto SpawnableHost::activeCount() const noexcept -> std::size_t {
  return impl_->activeCount();
}

auto SpawnableHost::rootFinished() const noexcept -> bool {
  return impl_->rootFinished;
}

auto SpawnableHost::hasCpuParticleEvents() const noexcept -> bool {
  if (impl_->cpuParticles && impl_->cpuParticles->pendingEventCount() != 0) {
    return true;
  }
  for (const auto &state : impl_->templates) {
    for (const auto &instance : state->instances) {
      if (instance.cpuParticles &&
          instance.cpuParticles->pendingEventCount() != 0) {
        return true;
      }
    }
  }
  return !impl_->retiredCpuEvents.empty();
}

auto SpawnableHost::package() const noexcept -> const SpawnablePackage & {
  return impl_->packageData;
}

SpawnableHost::SpawnableHost(std::unique_ptr<Impl> impl) noexcept
    : impl_(std::move(impl)) {}
SpawnableHost::~SpawnableHost() = default;

auto SpawnableHost::create(SpawnablePackage package,
                           const SpawnableHostOptions &options,
                           std::unique_ptr<SpawnableHost> &out) -> Result {
  return create(std::move(package), options, nullptr, 0, out);
}

auto SpawnableHost::create(SpawnablePackage package,
                           const SpawnableHostOptions &options,
                           const CustomNodeDescriptor *registry,
                           std::size_t registryCount,
                           std::unique_ptr<SpawnableHost> &out) -> Result {
  out.reset();
  if (registryCount != 0 && registry == nullptr) {
    return Result::InvalidArgument;
  }
  try {
    auto impl = std::make_unique<Impl>(std::move(package), options);
    if (registryCount != 0) {
      if (registryCount > impl->projectDescriptors.max_size()) {
        return Result::InvalidArgument;
      }
      impl->projectDescriptors.assign(registry, registry + registryCount);
    }
    auto result = nextHostGeneration(impl->debugGeneration);
    if (result != Result::Ok) return result;
    result = impl->initialize();
    if (result != Result::Ok) {
      return result;
    }
    impl->configureExecutionSafety();
    out.reset(new SpawnableHost(std::move(impl)));
    return Result::Ok;
  } catch (const std::bad_alloc &) {
    return Result::InsufficientWork;
  }
}
} // namespace liberafx
