#include "LbSpawnableHostInternal.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <numeric>
#include <optional>
#include <utility>
#include <vector>

namespace liberafx::spawnable_host_detail {
struct CpuStep final {
  CpuParticleWorld *world = nullptr;
  double time = 0;
  double frameDelta = 0;
  std::size_t instructionBudget = 0;
  bool emitting = false;
  bool prepare = false;
  bool deliverSignals = false;
  bool advance = true;
  HostQualitySnapshot quality{};
};

auto runCpuStep(void *context, Instance &instance) noexcept -> Result {
  auto &step = *static_cast<CpuStep *>(context);
  if (!step.world) {
    return Result::Ok;
  }
  auto result = Result::Ok;
  if (step.prepare) {
    result = step.world->prepare(instance);
  }
  if (result == Result::Ok && step.deliverSignals) {
    result = step.world->deliverSignals(instance, step.time,
                                         step.instructionBudget);
  }
  if (result == Result::Ok && step.advance) {
    result = step.world->advance(instance, step.time, step.emitting,
                                 step.quality, step.frameDelta);
  }
  return result;
}
} // namespace liberafx::spawnable_host_detail

namespace liberafx {
using namespace spawnable_host_detail;
auto SpawnableHost::Impl::initialize() -> Result {
  if (!options.maxActivePerTemplate || options.maxActivePerTemplate > 4096 ||
      !options.memoryBudgetBytesPerTemplate || !options.eventCapacity ||
      options.eventCapacity > 8192 || !options.instructionBudget ||
      options.instructionBudget > 10000 ||
      packageData.templates.size() > 4096) {
    return Result::InvalidArgument;
  }
  auto result = Result::Ok;
  templates.reserve(packageData.templates.size());
  for (const auto &data : packageData.templates) {
    if (!isNonzero(data.containerId.data()) ||
        !isNonzero(data.signalTypeId.data()) || data.inputs.size() > 256 ||
        (data.parentContainerId &&
         !isNonzero(data.parentContainerId->data()))) {
      return Result::InvalidBinary;
    }
    if (findTemplate(data.containerId.data())) {
      return Result::InvalidBinary;
    }
    auto state = std::make_unique<TemplateState>();
    state->data = &data;
    templates.push_back(std::move(state));
  }
  for (const auto &state : templates) {
    auto cursor = state.get();
    auto depth = std::size_t{1};
    while (cursor->data->parentContainerId) {
      cursor = findTemplate(cursor->data->parentContainerId->data());
      if (!cursor || ++depth > 12) {
        return Result::InvalidBinary;
      }
      if (cursor == state.get()) {
        return Result::InvalidBinary;
      }
    }
    state->depth = static_cast<std::uint8_t>(depth);
  }
  for (const auto &route : packageData.destroyRoutes) {
    auto source = findTemplate(route.sourceContainerId.data());
    auto target = findTemplate(route.targetContainerId.data());
    if (!source || !target ||
        source->data->parentContainerId != target->data->parentContainerId) {
      return Result::InvalidBinary;
    }
  }
  for (const auto &route : packageData.signalRoutes) {
    if (!findTemplate(route.sourceContainerId.data())) {
      return Result::InvalidBinary;
    }
  }

  for (auto index = std::size_t{}; index < templates.size(); ++index) {
    auto &state = *templates[index];
    if (!state.data->cpuPrograms.empty()) {
      result = CpuParticleWorld::create(
          state.data->cpuPrograms.data(), state.data->cpuPrograms.size(),
          projectDescriptors.data(), projectDescriptors.size(),
          state.cpuDescriptorWorld, options.cpuParticleBatching);
      if (result != Result::Ok) {
        return result;
      }
    }
    auto childCount = std::size_t{};
    auto signalCount = std::size_t{};
    for (const auto &candidate : templates) {
      if (!candidate->data->parentContainerId ||
          !sameId(candidate->data->parentContainerId->data(),
                  state.data->containerId.data())) {
        continue;
      }
      ++childCount;
      if (candidate->data->signalInputs) {
        signalCount += candidate->data->signalInputs->size();
      }
    }
    state.spawnCallbacks.resize(childCount);
    state.signalCallbacks.resize(signalCount);
    state.signalTypes.resize(signalCount);
    auto cpuSignalCount = std::size_t{};
    const auto cpuSignals = state.cpuDescriptorWorld
                                ? state.cpuDescriptorWorld->signalDescriptors(
                                      cpuSignalCount)
                                : nullptr;
    state.cpuCallbacks.resize(cpuSignalCount);
    state.descriptors.resize(1 + childCount + signalCount + cpuSignalCount);
    passthroughDescriptor(state.descriptors[0]);
    auto descriptorIndex = std::size_t{1};
    auto spawnIndex = std::size_t{};
    auto signalIndex = std::size_t{};
    for (const auto &candidate : templates) {
      if (!candidate->data->parentContainerId ||
          !sameId(candidate->data->parentContainerId->data(),
                  state.data->containerId.data())) {
        continue;
      }
      auto &callback = state.spawnCallbacks[spawnIndex++];
      callback.ownerTag = &currentOwnerTag;
      const auto extended =
          candidate->data->inputs.size() > 4 ||
          std::accumulate(candidate->data->inputs.begin(),
                          candidate->data->inputs.end(), std::size_t{},
                          [](std::size_t total, const auto &input) {
                            return total + input.width;
                          }) > 16;
      if (!extended) {
        if (candidate->data->inputs.size() > 4) {
          return Result::InvalidBinary;
        }
        callback.inputCount =
            static_cast<std::uint8_t>(candidate->data->inputs.size());
        for (auto input = std::size_t{}; input < candidate->data->inputs.size();
             ++input) {
          const auto &source = candidate->data->inputs[input];
          if (source.width < 1 || source.width > 4 ||
              (source.isColor && source.width != 4)) {
            return Result::InvalidBinary;
          }
          callback.nodeIds[input] = source.templateNodeId;
          callback.widths[input] = static_cast<std::uint8_t>(source.width);
        }
      }
      auto &spawnDescriptor = state.descriptors[descriptorIndex++];
      std::memcpy(spawnDescriptor.typeId,
                  candidate->data->signalTypeId.data(), 16);
      spawnDescriptor.abiVersion = 1;
      spawnDescriptor.execution = CustomExecution::Signal;
      spawnDescriptor.inputCount = callback.inputCount;
      spawnDescriptor.branchCount = 1;
      spawnDescriptor.function = enqueueSpawn;
      spawnDescriptor.context = &callback;
      for (auto input = 0u; input < spawnDescriptor.inputCount; ++input) {
        spawnDescriptor.inputWidths[input] = callback.widths[input];
        spawnDescriptor.inputIsColor[input] =
            candidate->data->inputs[input].isColor ? 1 : 0;
      }
      auto parentCount = std::size_t{};
      result = bytecodeNodeCount(state.data->bytecode, parentCount);
      if (result != Result::Ok) {
        return result;
      }
      auto recordMatches = std::size_t{};
      for (auto node = std::size_t{}; node < parentCount; ++node) {
        const auto *record = state.data->bytecode.data() +
                             bytecodeHeaderBytes + node * customInstructionBytes;
        if (record[16] != 41 ||
            !sameId(record, candidate->data->containerId.data()) ||
            !sameId(record + 24, candidate->data->signalTypeId.data())) {
          continue;
        }
        if (record[18] != callback.inputCount) {
          return Result::InvalidBinary;
        }
        ++recordMatches;
      }
      if (recordMatches != 1) {
        return Result::InvalidBinary;
      }

      if (!candidate->data->signalInputs) {
        continue;
      }
      for (const auto &input : *candidate->data->signalInputs) {
        auto &signalCallback = state.signalCallbacks[signalIndex++];
        result = findSignalType(state.data->bytecode,
                                input.proxyNodeId.data(),
                                state.signalTypes[signalIndex - 1]);
        if (result != Result::Ok) {
          return result;
        }
        signalCallback.nodeId = input.templateNodeId;
        signalCallback.ownerTag = &currentOwnerTag;
        auto &signalDescriptor = state.descriptors[descriptorIndex++];
        std::memcpy(signalDescriptor.typeId,
                    state.signalTypes[signalIndex - 1].data(), 16);
        signalDescriptor.abiVersion = 1;
        signalDescriptor.execution = CustomExecution::Signal;
        signalDescriptor.branchCount = 1;
        signalDescriptor.function = dispatchTemplateSignal;
        signalDescriptor.context = &signalCallback;
        for (auto prior = std::size_t{1}; prior < descriptorIndex - 1; ++prior) {
          if (sameId(state.descriptors[prior].typeId,
                     signalDescriptor.typeId)) {
            return Result::InvalidBinary;
          }
        }
      }
    }
    for (auto cpuIndex = std::size_t{}; cpuIndex < cpuSignalCount; ++cpuIndex) {
      auto &callback = state.cpuCallbacks[cpuIndex];
      callback.activeWorld = &activeCpuParticles;
      std::memcpy(callback.nodeId.data(), cpuSignals[cpuIndex].typeId, 16);
      auto descriptor = cpuSignals[cpuIndex];
      descriptor.function = dispatchCpuParticleSignal;
      descriptor.context = &callback;
      state.descriptors[descriptorIndex++] = descriptor;
    }
    state.descriptors.insert(state.descriptors.end(), projectDescriptors.begin(),
                             projectDescriptors.end());
    result = loadProgram(state.data->bytecode, state.descriptors.data(),
                         state.descriptors.size(), state.bindings, state.program);
    if (result != Result::Ok) {
      return result;
    }
    const auto &data = *state.data;
    for (auto portIndex = std::size_t{};
         portIndex < state.program.gameIoPortCount(); ++portIndex) {
      auto port = ProgramGameIoPort{};
      if (!state.program.gameIoPort(portIndex, port) || port.kind > 3) {
        return Result::InvalidBinary;
      }
      if (port.kind == static_cast<std::uint8_t>(NamedIoKind::OutputSignal)) {
        auto id = std::array<std::uint8_t, 16>{};
        std::memcpy(id.data(), port.nodeId, 16);
        state.outputIds.push_back(id);
      }
    }

    auto maxCaptureLanes = std::size_t{16};
    const auto extended =
        data.inputs.size() > 4 ||
        std::accumulate(data.inputs.begin(), data.inputs.end(), std::size_t{},
                        [](std::size_t total, const auto &input) {
                          return total + input.width;
                        }) > 16;
    if (extended || (data.capturedInputs && !data.capturedInputs->empty()) ||
        (data.liveInputs && !data.liveInputs->empty())) {
      maxCaptureLanes = 256;
    }
    result = GeneratorPool::createWithCaptureLimit(
        state.program, data.containerId.data(), options.maxActivePerTemplate,
        options.memoryBudgetBytesPerTemplate, options.eventCapacity,
        maxCaptureLanes, state.pool);
    if (result != Result::Ok) {
      return result;
    }

    auto lifecycleConfig = GeneratorLifecycle{};
    lifecycleConfig.instructionBudget = options.instructionBudget;
    lifecycleConfig.enableDeferredSignals = packageData.hasChildDeferredSignals;
    if (data.lifecycle) {
      if (data.lifecycle->onStartNodeId) {
        std::memcpy(lifecycleConfig.onStartNodeId,
                    data.lifecycle->onStartNodeId->data(), 16);
        lifecycleConfig.hasOnStart = true;
      }
      if (data.lifecycle->lifeNodeId) {
        std::memcpy(lifecycleConfig.lifeNodeId,
                    data.lifecycle->lifeNodeId->data(), 16);
        lifecycleConfig.hasLife = true;
      }
    }
    result = state.pool->configureLifecycle(lifecycleConfig);
    if (result != Result::Ok) {
      return result;
    }
    if (data.signalOutputs && !data.signalOutputs->empty()) {
      state.outputIds.reserve(state.outputIds.size() +
                              data.signalOutputs->size());
      for (const auto &port : *data.signalOutputs) {
        state.outputIds.push_back(port.templateNodeId);
      }
    }
    if (!state.outputIds.empty()) {
      result = state.pool->setSignalOutputs(
          reinterpret_cast<const std::uint8_t *>(state.outputIds.data()),
          state.outputIds.size());
      if (result != Result::Ok) {
        return result;
      }
    }
  }

  for (auto index = std::size_t{}; index < templates.size(); ++index) {
    auto &state = *templates[index];
    auto childIndex = std::size_t{};
    auto signalIndex = std::size_t{};
    for (const auto &candidate : templates) {
      if (!candidate->data->parentContainerId ||
          !sameId(candidate->data->parentContainerId->data(),
                  state.data->containerId.data())) {
        continue;
      }
      state.spawnCallbacks[childIndex++].pool = candidate->pool.get();
      if (candidate->data->signalInputs) {
        for (const auto &input : *candidate->data->signalInputs) {
          (void)input;
          state.signalCallbacks[signalIndex++].pool = candidate->pool.get();
        }
      }
    }
  }

  auto signalInputCount = std::size_t{};
  for (const auto &state : templates) {
    if (!state->data->parentContainerId && state->data->signalInputs) {
      signalInputCount += state->data->signalInputs->size();
    }
  }
  auto rootSpawnCount = std::count_if(
      templates.begin(), templates.end(), [](const auto &state) {
        return !state->data->parentContainerId.has_value();
      });
  spawnContexts.resize(templates.size());
  signalInputs.resize(signalInputCount);
  parentDescriptors.resize(1 + rootSpawnCount + signalInputs.size());
  passthroughDescriptor(parentDescriptors[0]);
  auto descriptorIndex = std::size_t{1};
  for (auto index = std::size_t{}; index < templates.size(); ++index) {
    auto &state = *templates[index];
    if (state.data->parentContainerId) {
      continue;
    }
    auto &context = spawnContexts[index];
    context.callback.pool = state.pool.get();
    context.callback.ownerTag = &currentOwnerTag;
    const auto extended =
        state.data->inputs.size() > 4 ||
        std::accumulate(state.data->inputs.begin(), state.data->inputs.end(),
                        std::size_t{},
                        [](std::size_t total, const auto &input) {
                          return total + input.width;
                        }) > 16;
    if (!extended) {
      if (state.data->inputs.size() > 4) {
        return Result::InvalidBinary;
      }
      context.callback.inputCount =
          static_cast<std::uint8_t>(state.data->inputs.size());
      for (auto input = std::size_t{}; input < state.data->inputs.size();
           ++input) {
        const auto &source = state.data->inputs[input];
        if (source.width < 1 || source.width > 4 ||
            (source.isColor && source.width != 4)) {
          return Result::InvalidBinary;
        }
        context.callback.nodeIds[input] = source.templateNodeId;
        context.callback.widths[input] =
            static_cast<std::uint8_t>(source.width);
      }
    }
    auto &descriptor = parentDescriptors[descriptorIndex++];
    std::memcpy(descriptor.typeId, state.data->signalTypeId.data(), 16);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Signal;
    descriptor.inputCount = context.callback.inputCount;
    descriptor.branchCount = 1;
    descriptor.function = enqueueSpawn;
    descriptor.context = &context.callback;
    for (auto input = 0u; input < descriptor.inputCount; ++input) {
      descriptor.inputWidths[input] = context.callback.widths[input];
      descriptor.inputIsColor[input] =
          state.data->inputs[input].isColor ? 1 : 0;
    }
    auto recordMatches = std::size_t{};
    auto parentCount = std::size_t{};
    result = bytecodeNodeCount(packageData.parentBytecode, parentCount);
    if (result != Result::Ok) {
      return result;
    }
    for (auto node = std::size_t{}; node < parentCount; ++node) {
      const auto *record = packageData.parentBytecode.data() +
                           bytecodeHeaderBytes + node * customInstructionBytes;
      if (record[16] != 41 || !sameId(record, state.data->containerId.data()) ||
          !sameId(record + 24, state.data->signalTypeId.data())) {
        continue;
      }
      if (record[18] != descriptor.inputCount) {
        return Result::InvalidBinary;
      }
      ++recordMatches;
    }
    if (recordMatches != 1) {
      return Result::InvalidBinary;
    }
  }

  auto signalContextIndex = std::size_t{};
  for (auto &statePointer : templates) {
    auto &state = *statePointer;
    if (state.data->parentContainerId || !state.data->signalInputs) {
      continue;
    }
    for (const auto &input : *state.data->signalInputs) {
      auto &context = signalInputs[signalContextIndex++];
      result = findSignalType(packageData.parentBytecode,
                              input.proxyNodeId.data(), context.typeId);
      if (result != Result::Ok) {
        return result;
      }
      context.callback.pool = state.pool.get();
      context.callback.ownerTag = &currentOwnerTag;
      context.callback.nodeId = input.templateNodeId;
      auto &descriptor = parentDescriptors[descriptorIndex++];
      std::memcpy(descriptor.typeId, context.typeId.data(), 16);
      descriptor.abiVersion = 1;
      descriptor.execution = CustomExecution::Signal;
      descriptor.branchCount = 1;
      descriptor.function = dispatchTemplateSignal;
      descriptor.context = &context.callback;
      for (auto previous = std::size_t{1}; previous < descriptorIndex - 1;
           ++previous) {
        if (sameId(parentDescriptors[previous].typeId, descriptor.typeId)) {
          return Result::InvalidBinary;
        }
      }
    }
  }

  for (const auto &project : projectDescriptors) {
    for (auto index = std::size_t{}; index < descriptorIndex; ++index) {
      if (sameId(project.typeId, parentDescriptors[index].typeId)) {
        return Result::InvalidArgument;
      }
    }
  }
  parentDescriptors.insert(parentDescriptors.end(), projectDescriptors.begin(),
                           projectDescriptors.end());
  result = CpuParticleWorld::create(packageData.cpuPrograms.data(), packageData.cpuPrograms.size(),
      projectDescriptors.data(), projectDescriptors.size(), cpuParticles, options.cpuParticleBatching);
  if (result != Result::Ok)
  {
    return result;
  }
  auto cpuSignalCount = std::size_t{};
  auto cpuSignals = cpuParticles->signalDescriptors(cpuSignalCount);
  if (cpuSignalCount != 0)
  {
    parentDescriptors.insert(parentDescriptors.end(), cpuSignals, cpuSignals + cpuSignalCount);
  }
  result = loadProgram(packageData.parentBytecode, parentDescriptors.data(),
                       parentDescriptors.size(), parentBindings, parentProgram);
  if (result != Result::Ok) {
    return result;
  }
  if (parentProgram.gameIoPortCount() != packageData.gameIoPorts.size()) {
    return Result::InvalidBinary;
  }
  gameIoPorts.reserve(packageData.gameIoPorts.size());
  for (auto index = std::size_t{}; index < packageData.gameIoPorts.size();
       ++index) {
    auto compiled = ProgramGameIoPort{};
    if (!parentProgram.gameIoPort(index, compiled) || !compiled.name) {
      return Result::InvalidBinary;
    }
    const auto &saved = packageData.gameIoPorts[index];
    if (saved.name != compiled.name || saved.kind != compiled.kind ||
        saved.width != compiled.width ||
        saved.isColor != (compiled.isColor != 0)) {
      return Result::InvalidBinary;
    }
    auto port = NamedIoPort{};
    port.name = compiled.name;
    port.kind = static_cast<NamedIoKind>(compiled.kind);
    std::memcpy(port.nodeId, compiled.nodeId, 16);
    std::memcpy(port.additionalNodeIds, compiled.additionalNodeIds,
                sizeof(port.additionalNodeIds));
    port.width = compiled.width;
    port.isColor = compiled.isColor != 0;
    port.nodeCount = compiled.nodeCount;
    // Matrix ports keep a logical authoring ID distinct from their runtime
    // rows.
    if (saved.runtimeNodeIds) {
      if (saved.runtimeNodeIds->size() != compiled.nodeCount) {
        return Result::InvalidBinary;
      }
      for (auto node = std::size_t{}; node < saved.runtimeNodeIds->size();
           ++node) {
        const auto *compiledNode =
            node == 0 ? compiled.nodeId : compiled.additionalNodeIds[node - 1];
        if (!sameId((*saved.runtimeNodeIds)[node].data(), compiledNode)) {
          return Result::InvalidBinary;
        }
      }
    } else if (compiled.nodeCount != 1 ||
               !sameId(saved.nodeId.data(), compiled.nodeId)) {
      return Result::InvalidBinary;
    }
    gameIoPorts.push_back(port);
  }

  auto workBytes = parentProgram.workBytes();
  auto defaultsCount = parentProgram.parameterValueCount();
  if (defaultsCount >
      std::numeric_limits<std::size_t>::max() / sizeof(double)) {
    return Result::InsufficientWork;
  }
  result = rootWork.allocate(workBytes, parentProgram.workAlignment());
  if (result != Result::Ok) {
    return result;
  }
  result = rootDefaults.allocate(defaultsCount * sizeof(double),
                                 parentProgram.defaultsAlignment());
  if (result != Result::Ok) {
    return result;
  }
  rootInstance.emplace();
  result = rootInstance->initialize(
      parentProgram, static_cast<double *>(rootWork.data),
      (workBytes + sizeof(double) - 1) / sizeof(double),
      static_cast<double *>(rootDefaults.data), defaultsCount);
  if (result != Result::Ok) {
    return result;
  }
  result = rootInstance->enableForceRequests();
  if (result != Result::Ok) {
    return result;
  }
  if (!gameIoPorts.empty()) {
    result = NamedIo::create(*rootInstance, gameIoPorts.data(),
                             gameIoPorts.size(), options.eventCapacity, gameIo);
    if (result != Result::Ok) {
      return result;
    }
    lifecycle.emplace(*rootInstance, *gameIo);
  } else {
    lifecycle.emplace(*rootInstance);
  }
  return Result::Ok;
}

auto SpawnableHost::Impl::applyCaptures(
    TemplateState &state, GeneratorInstanceId id,
    const std::optional<std::vector<SpawnablePackageCapture>> &inputs,
    bool live, std::uint64_t ownerTag) -> Result {
  if (!inputs || inputs->empty()) {
    return Result::Ok;
  }
  auto captures = std::vector<GeneratorCapture>{};
  for (const auto &binding : *inputs) {
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
    result = appendCapture(captures, binding, values.data(), width);
    if (result != Result::Ok) {
      return result;
    }
  }
  return live ? state.pool->setLive(id, captures.data(), captures.size())
              : state.pool->setCaptured(id, captures.data(), captures.size());
}

auto SpawnableHost::Impl::startRoot(double initialFrameDelta) -> Result {
  if (started) {
    return Result::Ok;
  }
  auto result = rootInstance->setHostQuality(quality);
  if (result != Result::Ok) {
    return result;
  }
  result = rootInstance->setTimeContext(0, initialFrameDelta, realTimeAt(0), realTimeAt(0), realFrameDelta);
  if (result != Result::Ok) {
    return result;
  }
  auto sample = LifetimeSample{};
  const auto *startNode =
      packageData.onStartNodeId ? packageData.onStartNodeId->data() : nullptr;
  result = lifecycle->start(Lifetime::infinite(), startNode,
                            options.instructionBudget, sample);
  if (result != Result::Ok) {
    return result;
  }
  started = true;
  rootAge = 0;
  time = 0;
  rootEnding = sample.expired;
  result = cpuParticles->advance(*rootInstance, 0, !rootEnding, quality, initialFrameDelta);
  if (result != Result::Ok)
  {
    failure = result;
    return result;
  }
  result = pump(0);
  if (result == Result::Ok) {
    result = dispatchScheduled(0);
  }
  if (result == Result::Ok) {
    result = beginAutoEnd(0);
  }
  if (result == Result::Ok) {
    result = notifyOwnerDestroy(0);
  }
  if (result == Result::Ok) {
    result = finishIfQuiescent();
  }
  return result;
}

auto SpawnableHost::Impl::evaluateParent(double at) -> Result {
  if (!std::isfinite(at) || at < rootAge) {
    return Result::InvalidArgument;
  }
  if (at == rootAge) {
    return Result::Ok;
  }
  auto result = rootInstance->setHostQuality(quality);
  if (result != Result::Ok) {
    return result;
  }
  result = rootInstance->setTimeContext(at, frameDelta, realTimeAt(at), realTimeAt(at), realFrameDelta);
  if (result != Result::Ok) {
    return result;
  }
  if (rootEnding) {
    result = lifecycle->drain(at);
    if (result == Result::Ok) {
      rootAge = at;
    }
    return result;
  }
  auto sample = LifetimeSample{};
  result = lifecycle->advance(at, sample);
  if (result == Result::Ok) {
    rootAge = sample.age;
    rootEnding = sample.expired;
  }
  return result;
}

auto SpawnableHost::Impl::evaluateFrame(double at) -> Result {
  auto cpuResult = cpuParticles->prepare(*rootInstance);
  if (cpuResult != Result::Ok)
  {
    failure = cpuResult;
    return cpuResult;
  }
  auto result = evaluateParent(at);
  if (result != Result::Ok) {
    return result;
  }
  result = applyAuthoredForce();
  if (result != Result::Ok || rootForced) {
    return result;
  }
  result = cpuParticles->deliverSignals(*rootInstance, at, options.instructionBudget);
  if (result == Result::Ok)
  {
    result = cpuParticles->advance(*rootInstance, at, !rootEnding, quality, frameDelta);
  }
  if (result != Result::Ok)
  {
    failure = result;
    return result;
  }
  auto evaluated = std::vector<OwnerNotice>{};
  for (auto round = 0u; round < 128; ++round) {
    auto foundPending = false;
    for (auto depth = std::uint8_t{1}; depth <= 12; ++depth) {
      for (auto &statePointer : templates) {
      auto &state = *statePointer;
      if (state.depth != depth) {
        continue;
      }
      auto instanceIds = std::vector<GeneratorInstanceId>{};
      instanceIds.reserve(state.instances.size());
      for (const auto &instance : state.instances) {
        instanceIds.push_back(instance.id);
      }
      for (auto id : instanceIds) {
        const auto alreadyEvaluated = std::any_of(
            evaluated.begin(), evaluated.end(), [&](const auto &item) {
              return item.instanceId == id &&
                     sameId(item.containerId.data(),
                            state.data->containerId.data());
            });
        if (alreadyEvaluated) {
          continue;
        }
        auto instance = findInstance(state, id);
        if (!instance) {
          continue;
        }
        foundPending = true;
        evaluated.push_back({state.data->containerId, id});
        auto ownerContext = OwnerTagOperation(currentOwnerTag,
                                              instance->scopeTag);
        auto cpuContext =
            CpuWorldOperation(activeCpuParticles, instance->cpuParticles.get());
        if (instance->retiring) {
          if (instance->cpuParticles) {
            auto step = CpuStep{instance->cpuParticles.get(), at, frameDelta,
                                options.instructionBudget, false, true, true,
                                true, quality};
            result = state.pool->withInstance(id, runCpuStep, &step);
            if (result != Result::Ok) {
              return result;
            }
          }
          continue;
        }
        result = state.pool->setHostQuality(id, quality);
        if (result != Result::Ok) {
          return result;
        }
        result = state.pool->setTimeContext(id, at, frameDelta, realTimeAt(at),
            std::max(0.0, realTimeAt(at) - instance->realStart), realFrameDelta);
        if (result == Result::Ok) {
          result = applyCaptures(state, id, state.data->liveInputs, true,
                                 instance->ownerTag);
        }
        if (result == Result::Ok && instance->cpuParticles) {
          auto step = CpuStep{instance->cpuParticles.get(), at, frameDelta,
                              options.instructionBudget,
                              at < instance->start + instance->life,
                              true, false, false, quality};
          result = state.pool->withInstance(id, runCpuStep, &step);
        }
        if (result == Result::Ok) {
          result = state.pool->evaluate(id, at);
        }
        if (result == Result::Ok && instance->cpuParticles) {
          auto step = CpuStep{instance->cpuParticles.get(), at, frameDelta,
                              options.instructionBudget,
                              at < instance->start + instance->life,
                              false, true, true, quality};
          result = state.pool->withInstance(id, runCpuStep, &step);
        }
        if (result == Result::Ok) {
          result = pump(at);
        }
        if (result != Result::Ok) {
          return result;
        }
        if (rootForced) {
          return Result::Ok;
        }
      }
    }
    }
    if (!foundPending) {
      return Result::Ok;
    }
  }
  return Result::InstructionBudgetExceeded;
}

auto SpawnableHost::Impl::spawnRoute(const CapturedRoute &captured, double at)
    -> Result {
  if (!captured.route) {
    return Result::InvalidBinary;
  }
  auto target = findTemplate(captured.route->targetContainerId.data());
  if (!target) {
    return Result::InvalidBinary;
  }
  auto captures = captured.captures;
  for (const auto &binding : captured.route->captures) {
    if (binding.sourceContainerId) {
      continue;
    }
    auto result = appendParentCapture(captures, binding, captured.ownerTag);
    if (result != Result::Ok) {
      return result;
    }
  }
  auto id = GeneratorInstanceId{};
  return target->pool->spawnTagged(at, captures.data(), captures.size(),
                                   captured.ownerTag, id);
}

auto SpawnableHost::Impl::dispatchOwnerSignal(std::uint64_t ownerTag,
                                               const std::uint8_t nodeId[16],
                                               double at) -> Result {
  if (!ownerTag) {
    auto result = evaluateParent(at);
    if (result == Result::Ok) {
      result = rootInstance->setTimeContext(at, frameDelta, realTimeAt(at), realTimeAt(at), realFrameDelta);
    }
    if (result == Result::Ok) {
      result = lifecycle->validateSignalTime(at);
    }
    if (result == Result::Ok) {
      result = rootInstance->dispatchSignal(nodeId, at,
                                            options.instructionBudget);
    }
    return result;
  }
  auto parentState = static_cast<TemplateState *>(nullptr);
  auto parent = static_cast<InstanceInfo *>(nullptr);
  if (!findScope(ownerTag, parentState, parent) || !parentState || !parent) {
    return Result::InvalidHandle;
  }
  return parentState->pool->dispatchSignal(
      parent->id, nodeId, at, options.instructionBudget);
}

auto SpawnableHost::Impl::processPoolEvent(TemplateState &state,
                                           const GeneratorEvent &event,
                                           double at) -> Result {
  if (event.kind == GeneratorEventKind::Spawn) {
    auto parentTag = std::uint64_t{};
    auto result = state.pool->ownerTag(event.instanceId, parentTag);
    if (result != Result::Ok) {
      return result;
    }
    if (!nextScopeTag || nextScopeTag == std::numeric_limits<std::uint64_t>::max()) {
      return Result::CounterOverflow;
    }
    const auto scopeTag = nextScopeTag++;
    state.instances.push_back(
        {event.instanceId, event.startTime,
         std::numeric_limits<double>::infinity(), parentTag, scopeTag});
    auto instance = findInstance(state, event.instanceId);
    if (!instance) {
      return Result::InvalidHandle;
    }
    instance->realStart = realTimeAt(event.startTime);
    if (!state.data->cpuPrograms.empty()) {
        if (retainForPool)
        {
            if (state.idleCpuWorlds.empty())
            {
                return Result::InsufficientWork;
            }
            instance->cpuParticles = std::move(state.idleCpuWorlds.back());
            state.idleCpuWorlds.pop_back();
            result = Result::Ok;
        }
        else
        {
            result = CpuParticleWorld::create(state.data->cpuPrograms.data(), state.data->cpuPrograms.size(),
                                              projectDescriptors.data(), projectDescriptors.size(),
                                              instance->cpuParticles, options.cpuParticleBatching);
        }
      if (result != Result::Ok) {
        state.instances.pop_back();
        return result;
      }
    }
    result = state.pool->setHostQuality(event.instanceId, quality);
    if (result != Result::Ok) {
      return result;
    }
    result = state.pool->setTimeContext(event.instanceId, event.startTime,
        frameDelta, realTimeAt(event.startTime), 0, realFrameDelta);
    if (result == Result::Ok) {
      result = applyCaptures(state, event.instanceId,
                             state.data->capturedInputs, false, parentTag);
    }
    if (result == Result::Ok) {
      result =
          applyCaptures(state, event.instanceId, state.data->liveInputs, true,
                        parentTag);
    }
    if (result == Result::Ok) {
      auto ownerContext = OwnerTagOperation(currentOwnerTag, scopeTag);
      auto cpuContext =
          CpuWorldOperation(activeCpuParticles, instance->cpuParticles.get());
      result = state.pool->start(event.instanceId);
    }
    if (result != Result::Ok) {
        const auto recycled = recycleCpuWorld(state, *instance);
        if (recycled != Result::Ok)
        {
            return recycled;
        }
      state.instances.erase(
          std::remove_if(state.instances.begin(), state.instances.end(),
                         [&](const auto &item) { return item.id == event.instanceId; }),
          state.instances.end());
      return result;
    }
    auto life = std::numeric_limits<double>::infinity();
    if (state.data->lifecycle && state.data->lifecycle->lifeNodeId) {
      auto values = std::array<double, 4>{};
      auto width = std::uint8_t{};
      result = readChild(state, event.instanceId,
                         state.data->lifecycle->lifeNodeId->data(),
                         values.data(), width);
      if (result != Result::Ok) {
        return result;
      }
      if (width != 1 || !std::isfinite(values[0]) || values[0] < 0 ||
          !std::isfinite(event.startTime + values[0])) {
        return Result::InvalidArgument;
      }
      life = values[0];
    }
    instance->life = life;
    if (result == Result::Ok && instance->cpuParticles) {
      auto ownerContext = OwnerTagOperation(currentOwnerTag, scopeTag);
      auto cpuContext =
          CpuWorldOperation(activeCpuParticles, instance->cpuParticles.get());
      auto step = CpuStep{instance->cpuParticles.get(), event.startTime,
                          frameDelta,
                          options.instructionBudget, life > 0, true, true,
                          true, quality};
      result = state.pool->withInstance(event.instanceId, runCpuStep, &step);
      if (result != Result::Ok) {
        return result;
      }
    }
    return appendEvent(SpawnableHostEventKind::Spawn, event.containerId,
                       event.instanceId, event.startTime, event.seed);
  }
  if (event.kind == GeneratorEventKind::Destroy) {
    auto instance = findInstance(state, event.instanceId);
    if (!instance) {
      return Result::InvalidHandle;
    }
    // The pool's Destroy is an internal deadline notification. Keep the
    // public render scope alive until its descendants and CPU world drain.
    const auto ownsTemplates = std::any_of(
        templates.begin(), templates.end(), [&](const auto &candidate) {
          const auto &parent = candidate->data->parentContainerId;
          return parent && *parent == state.data->containerId;
        });
    if (ownsTemplates || instance->cpuParticles ||
        state.pool->pendingSignalEventCount() != 0) {
      instance->retiring = true;
      instance->retirementSeed = event.seed;
      return evaluateParent(at);
    }

    // Legacy leaves can still publish Destroy and queue Stop immediately,
    // before the next pool starts any routed sibling.
    auto expiry = instance->start + instance->life;
    if (packageData.hasChildDeferredSignals) {
      auto elapsed = double{};
      auto result = state.pool->elapsed(event.instanceId, elapsed);
      if (result != Result::Ok) {
        return result;
      }
      expiry = instance->start + elapsed;
    }
    auto result = publishNaturalDestroy(state, *instance, expiry, event.seed);
    if (result == Result::Ok) {
      result = state.pool->finalize(event.instanceId);
    }
    if (result != Result::Ok) {
      return result;
    }
    return evaluateParent(at);
  }
  if (event.kind == GeneratorEventKind::Stop) {
    const auto instanceId = event.instanceId;
    auto *retiring = findInstance(state, instanceId);
    if (retiring)
    {
        const auto recycled = recycleCpuWorld(state, *retiring);
        if (recycled != Result::Ok)
        {
            return recycled;
        }
    }
    state.instances.erase(
        std::remove_if(state.instances.begin(), state.instances.end(),
                       [&](const auto &item) { return item.id == instanceId; }),
        state.instances.end());
    ownerNotices.erase(std::remove_if(ownerNotices.begin(), ownerNotices.end(),
                                      [&](const auto &item) {
                                        return item.instanceId == instanceId &&
                                               sameId(item.containerId.data(),
                                                      event.containerId);
                                      }),
                       ownerNotices.end());
    return appendEvent(SpawnableHostEventKind::Stop, event.containerId,
                       instanceId, at, event.seed);
  }
  return Result::InvalidBinary;
}

auto SpawnableHost::Impl::publishNaturalDestroy(TemplateState &state,
                                                const InstanceInfo &instance,
                                                double at, std::uint64_t seed)
    -> Result {
    const auto *containerId = state.data->containerId.data();
    const auto ownerTag = instance.ownerTag;
    auto result = appendEvent(SpawnableHostEventKind::Destroy, containerId,
                              instance.id, at, seed);
    if (result != Result::Ok) {
      return result;
    }
    result = evaluateParent(at);
    if (result != Result::Ok) {
      return result;
    }
    for (const auto &route : packageData.destroyRoutes) {
      if (!sameId(route.sourceContainerId.data(), containerId)) {
        continue;
      }
      auto frozen = CapturedRoute{};
      frozen.route = &route;
      frozen.ownerTag = ownerTag;
      for (const auto &binding : route.captures) {
        if (!binding.sourceContainerId) {
          continue;
        }
        if (!sameId(binding.sourceContainerId->data(), containerId)) {
          return Result::InvalidBinary;
        }
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        result = readChild(state, instance.id, binding.sourceNodeId.data(),
                           values.data(), width);
        if (result != Result::Ok) {
          return result;
        }
        result = appendCapture(frozen.captures, binding, values.data(), width);
        if (result != Result::Ok) {
          return result;
        }
      }
      if (route.delayNodeId) {
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        result = readOwner(ownerTag, nullptr, route.delayNodeId->data(),
                          values.data(), width);
        if (result != Result::Ok) {
          return result;
        }
        if (width != 1 || !std::isfinite(values[0]) || values[0] < 0 ||
            !std::isfinite(at + values[0])) {
          return Result::InvalidArgument;
        }
        if (!nextToken) {
          return Result::InsufficientWork;
        }
        const auto token = nextToken++;
        const auto due = at + values[0];
        result = lifecycle->schedule(due, token);
        if (result != Result::Ok) {
          return result;
        }
        scheduledRoutes.push_back(
            {token, std::move(frozen), instance.id, ownerTag});
      } else {
        result = spawnRoute(frozen, at);
        if (result != Result::Ok) {
          return result;
        }
      }
    }
    return Result::Ok;
}

auto SpawnableHost::Impl::pump(double at) -> Result {
  const auto fail = [&](Result result) {
    if (result == Result::QueueRejected) {
      pendingPump = true;
    }
    return result;
  };
  for (auto round = 0u; round < 128; ++round) {
    auto controlResult = applyAuthoredForce();
    if (controlResult != Result::Ok || rootForced) {
      return fail(controlResult);
    }
    auto progress = false;
    for (auto &statePointer : templates) {
      auto &state = *statePointer;
      auto processed = std::size_t{};
      auto result = state.pool->processSpawns(128, processed);
      if (result != Result::Ok) {
        return fail(result);
      }
      progress = progress || processed != 0;
      if (eventCount == events.size() &&
          (state.pool->pendingEventCount() ||
           state.pool->pendingSignalEventCount())) {
        return fail(Result::QueueRejected);
      }
      while (eventCount < events.size()) {
        auto event = GeneratorEvent{};
        if (!state.pool->popEvent(event)) {
          break;
        }
        progress = true;
        result = processPoolEvent(state, event, at);
        if (result != Result::Ok) {
          return fail(result);
        }
        controlResult = applyAuthoredForce();
        if (controlResult != Result::Ok || rootForced) {
          return fail(controlResult);
        }
      }
      while (eventCount < events.size() &&
             childGameCount < childGameEvents.size()) {
        auto signal = GeneratorSignalEvent{};
        if (!state.pool->popSignalEvent(signal)) {
          break;
        }
        auto ownerTag = std::uint64_t{};
        result = state.pool->ownerTag(signal.instanceId, ownerTag);
        if (result != Result::Ok) {
          return fail(result);
        }
        progress = true;
        for (auto index = std::size_t{};
             index < state.program.gameIoPortCount(); ++index) {
          auto port = ProgramGameIoPort{};
          if (!state.program.gameIoPort(index, port) ||
              port.kind !=
                  static_cast<std::uint8_t>(NamedIoKind::OutputSignal) ||
              !sameId(port.nodeId, signal.nodeId)) {
            continue;
          }
          auto &event = childGameEvents[(childGameHead + childGameCount++) %
                                        childGameEvents.size()];
          event = {};
          std::memcpy(event.identity.containerId, signal.containerId, 16);
          event.identity.instanceId = signal.instanceId;
          event.portIndex = static_cast<std::uint32_t>(index);
          event.kind = NamedIoKind::OutputSignal;
          break;
        }
        result = appendEvent(SpawnableHostEventKind::Signal, signal.containerId,
                             signal.instanceId, signal.time, signal.seed,
                             signal.nodeId);
        if (result != Result::Ok) {
          return fail(result);
        }
        auto ownerContext = OwnerTagOperation(currentOwnerTag, ownerTag);
        for (const auto &route : packageData.signalRoutes) {
          if (!sameId(route.sourceContainerId.data(), signal.containerId) ||
              !sameId(route.sourceNodeId.data(), signal.nodeId)) {
            continue;
          }
          result = dispatchOwnerSignal(ownerTag, route.targetNodeId.data(),
                                       signal.time);
          if (result != Result::Ok) {
            return fail(result);
          }
        }
      }
      if ((eventCount == events.size() ||
           childGameCount == childGameEvents.size()) &&
          (state.pool->pendingEventCount() ||
           state.pool->pendingSignalEventCount())) {
        return fail(Result::QueueRejected);
      }
    }
    if (!progress) {
      pendingPump = false;
      return Result::Ok;
    }
  }
  return fail(Result::InstructionBudgetExceeded);
}
} // namespace liberafx
