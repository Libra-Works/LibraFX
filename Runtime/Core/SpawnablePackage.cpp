#include "liberafx/SpawnablePackage.h"
#include <cstring>
#include <new>
#include <utility>

namespace liberafx {
namespace {
constexpr auto headerSize = std::size_t{24};
constexpr auto maximumPackageBytes = std::size_t{512} * 1024 * 1024;
constexpr auto maximumBlobBytes = std::size_t{256} * 1024 * 1024;
constexpr auto maximumItems = std::uint32_t{100000};

auto validUtf8(const std::uint8_t* bytes, std::size_t length) noexcept -> bool
{
    auto index = std::size_t{};
    while (index < length) {
        const auto first = bytes[index++];
        if (first <= 0x7f) continue;
        if (first >= 0xc2 && first <= 0xdf) {
            if (index >= length || (bytes[index++] & 0xc0) != 0x80) return false;
            continue;
        }
        if (first >= 0xe0 && first <= 0xef) {
            if (length - index < 2) return false;
            const auto second = bytes[index++];
            const auto third = bytes[index++];
            if ((third & 0xc0) != 0x80 ||
                (first == 0xe0 ? second < 0xa0 || second > 0xbf :
                 first == 0xed ? second < 0x80 || second > 0x9f :
                     (second & 0xc0) != 0x80))
                return false;
            continue;
        }
        if (first >= 0xf0 && first <= 0xf4) {
            if (length - index < 3) return false;
            const auto second = bytes[index++];
            const auto third = bytes[index++];
            const auto fourth = bytes[index++];
            if ((third & 0xc0) != 0x80 || (fourth & 0xc0) != 0x80 ||
                (first == 0xf0 ? second < 0x90 || second > 0xbf :
                 first == 0xf4 ? second < 0x80 || second > 0x8f :
                     (second & 0xc0) != 0x80))
                return false;
            continue;
        }
        return false;
    }
    return true;
}

class Reader final {
public:
    Reader(const std::uint8_t* bytes, std::size_t size) noexcept : bytes_(bytes), size_(size) {}

    auto remaining() const noexcept -> std::size_t { return size_ - offset_; }

    auto readU8(std::uint8_t& out) noexcept -> bool
    {
        if (remaining() < 1) return false;
        out = bytes_[offset_++];
        return true;
    }

    auto readU16(std::uint16_t& out) noexcept -> bool
    {
        if (remaining() < 2) return false;
        out = static_cast<std::uint16_t>(bytes_[offset_]) |
            static_cast<std::uint16_t>(bytes_[offset_ + 1] << 8);
        offset_ += 2;
        return true;
    }

    auto readU32(std::uint32_t& out) noexcept -> bool
    {
        if (remaining() < 4) return false;
        out = std::uint32_t{bytes_[offset_]} |
            (std::uint32_t{bytes_[offset_ + 1]} << 8) |
            (std::uint32_t{bytes_[offset_ + 2]} << 16) |
            (std::uint32_t{bytes_[offset_ + 3]} << 24);
        offset_ += 4;
        return true;
    }

    auto readI32(std::int32_t& out) noexcept -> bool
    {
        auto value = std::uint32_t{};
        if (!readU32(value)) return false;
        std::memcpy(&out, &value, sizeof(out));
        return true;
    }

    auto readU64(std::uint64_t& out) noexcept -> bool
    {
        if (remaining() < 8) return false;
        out = 0;
        for (auto index = 0u; index < 8; ++index)
            out |= std::uint64_t{bytes_[offset_ + index]} << (index * 8);
        offset_ += 8;
        return true;
    }

    auto readBoolean(bool& out) noexcept -> bool
    {
        auto value = std::uint8_t{};
        if (!readU8(value) || value > 1) return false;
        out = value != 0;
        return true;
    }

    auto readGuid(std::array<std::uint8_t, 16>& out) noexcept -> bool
    {
        if (remaining() < out.size()) return false;
        std::memcpy(out.data(), bytes_ + offset_, out.size());
        offset_ += out.size();
        return true;
    }

    auto readOptionalGuid(std::optional<std::array<std::uint8_t, 16>>& out) noexcept -> bool
    {
        auto present = false;
        if (!readBoolean(present)) return false;
        if (!present) {
            out.reset();
            return true;
        }
        auto value = std::array<std::uint8_t, 16>{};
        if (!readGuid(value)) return false;
        out = value;
        return true;
    }

    auto readCount(std::uint32_t& out) noexcept -> bool
    {
        return readU32(out) && out <= maximumItems;
    }

    auto readBlob(std::vector<std::uint8_t>& out) -> bool
    {
        auto length = std::uint32_t{};
        if (!readU32(length) || length > maximumBlobBytes || length > remaining()) return false;
        out.assign(bytes_ + offset_, bytes_ + offset_ + length);
        offset_ += length;
        return true;
    }

    auto readString(std::string& out) -> bool
    {
        auto blob = std::vector<std::uint8_t>{};
        if (!readBlob(blob) || !validUtf8(blob.data(), blob.size())) return false;
        if (blob.empty()) out.clear();
        else out.assign(reinterpret_cast<const char*>(blob.data()), blob.size());
        return true;
    }

private:
    const std::uint8_t* bytes_ = nullptr;
    std::size_t size_ = 0;
    std::size_t offset_ = 0;
};

auto readCapture(Reader& reader, SpawnablePackageCapture& out) -> bool
{
    auto width = std::int32_t{};
    auto hasSourceContainer = std::optional<std::array<std::uint8_t, 16>>{};
    auto component = std::int32_t{};
    if (!reader.readGuid(out.targetNodeId) || !reader.readI32(width) ||
        width < 1 || width > 4 || !reader.readOptionalGuid(hasSourceContainer) ||
        !reader.readGuid(out.sourceNodeId) || !reader.readI32(component) ||
        component < 0 || component > 3 || component + width > 4)
        return false;
    out.width = static_cast<std::uint32_t>(width);
    out.sourceContainerId = hasSourceContainer;
    out.sourceComponent = static_cast<std::uint32_t>(component);
    return true;
}

template<class T, class ReadItem>
auto readList(Reader& reader, std::vector<T>& out, ReadItem readItem) -> bool
{
    auto count = std::uint32_t{};
    if (!reader.readCount(count)) return false;
    out.resize(count);
    for (auto& item : out)
        if (!readItem(item)) return false;
    return true;
}

template<class T, class ReadItem>
auto readOptionalList(Reader& reader, std::optional<std::vector<T>>& out, ReadItem readItem) -> bool
{
    auto present = false;
    if (!reader.readBoolean(present)) return false;
    if (!present) {
        out.reset();
        return true;
    }
    auto items = std::vector<T>{};
    if (!readList(reader, items, readItem)) return false;
    out = std::move(items);
    return true;
}

auto readTemplate(Reader& reader, SpawnablePackageTemplate& out) -> bool
{
    if (!reader.readGuid(out.containerId) || !reader.readGuid(out.signalTypeId) ||
        !reader.readBlob(out.bytecode) || !reader.readBlob(out.renderConfiguration) ||
        !reader.readBlob(out.gpuContainersJson) ||
        !validUtf8(out.gpuContainersJson.data(), out.gpuContainersJson.size()))
        return false;
    if (!readList(reader, out.inputs, [&](SpawnablePackageInput& input) {
        auto width = std::int32_t{};
        if (!reader.readString(input.portId) || !reader.readGuid(input.templateNodeId) ||
            !reader.readString(input.portType) || !reader.readI32(width) || width < 1 || width > 4 ||
            !reader.readBoolean(input.isColor) || !reader.readBoolean(input.isLive))
            return false;
        input.width = static_cast<std::uint32_t>(width);
        return true;
    }))
        return false;
    if (!readOptionalList(reader, out.outputs, [&](SpawnablePackageOutput& output) {
        return reader.readString(output.portId) && reader.readGuid(output.templateNodeId) &&
            reader.readString(output.portType);
    }) ||
        !readOptionalList(reader, out.liveInputs, [&](SpawnablePackageCapture& capture) {
            return readCapture(reader, capture);
        }) ||
        !readOptionalList(reader, out.capturedInputs, [&](SpawnablePackageCapture& capture) {
            return readCapture(reader, capture);
        }) ||
        !readOptionalList(reader, out.signalInputs, [&](SpawnablePackageSignalPort& signal) {
            return reader.readString(signal.portId) && reader.readGuid(signal.templateNodeId) &&
                reader.readGuid(signal.proxyNodeId);
        }) ||
        !readOptionalList(reader, out.signalOutputs, [&](SpawnablePackageSignalPort& signal) {
            return reader.readString(signal.portId) && reader.readGuid(signal.templateNodeId) &&
                reader.readGuid(signal.proxyNodeId);
        }))
        return false;

    auto hasLifecycle = false;
    if (!reader.readBoolean(hasLifecycle)) return false;
    if (hasLifecycle) {
        auto lifecycle = SpawnablePackageLifecycle{};
        if (!reader.readOptionalGuid(lifecycle.onStartNodeId) ||
            !reader.readOptionalGuid(lifecycle.lifeNodeId) ||
            !reader.readOptionalGuid(lifecycle.ownerDestroyNodeId))
            return false;
        out.lifecycle = std::move(lifecycle);
    }
    return true;
}
}

auto loadSpawnablePackage(const std::uint8_t* bytes, std::size_t byteCount,
    SpawnablePackage& out) noexcept -> Result
{
    if (!bytes) return Result::InvalidArgument;
    if (byteCount < headerSize || byteCount > maximumPackageBytes)
        return Result::InvalidBinary;
    auto reader = Reader(bytes, byteCount);
    auto signature = std::uint32_t{};
    auto formatVersion = std::uint16_t{};
    auto encodedHeaderSize = std::uint16_t{};
    auto totalLength = std::uint64_t{};
    auto packageVersion = std::uint32_t{};
    auto reserved = std::uint32_t{};
    if (!reader.readU32(signature) || signature != 0x5058464c)
        return Result::InvalidBinary;
    if (!reader.readU16(formatVersion) || !reader.readU16(encodedHeaderSize) ||
        !reader.readU64(totalLength) || !reader.readU32(packageVersion) ||
        !reader.readU32(reserved) || encodedHeaderSize != headerSize ||
        totalLength != byteCount || reserved != 0)
        return Result::InvalidBinary;
    if (formatVersion != 1 || packageVersion != 9) return Result::UnsupportedBinary;

    try {
        auto package = SpawnablePackage{};
        package.packageVersion = packageVersion;
        if (!reader.readBoolean(package.autoDestroyWhenIdle) ||
            !reader.readBoolean(package.hasDeferredSignals) ||
            !reader.readBoolean(package.hasChildDeferredSignals) ||
            !reader.readOptionalGuid(package.onStartNodeId) ||
            !reader.readOptionalGuid(package.onDestroyNodeId))
            return Result::InvalidBinary;

        if (!readList(reader, package.gameIoPorts, [&](SpawnablePackageGameIoPort& port) {
            auto kind = std::uint8_t{};
            auto width = std::int32_t{};
            if (!reader.readGuid(port.nodeId) || !reader.readString(port.name) ||
                !reader.readU8(kind) || kind > 4 || !reader.readI32(width) ||
                width < 0 || width > 16 || !reader.readBoolean(port.isColor))
                return false;
            port.kind = kind;
            port.width = static_cast<std::uint32_t>(width);
            auto runtimeCount = std::int32_t{};
            if (!reader.readI32(runtimeCount) || runtimeCount < -1 ||
                runtimeCount > static_cast<std::int32_t>(maximumItems))
                return false;
            if (runtimeCount < 0) return true;
            auto runtimeIds = std::vector<std::array<std::uint8_t, 16>>(
                static_cast<std::size_t>(runtimeCount));
            for (auto& nodeId : runtimeIds)
                if (!reader.readGuid(nodeId)) return false;
            port.runtimeNodeIds = std::move(runtimeIds);
            return true;
        }))
            return Result::InvalidBinary;

        if (!reader.readBlob(package.parentBytecode) ||
            !readList(reader, package.templates, [&](SpawnablePackageTemplate& item) {
                return readTemplate(reader, item);
            }) ||
            !readList(reader, package.destroyRoutes, [&](SpawnablePackageDestroyRoute& route) {
                return reader.readGuid(route.sourceContainerId) &&
                    reader.readGuid(route.targetContainerId) &&
                    reader.readOptionalGuid(route.delayNodeId) &&
                    readList(reader, route.captures, [&](SpawnablePackageCapture& capture) {
                        return readCapture(reader, capture);
                    });
            }) ||
            !readList(reader, package.signalRoutes, [&](SpawnablePackageSignalRoute& route) {
                return reader.readGuid(route.sourceContainerId) &&
                    reader.readGuid(route.sourceNodeId) &&
                    reader.readGuid(route.targetNodeId);
            }) ||
            reader.remaining() != 0)
            return Result::InvalidBinary;
        out = std::move(package);
        return Result::Ok;
    } catch (const std::bad_alloc&) {
        return Result::InsufficientWork;
    }
}

}
