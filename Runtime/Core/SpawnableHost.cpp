#include "SpawnableHostInternal.h"
#include "liberafx/BuiltinSignals.h"
#include "liberafx/EffectLifecycle.h"
#include "liberafx/host/SpawnableDebugJob.h"
#include <atomic>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <numeric>
#include <optional>
#include <utility>
#include <vector>

namespace liberafx::spawnable_host_detail {
auto bytecodeInstructionBytes(const std::vector<std::uint8_t> &bytes,
                              std::size_t &size) noexcept -> Result {
  if (bytes.size() < bytecodeHeaderBytes ||
      std::memcmp(bytes.data(), "LFXB", 4) != 0 || bytes[4] < 2 ||
      bytes[4] > 11 || bytes[5] != 0 || bytes[6] != 1 || bytes[7] != 0) {
    return Result::InvalidBinary;
  }
  if (bytes[4] == 2) {
    if (bytes[12] != typedInstructionBytes || bytes[13] != 0 ||
        bytes[14] != 0 || bytes[15] != 0) {
      return Result::InvalidBinary;
    }
    size = typedInstructionBytes;
  } else {
    if (bytes[12] != 0 || bytes[13] != 1 || bytes[14] != 0 || bytes[15] != 0) {
      return Result::InvalidBinary;
    }
    size = customInstructionBytes;
  }
  return Result::Ok;
}

auto bytecodeNodeCount(const std::vector<std::uint8_t> &bytes,
                       std::size_t &count) noexcept -> Result {
  auto instructionBytes = std::size_t{};
  auto result = bytecodeInstructionBytes(bytes, instructionBytes);
  if (result != Result::Ok) {
    return result;
  }
  auto value = std::uint32_t{};
  for (auto index = 0u; index < 4; ++index) {
    value |= std::uint32_t{bytes[8 + index]} << (index * 8);
  }
  if (value > 10000 ||
      bytes.size() < bytecodeHeaderBytes + instructionBytes * value) {
    return Result::InvalidBinary;
  }
  if (bytes[4] == 2 &&
      bytes.size() != bytecodeHeaderBytes + instructionBytes * value) {
    return Result::InvalidBinary;
  }
  if (bytes[4] == 3 &&
      bytes.size() != bytecodeHeaderBytes + instructionBytes * value) {
    return Result::InvalidBinary;
  }
  count = value;
  return Result::Ok;
}

auto loadProgram(const std::vector<std::uint8_t> &bytes,
                 const CustomNodeDescriptor *registry,
                 std::size_t registryCount,
                 std::vector<CustomNodeBinding> &bindings, Program &out)
    -> Result {
  auto count = std::size_t{};
  auto result = bytecodeNodeCount(bytes, count);
  if (result != Result::Ok) {
    return result;
  }
  bindings.resize(count);
  return Program::load(bytes.data(), bytes.size(), registry, registryCount,
                       bindings.data(), bindings.size(), out);
}

auto findSignalType(const std::vector<std::uint8_t> &bytes,
                    const std::uint8_t nodeId[16],
                    std::array<std::uint8_t, 16> &typeId) noexcept -> Result {
  auto count = std::size_t{};
  auto result = bytecodeNodeCount(bytes, count);
  if (result != Result::Ok) {
    return result;
  }
  if (bytes[4] < 3) {
    return Result::InvalidBinary;
  }
  auto matches = std::size_t{};
  for (auto index = std::size_t{}; index < count; ++index) {
    const auto *record =
        bytes.data() + bytecodeHeaderBytes + index * customInstructionBytes;
    if (record[16] != 41 || !sameId(record, nodeId)) {
      continue;
    }
    std::memcpy(typeId.data(), record + 24, typeId.size());
    ++matches;
  }
  return matches == 1 && isNonzero(typeId.data()) ? Result::Ok
                                                  : Result::InvalidBinary;
}

auto enqueueSpawn(CustomNodeInvocation &call) noexcept -> Result {
  auto &context = *static_cast<SpawnCallbackContext *>(call.context);
  auto captures = std::array<GeneratorCapture, 16>{};
  auto count = std::size_t{};
  for (auto input = 0u; input < context.inputCount; ++input) {
    for (auto lane = 0u; lane < context.widths[input]; ++lane) {
      auto &capture = captures[count++];
      std::memcpy(capture.nodeId, context.nodeIds[input].data(), 16);
      capture.component = static_cast<std::uint8_t>(lane);
      capture.value = call.inputs[input * 4 + lane];
    }
  }
  call.selectedBranch = 0;
  return context.pool->enqueueSpawn(call.time, captures.data(), count);
}

auto dispatchTemplateSignal(CustomNodeInvocation &call) noexcept -> Result {
  auto &context = *static_cast<SignalInputCallbackContext *>(call.context);
  call.selectedBranch = 0;
  return context.pool->dispatchSignal(context.nodeId.data(), call.time, 10000);
}

auto passthroughDescriptor(CustomNodeDescriptor &out) noexcept -> void {
  std::memcpy(out.typeId, signalPassthroughTypeId.data(), 16);
  out.abiVersion = 1;
  out.execution = CustomExecution::Signal;
  out.branchCount = 1;
  out.function = signalPassthrough;
}
} // namespace liberafx::spawnable_host_detail

namespace liberafx {
void host::SpawnableDebugJob::run(void *self) noexcept {
  auto &job = *static_cast<SpawnableDebugJob *>(self);
  const auto &request = job.request_;
  try {
    switch (request.operation) {
    case SpawnableDebugOperation::Advance:
      job.result_ = job.host_.advance(request.time, request.frameDelta);
      break;
    case SpawnableDebugOperation::Signal:
      job.result_ = job.host_.dispatchSignal(request.node.data());
      break;
    case SpawnableDebugOperation::GameSignal:
      job.result_ = job.host_.fireGameSignal(request.name.data());
      break;
    case SpawnableDebugOperation::NaturalEnd:
      job.result_ = job.host_.requestNaturalEnd();
      break;
    case SpawnableDebugOperation::NotifyEnd:
      job.result_ =
          job.host_.notifyEndRequest(request.scope, request.node.data());
      break;
    default:
      job.result_ = Result::InvalidArgument;
      break;
    }
  } catch (const std::bad_alloc &) {
    job.result_ = Result::InsufficientWork;
  } catch (...) {
    job.result_ = Result::NotReady;
  }
  job.done_.store(true, std::memory_order_release);
}
} // namespace liberafx
