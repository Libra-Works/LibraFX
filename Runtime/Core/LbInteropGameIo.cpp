#include "LbInteropInternal.h"

using namespace liberafx::interop_detail;

LibraResult LIBRA_INTEROP_CALL libra_game_io_configure(LibraInstance instance, uint32_t eventCapacity,
                                                       const uint8_t containerId[16], uint64_t instanceId) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (native->lifecycle || native->gameIo || native->owner->program.version() < 6 ||
        native->owner->program.version() > 11)
    {
      return LIBRA_INVALID_VALUE_TYPE;
    }
    auto converted = std::vector<liberafx::NamedIoPort>{};
    auto count = native->owner->program.gameIoPortCount();
    converted.reserve(count);
    for (auto index = std::size_t{}; index < count; ++index)
    {
      auto port = liberafx::ProgramGameIoPort{};
      if (!native->owner->program.gameIoPort(index, port) || port.kind > 4)
      {
        return LIBRA_INVALID_BINARY;
      }
      auto item = liberafx::NamedIoPort{};
      item.name = port.name;
      item.kind = static_cast<liberafx::NamedIoKind>(port.kind);
      std::memcpy(item.nodeId, port.nodeId, 16);
      std::memcpy(item.additionalNodeIds, port.additionalNodeIds, sizeof(item.additionalNodeIds));
      item.width = port.width;
      item.isColor = port.isColor != 0;
      item.nodeCount = port.nodeCount;
      converted.push_back(item);
    }
    auto identity = liberafx::NamedIoIdentity{};
    if (containerId)
    {
      std::memcpy(identity.containerId, containerId, 16);
    }
    identity.instanceId = instanceId;
    auto result = liberafx::NamedIo::create(native->instance, converted.data(), converted.size(), eventCapacity,
                                            native->gameIo, identity);
    return toLibraResult(result);
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_set_property(LibraInstance instance, const char *name,
                                                          const double *values, uint32_t width) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    return toLibraResult(native->gameIo->setProperty(name, values, width));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_set_owner_matrix(LibraInstance instance, const double values[16]) {
  if (!values)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    return toLibraResult(native->gameIo->setOwnerMatrix(values, 16));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_start(LibraInstance instance, double time, const uint8_t onStartNodeId[16],
                                                   uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    if (native->lifecycle)
    {
      return LIBRA_NOT_READY;
    }
    native->gameIoStarted = true;
    return toLibraResult(native->gameIo->start(time, onStartNodeId, instructionBudget));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_update(LibraInstance instance, double time) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    if (native->lifecycle)
    {
      return LIBRA_NOT_READY;
    }
    return toLibraResult(native->gameIo->update(time));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_fire_signal(LibraInstance instance, const char *name, double time,
                                                         uint32_t instructionBudget) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    if (native->lifecycle)
    {
      auto status = native->lifecycle->validateSignalTime(time);
      if (status != liberafx::Result::Ok)
      {
        return toLibraResult(status);
      }
    }
    return toLibraResult(native->gameIo->fireSignal(name, time, instructionBudget));
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_publish_properties(LibraInstance instance) {
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    return toLibraResult(native->gameIo->publishProperties());
  });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_next_event(LibraInstance instance, LibraGameIoEvent *outEvent,
                                                        uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    auto event = liberafx::NamedIoEvent{};
    if (!native->gameIo->peekEvent(event))
    {
      return LIBRA_OK;
    }
    if (event.width > 4)
    {
      return LIBRA_INVALID_VALUE_TYPE;
    }
    if (!native->gameIo->popEvent(event))
    {
      return LIBRA_INTERNAL_ERROR;
    }
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
LibraResult LIBRA_INTEROP_CALL libra_game_io_next_event_v2(LibraInstance instance, LibraGameIoEventV2 *outEvent,
                                                           uint32_t *hasEvent) {
  if (!outEvent || !hasEvent)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outEvent = {};
  *hasEvent = 0;
  return guarded([&]() -> LibraResult {
    auto native = findInstance(instance);
    if (!native)
    {
      return LIBRA_INVALID_HANDLE;
    }
    if (!native->gameIo)
    {
      return LIBRA_NOT_READY;
    }
    auto event = liberafx::NamedIoEvent{};
    if (!native->gameIo->popEvent(event))
    {
      return LIBRA_OK;
    }
    std::memcpy(outEvent->containerId, event.identity.containerId, 16);
    outEvent->instanceId = event.identity.instanceId;
    outEvent->portIndex = event.portIndex;
    outEvent->kind = static_cast<uint8_t>(event.kind);
    outEvent->width = event.width;
    outEvent->isColor = event.isColor ? 1 : 0;
    std::copy(std::begin(event.values), std::end(event.values), outEvent->values);
    *hasEvent = 1;
    return LIBRA_OK;
  });
}
