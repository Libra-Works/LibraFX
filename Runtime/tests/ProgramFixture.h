#pragma once

#include "liberafx/LbVfxRuntime.h"

#include <array>
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <vector>

namespace runtime_test_fixture {
inline void put32(std::vector<std::uint8_t>& bytes, std::size_t offset, std::uint32_t value)
{
    for (auto index = 0u; index < 4; ++index)
    {
        bytes[offset + index] = static_cast<std::uint8_t>(value >> (index * 8));
    }
}
inline auto promoteSimpleProgram(const std::vector<std::uint8_t>& input,
                                 const liberafx::CustomNodeDescriptor* registry = nullptr,
                                 std::size_t registryCount = 0) -> std::vector<std::uint8_t>
{
    if (input.size() >= 12 && std::memcmp(input.data(), "LFXB", 4) == 0 && input[4] == 1)
    {
        auto count = static_cast<std::uint32_t>(input[8]) |
            (static_cast<std::uint32_t>(input[9]) << 8) |
            (static_cast<std::uint32_t>(input[10]) << 16) |
            (static_cast<std::uint32_t>(input[11]) << 24);
        if (input.size() != 12 + count * 44)
        {
            throw std::runtime_error("Unsupported scalar test payload.");
        }
        auto typed = std::vector<std::uint8_t>(16 + count * 96, 0);
        std::memcpy(typed.data(), input.data(), 12);
        typed[4] = 2;
        typed[12] = 96;
        for (auto index = 0u; index < count; ++index)
        {
            auto source = input.data() + 12 + index * 44;
            auto target = typed.data() + 16 + index * 96;
            std::memcpy(target, source, 17);
            target[17] = 1;
            std::memcpy(target + 24, source + 20, 8);
            target[18] = source[20] == 255 && source[21] == 255 && source[22] == 255 && source[23] == 255 ? 0 : 1;
            target[19] = source[24] == 255 && source[25] == 255 && source[26] == 255 && source[27] == 255 ? 0 : 1;
            std::memcpy(target + 32, source + 28, 8);
            std::memcpy(target + 64, source + 36, 8);
        }
        return promoteSimpleProgram(typed, registry, registryCount);
    }
    if (input.size() < 16 || std::memcmp(input.data(), "LFXB", 4) != 0 ||
        (input[4] != 2 && input[4] != 3 && input[4] != 4 && input[4] != 5))
    {
        throw std::runtime_error("Unsupported synthetic test program.");
    }
    auto count = static_cast<std::uint32_t>(input[8]) |
        (static_cast<std::uint32_t>(input[9]) << 8) |
        (static_cast<std::uint32_t>(input[10]) << 16) |
        (static_cast<std::uint32_t>(input[11]) << 24);
    auto stride = input[4] == 2 ? std::size_t{96} : std::size_t{256};
    auto instructionBytes = 16 + count * stride;
    if (input.size() < instructionBytes ||
        (input[4] != 4 && input[4] != 5 && input.size() != instructionBytes))
    {
        throw std::runtime_error("Unsupported synthetic test program payload.");
    }
    auto bytes = std::vector<std::uint8_t>(16 + count * 256, 0);
    std::memcpy(bytes.data(), input.data(), 16);
    bytes[4] = 11;
    bytes[12] = 0;
    bytes[13] = 1;
    auto types = std::vector<std::uint8_t>(count * 8, 0);
    auto routes = std::vector<std::array<std::uint32_t, 3>>{};
    for (auto index = 0u; index < count; ++index)
    {
        auto offset = 16 + index * 256;
        std::memcpy(bytes.data() + offset, input.data() + 16 + index * stride, stride);
        auto opcode = bytes[offset + 16];
        auto custom = opcode == 40 || opcode == 41;
        put32(bytes, offset + (custom ? 44 : 112), UINT32_MAX);
        auto shapeType = [](auto width, auto color) -> std::uint8_t
        {
            return color ? 6 : width == 1 ? 1 : width == 2 ? 3 : width == 3 ? 4 : width == 4 ? 5 : 0;
        };
        auto outputType = opcode == 41 ? 0u : shapeType(bytes[offset + 17], bytes[offset + 20] & 1);
        if (!((opcode >= 1 && opcode <= 39) || opcode == 42) && !custom)
        {
            throw std::runtime_error("Unsupported synthetic test opcode.");
        }
        if (custom)
        {
            auto descriptor = static_cast<const liberafx::CustomNodeDescriptor*>(nullptr);
            for (auto item = std::size_t{}; item < registryCount; ++item)
            {
                if (std::memcmp(bytes.data() + offset + 24, registry[item].typeId, 16) == 0)
                {
                    descriptor = &registry[item];
                    break;
                }
            }
            if (!descriptor)
            {
                auto builtinCount = std::size_t{};
                auto builtins = liberafx::builtinNodes(builtinCount);
                for (auto item = std::size_t{}; item < builtinCount; ++item)
                {
                    if (std::memcmp(bytes.data() + offset + 24, builtins[item].typeId, 16) == 0)
                    {
                        descriptor = &builtins[item];
                        break;
                    }
                }
            }
            outputType = opcode == 41 ? 0u : shapeType(bytes[offset + 17], bytes[offset + 20] & 1);
            types[index * 8 + 1] = bytes[offset + 18];
            if (bytes[offset + 18] && !descriptor)
            {
                throw std::runtime_error("Custom test node requires a descriptor.");
            }
            for (auto slot = 0u; slot < bytes[offset + 18]; ++slot)
            {
                types[index * 8 + 2 + slot] = shapeType(descriptor->inputWidths[slot], descriptor->inputIsColor[slot]);
            }
            if (opcode == 41)
            {
                for (auto branch = 0u; branch < bytes[offset + 19]; ++branch)
                {
                    auto target = std::uint32_t{};
                    std::memcpy(&target, bytes.data() + offset + 208 + branch * 4, 4);
                    if (target != UINT32_MAX)
                    {
                        routes.push_back({index, branch, target});
                    }
                    if (input[4] == 5)
                    {
                        auto extraOffset = std::uint32_t{};
                        auto extraCount = std::uint32_t{};
                        std::memcpy(&extraOffset, bytes.data() + offset + 224 + branch * 8, 4);
                        std::memcpy(&extraCount, bytes.data() + offset + 228 + branch * 8, 4);
                        if (extraOffset > input.size() - instructionBytes ||
                            extraCount > (input.size() - instructionBytes - extraOffset) / 4)
                        {
                            throw std::runtime_error("Unsupported synthetic test routes.");
                        }
                        for (auto extra = 0u; extra < extraCount; ++extra)
                        {
                            std::memcpy(&target, input.data() + instructionBytes + extraOffset + extra * 4, 4);
                            routes.push_back({index, branch, target});
                        }
                    }
                }
            }
        }
        types[index * 8] = static_cast<std::uint8_t>(outputType);
        if (!custom && opcode != 2 && opcode != 42)
        {
            auto inputType = static_cast<std::uint8_t>(outputType);
            if (opcode >= 23 && opcode <= 25) inputType = 1;
            else if (opcode >= 29 && opcode <= 30) inputType = 3;
            else if (opcode >= 31 && opcode <= 32) inputType = 4;
            else if (opcode >= 33 && opcode <= 37) inputType = 5;
            else if (opcode == 38) inputType = 6;
            else if (opcode == 39) inputType = 5;
            auto binary = (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22);
            types[index * 8 + 1] = binary ? 2 : 1;
            types[index * 8 + 2] = inputType;
            if (binary)
            {
                types[index * 8 + 3] = inputType;
            }
        }
    }
    if (input[4] == 4 || input[4] == 5)
    {
        bytes.insert(bytes.end(), input.begin() + instructionBytes, input.end());
    }
    auto append = [&](const char* magic, std::size_t size)
    {
        auto offset = bytes.size();
        bytes.resize(offset + size, 0);
        std::memcpy(bytes.data() + offset, magic, 4);
        return offset;
    };
    auto enumOffset = append("LFEB", 16);
    bytes[enumOffset + 4] = 1;
    put32(bytes, enumOffset + 12, 16);
    auto logicalOffset = append("LFLE", 16 + routes.size() * 12);
    bytes[logicalOffset + 4] = 1;
    put32(bytes, logicalOffset + 8, static_cast<std::uint32_t>(routes.size()));
    put32(bytes, logicalOffset + 12, static_cast<std::uint32_t>(16 + routes.size() * 12));
    for (auto index = std::size_t{}; index < routes.size(); ++index)
    {
        auto offset = logicalOffset + 16 + index * 12;
        put32(bytes, offset, routes[index][0]);
        bytes[offset + 4] = static_cast<std::uint8_t>(routes[index][1]);
        put32(bytes, offset + 8, routes[index][2]);
    }
    auto typeOffset = append("LFTY", 8);
    bytes[typeOffset + 4] = 1;
    bytes[typeOffset + 6] = 8;
    bytes.insert(bytes.end(), types.begin(), types.end());
    return bytes;
}
}
