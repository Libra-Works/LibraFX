#include "LbCpuParticlesInternal.h"

namespace liberafx::cpu_particle_detail {
class Reader final {
public:
    Reader(const std::uint8_t* bytes, std::size_t count) : bytes_(bytes), left_(count) {}
    auto u8(std::uint8_t& value) noexcept -> bool
    {
        return copy(&value, 1);
    }
    auto u32(std::uint32_t& value) noexcept -> bool
    {
        if (left_ < 4)
        {
            return false;
        }
        value = std::uint32_t{bytes_[0]} | (std::uint32_t{bytes_[1]} << 8) |
            (std::uint32_t{bytes_[2]} << 16) | (std::uint32_t{bytes_[3]} << 24);
        bytes_ += 4;
        left_ -= 4;
        return true;
    }
    auto number(double& value) noexcept -> bool
    {
        auto bits = std::uint64_t{};
        if (left_ < 8)
        {
            return false;
        }
        for (auto index = 0u; index < 8; ++index)
        {
            bits |= std::uint64_t{bytes_[index]} << (index * 8);
        }
        std::memcpy(&value, &bits, sizeof(value));
        bytes_ += 8;
        left_ -= 8;
        return std::isfinite(value);
    }
    auto id(Id& value) noexcept -> bool
    {
        return copy(value.data(), value.size());
    }
    auto blob(std::vector<std::uint8_t>& out) -> bool
    {
        auto count = std::uint32_t{};
        if (!u32(count) || count > left_ || count > 64 * 1024 * 1024)
        {
            return false;
        }
        out.assign(bytes_, bytes_ + count);
        bytes_ += count;
        left_ -= count;
        return true;
    }
    auto remaining() const noexcept -> std::size_t
    {
        return left_;
    }
private:
    auto copy(void* out, std::size_t count) noexcept -> bool
    {
        if (left_ < count)
        {
            return false;
        }
        std::memcpy(out, bytes_, count);
        bytes_ += count;
        left_ -= count;
        return true;
    }
    const std::uint8_t* bytes_;
    std::size_t left_;
};

auto parse(const std::uint8_t* bytes, std::size_t count,
    std::vector<std::unique_ptr<System>>& systems) -> Result
{
    auto reader = Reader(bytes, count);
    auto version = std::uint32_t{};
    auto systemCount = std::uint32_t{};
    if (!reader.u32(version) || (version != 1 && version != 2) || !reader.u32(systemCount) || systemCount > 1024)
    {
        return Result::InvalidBinary;
    }
    systems.reserve(systemCount);
    for (auto index = 0u; index < systemCount; ++index)
    {
        auto system = std::make_unique<System>();
        if (!reader.id(system->id) || !nonzero(system->id) ||
            !reader.u32(system->capacity) || !system->capacity || system->capacity > 1000000 ||
            !reader.u32(system->seed) || !reader.u32(system->queueCapacity) ||
            !system->queueCapacity || system->queueCapacity > 1000000 ||
            !reader.number(system->rate) || system->rate < 0 ||
            !reader.u32(system->burst) || system->burst > system->capacity ||
            !reader.id(system->emissionSource) || !reader.u32(system->emissionComponent) ||
            system->emissionComponent > 3)
        {
            return Result::InvalidBinary;
        }
        for (const auto& previous : systems)
        {
            if (previous->id == system->id)
            {
                return Result::InvalidBinary;
            }
        }
        for (auto& stage : system->stages)
        {
            auto ioCount = std::uint32_t{};
            if (!reader.id(stage.entry) || !reader.blob(stage.bytes) ||
                !reader.u32(ioCount) || ioCount > 256)
            {
                return Result::InvalidBinary;
            }
            stage.io.resize(ioCount);
            for (auto& io : stage.io)
            {
                if (!reader.u8(io.kind) || io.kind > 3 || !reader.u32(io.width) ||
                    (io.kind < 2 ? io.width < 1 || io.width > 4 : io.width != 0) ||
                    !reader.id(io.node) || !nonzero(io.node) ||
                    !reader.id(io.root) || !reader.u32(io.component) ||
                    io.component > 3 || io.component + io.width > 4)
                {
                    return Result::InvalidBinary;
                }
            }
        }
        if (version >= 2 && (!reader.blob(system->emitterDefinitionBytes) || system->emitterDefinitionBytes.size() > 4096))
            return Result::InvalidBinary;
        systems.push_back(std::move(system));
    }
    return reader.remaining() == 0 ? Result::Ok : Result::InvalidBinary;
}

auto System::initialize(const CustomNodeDescriptor* registry, std::size_t registryCount) -> Result
{
    if (!emitterDefinitionBytes.empty()) {
        auto status = procedural_adapter::decode(emitterDefinitionBytes.data(), emitterDefinitionBytes.size(), emitterDefinition);
        if (status != LIBRA_OK || emitterDefinition.kind != 0) return Result::InvalidBinary;
        status = libra_procedural_model_create(&emitterDefinition.model, emitterDefinition.modifiers.data(),
            static_cast<std::uint32_t>(emitterDefinition.modifiers.size()), &emitterModel);
        if (status != LIBRA_OK) return static_cast<Result>(status);
        const auto result = refreshEmitter(0);
        if (result != Result::Ok) return result;
    }
    auto inputSignalCount = std::size_t{};
    for (auto& stage : stages)
    {
        inputSignalCount += std::count_if(stage.io.begin(), stage.io.end(), [](const auto& io) { return io.kind == 2; });
    }
    callbacks.reserve(23 + inputSignalCount);
    descriptors.reserve(23 + inputSignalCount + registryCount);
    auto add = [&](std::uint8_t operation, std::uint8_t width, bool signal,
        std::initializer_list<std::uint8_t> inputs, bool color)
    {
        callbacks.push_back({this, {}, operation, 0});
        auto descriptor = CustomNodeDescriptor{};
        auto operationTypeId = typeId(operation);
        std::copy(operationTypeId.begin(), operationTypeId.end(), descriptor.typeId);
        descriptor.abiVersion = 1;
        descriptor.execution = signal ? CustomExecution::Signal : CustomExecution::Parameter;
        descriptor.inputCount = static_cast<std::uint8_t>(inputs.size());
        std::copy(inputs.begin(), inputs.end(), descriptor.inputWidths);
        descriptor.outputWidth = width;
        descriptor.outputIsColor = !signal && color;
        descriptor.inputIsColor[0] = signal && color ? 1 : 0;
        descriptor.branchCount = signal ? 1 : 0;
        descriptor.function = invoke;
        descriptor.context = &callbacks.back();
        descriptors.push_back(descriptor);
    };
    add(5, 0, true, {}, false);
    for (auto operation = 0x10u; operation <= 0x18; ++operation)
    {
        auto width = static_cast<std::uint8_t>(operation < 0x12 ? 3 : operation == 0x12 ? 4 : operation == 0x13 ? 2 : 1);
        add(static_cast<std::uint8_t>(operation), width, false, {}, operation == 0x12);
    }
    add(0x20, 0, true, {3}, false);
    add(0x21, 0, true, {3}, false);
    add(0x22, 0, true, {4}, true);
    add(0x23, 0, true, {2}, false);
    add(0x24, 0, true, {1}, false);
    add(0x25, 0, true, {3}, false);
    add(0x42, 3, false, {3, 3}, false);
    add(0x43, 3, false, {3, 1}, false);
    add(0x44, 3, false, {3, 1}, false);
    add(0x45, 3, false, {}, false);
    add(0x46, 3, false, {}, false);
    add(0x47, 2, false, {}, false);
    add(0x48, 4, false, {}, true);
    for (auto stageIndex = 0u; stageIndex < 2; ++stageIndex)
    {
        for (auto& io : stages[stageIndex].io)
        {
            if (io.kind != 2)
            {
                continue;
            }
            callbacks.push_back({this, io.node, 0x36, stageIndex});
        }
    }
    // Project callbacks are resolved in stage scope as usual, not GPU-filtered.
    for (auto index = std::size_t{}; index < registryCount; ++index)
    {
        descriptors.push_back(registry[index]);
    }
    auto totalMemory = std::size_t{sizeof(Slot)} * capacity;
    for (auto& stage : stages)
    {
        if (stage.bytes.size() < 16 || std::memcmp(stage.bytes.data(), "LFXB", 4) != 0)
        {
            return Result::InvalidBinary;
        }
        auto count = std::uint32_t{};
        for (auto lane = 0u; lane < 4; ++lane)
        {
            count |= std::uint32_t{stage.bytes[8 + lane]} << (lane * 8);
        }
        if (count > 10000)
        {
            return Result::InvalidBinary;
        }
        stage.bindings.resize(count);
        auto result = Program::load(stage.bytes.data(), stage.bytes.size(), descriptors.data(),
            descriptors.size(), stage.bindings.data(), stage.bindings.size(), stage.program);
        if (result != Result::Ok)
        {
            return result;
        }
        auto alignment = stage.program.workAlignment();
        stage.workStride = std::max(alignment, (stage.program.workBytes() + alignment - 1) & ~(alignment - 1));
        // Unchanged constants can be read directly from the shared bytecode.
        // Only connected stage inputs need mutable parameter defaults per slot.
        const auto hasBoundInputs = std::any_of(stage.io.begin(), stage.io.end(), [](const auto& io) {
            return io.kind == 0 && nonzero(io.root);
        });
        stage.defaultStride = hasBoundInputs ? stage.program.parameterValueCount() : 0;
        auto perSlot = stage.workStride + stage.defaultStride * sizeof(double);
        if (totalMemory > 512 * 1024 * 1024 || perSlot > 512 * 1024 * 1024 ||
            perSlot > (512 * 1024 * 1024 - totalMemory) / capacity)
        {
            return Result::InsufficientWork;
        }
        totalMemory += perSlot * capacity;
        stage.work.allocate(stage.workStride * capacity, alignment);
        stage.defaults.allocate(stage.defaultStride * sizeof(double) * capacity, stage.program.defaultsAlignment());
    }
    slots = std::make_unique<Slot[]>(capacity);
    activeIndices.resize(capacity);
    incoming.resize(queueCapacity);
    pending.resize(queueCapacity);
    delivery.resize(queueCapacity);
    events.resize(queueCapacity);
    return Result::Ok;
}
}
