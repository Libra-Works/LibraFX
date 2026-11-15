#include "liberafx/GeneratorPool.h"

#include <cmath>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <optional>
#include <vector>

namespace liberafx {
namespace {
auto inlineEnqueue(void*, Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}

struct AlignedBuffer final {
    void* data = nullptr;
    std::size_t alignment = 16;
    ~AlignedBuffer() { if (data) ::operator delete(data, std::align_val_t{alignment}); }
    void allocate(std::size_t bytes, std::size_t requested)
    {
        alignment = requested < 16 ? 16 : requested;
        if (bytes) data = ::operator new(bytes, std::align_val_t{alignment});
    }
};

struct Slot final {
    AlignedBuffer work;
    AlignedBuffer defaults;
    // Must be destroyed before its storage.
    std::optional<Instance> instance;
    GeneratorInstanceId id = 0;
    double start = 0;
    std::uint64_t seed = 0;
};
struct SpawnCommand final {
    double time = 0;
    std::size_t count = 0;
    GeneratorCapture captures[16]{};
};
}

struct GeneratorPool::Impl final {
    const Program& program;
    std::uint8_t containerId[16];
    std::size_t maxActive, budget, committed, workBytes, defaultBytes, slotCost;
    std::vector<std::unique_ptr<Slot>> slots;
    std::vector<GeneratorEvent> events;
    std::vector<SpawnCommand> commands;
    std::size_t eventHead = 0, eventCount = 0, active = 0;
    std::size_t commandHead = 0, commandCount = 0;
    GeneratorInstanceId nextId = 1;
    mutable std::mutex mutex;

    Impl(const Program& p, const std::uint8_t* id, std::size_t maximum,
         std::size_t limit, std::size_t capacity, std::size_t base,
         std::size_t work, std::size_t defaults, std::size_t cost)
        : program(p), maxActive(maximum), budget(limit), committed(base),
          workBytes(work), defaultBytes(defaults), slotCost(cost), events(capacity), commands(capacity)
    {
        std::memcpy(containerId, id, 16);
        slots.reserve(maximum);
    }
    auto find(GeneratorInstanceId id) noexcept -> Slot*
    {
        if (!id) return nullptr;
        for (auto& slot : slots) if (slot->id == id) return slot.get();
        return nullptr;
    }
    void emit(GeneratorEventKind kind, const Slot& slot) noexcept
    {
        auto& event = events[(eventHead + eventCount) % events.size()];
        event = {};
        event.kind = kind;
        std::memcpy(event.containerId, containerId, 16);
        event.instanceId = slot.id;
        event.startTime = slot.start;
        event.seed = slot.seed;
        ++eventCount;
    }
};

GeneratorPool::GeneratorPool(std::unique_ptr<Impl> impl) noexcept : impl_(std::move(impl)) {}
GeneratorPool::~GeneratorPool() = default;

auto GeneratorPool::create(const Program& program, const std::uint8_t containerId[16],
    std::size_t maxActive, std::size_t memoryBudgetBytes, std::size_t eventCapacity,
    std::unique_ptr<GeneratorPool>& out) noexcept -> Result
{
    out.reset();
    if (!containerId || !maxActive || maxActive > 4096 || !eventCapacity ||
        eventCapacity > 8192 || !memoryBudgetBytes) return Result::InvalidArgument;
    auto work = program.workBytes();
    auto defaultsCount = program.parameterValueCount();
    if (defaultsCount > std::numeric_limits<std::size_t>::max() / sizeof(double) ||
        work > std::numeric_limits<std::size_t>::max() - 7) return Result::InsufficientWork;
    work = (work + 7) & ~std::size_t{7};
    auto defaults = defaultsCount * sizeof(double);
    auto base = sizeof(Impl) + sizeof(GeneratorPool) + 128 +
        eventCapacity * (sizeof(GeneratorEvent) + sizeof(SpawnCommand)) +
        maxActive * sizeof(std::unique_ptr<Slot>);
    if (defaults > std::numeric_limits<std::size_t>::max() - sizeof(Slot) - 128 ||
        work > std::numeric_limits<std::size_t>::max() - defaults - sizeof(Slot) - 128)
        return Result::InsufficientWork;
    auto cost = sizeof(Slot) + work + defaults + 128;
    if (base > memoryBudgetBytes || cost > memoryBudgetBytes - base)
        return Result::InsufficientWork;
    try {
        auto impl = std::make_unique<Impl>(program, containerId, maxActive, memoryBudgetBytes,
            eventCapacity, base, work, defaults, cost);
        out.reset(new GeneratorPool(std::move(impl)));
        return Result::Ok;
    } catch (const std::bad_alloc&) { return Result::InsufficientWork; }
    catch (...) { return Result::InvalidArgument; }
}

auto GeneratorPool::spawn(double time, const GeneratorCapture* captures, std::size_t count,
                          GeneratorInstanceId& out) noexcept -> Result
{
    out = 0;
    if (!std::isfinite(time) || count > 256 || (count && !captures)) return Result::InvalidArgument;
    try {
        auto lock = std::lock_guard<std::mutex>(impl_->mutex);
        if (impl_->eventCount == impl_->events.size()) return Result::QueueRejected;
        if (impl_->active == impl_->maxActive || !impl_->nextId) return Result::InsufficientWork;
        auto slot = static_cast<Slot*>(nullptr);
        for (auto& candidate : impl_->slots) if (!candidate->id) { slot = candidate.get(); break; }
        if (!slot) {
            if (impl_->slotCost > impl_->budget - impl_->committed) return Result::InsufficientWork;
            auto candidate = std::make_unique<Slot>();
            candidate->work.allocate(impl_->workBytes, impl_->program.workAlignment());
            candidate->defaults.allocate(impl_->defaultBytes, impl_->program.defaultsAlignment());
            slot = candidate.get();
            impl_->slots.push_back(std::move(candidate));
            impl_->committed += impl_->slotCost;
        }
        slot->instance.emplace();
        auto result = slot->instance->initialize(impl_->program,
            static_cast<double*>(slot->work.data), impl_->workBytes / sizeof(double),
            static_cast<double*>(slot->defaults.data), impl_->defaultBytes / sizeof(double));
        if (result == Result::Ok)
            for (auto i = std::size_t{}; i < count; ++i) {
                result = slot->instance->setUnconnectedParameter(captures[i].nodeId, captures[i].inputSlot,
                    captures[i].component, captures[i].value);
                if (result != Result::Ok) break;
            }
        if (result != Result::Ok) { slot->instance.reset(); return result; }
        slot->id = impl_->nextId++;
        slot->start = time;
        auto seed = slot->id;
        for (auto byte : impl_->containerId) seed = (seed ^ byte) * UINT64_C(1099511628211);
        seed += UINT64_C(0x9e3779b97f4a7c15);
        seed = (seed ^ (seed >> 30)) * UINT64_C(0xbf58476d1ce4e5b9);
        seed = (seed ^ (seed >> 27)) * UINT64_C(0x94d049bb133111eb);
        slot->seed = seed ^ (seed >> 31);
        ++impl_->active;
        out = slot->id;
        impl_->emit(GeneratorEventKind::Spawn, *slot);
        return Result::Ok;
    } catch (const std::bad_alloc&) { return Result::InsufficientWork; }
    catch (...) { return Result::InvalidArgument; }
}

auto GeneratorPool::stop(GeneratorInstanceId id) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (impl_->eventCount == impl_->events.size()) return Result::QueueRejected;
    impl_->emit(GeneratorEventKind::Stop, *slot);
    slot->instance.reset();
    slot->id = 0;
    --impl_->active;
    return Result::Ok;
}

auto GeneratorPool::enqueueSpawn(double time, const GeneratorCapture* captures, std::size_t count) noexcept -> Result
{
    if (!std::isfinite(time) || count > 16 || (count && !captures)) return Result::InvalidArgument;
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->commandCount == impl_->commands.size()) return Result::QueueRejected;
    auto& command = impl_->commands[(impl_->commandHead + impl_->commandCount) % impl_->commands.size()];
    command.time = time;
    command.count = count;
    for (auto i = std::size_t{}; i < count; ++i) command.captures[i] = captures[i];
    ++impl_->commandCount;
    return Result::Ok;
}

auto GeneratorPool::processSpawns(std::size_t maxCommands, std::size_t& processed) noexcept -> Result
{
    processed = 0;
    while (processed < maxCommands) {
        auto command = SpawnCommand{};
        {
            auto lock = std::lock_guard<std::mutex>(impl_->mutex);
            if (!impl_->commandCount) return Result::Ok;
            command = impl_->commands[impl_->commandHead];
        }
        auto id = GeneratorInstanceId{};
        auto result = spawn(command.time, command.captures, command.count, id);
        if (result != Result::Ok) return result;
        {
            auto lock = std::lock_guard<std::mutex>(impl_->mutex);
            impl_->commandHead = (impl_->commandHead + 1) % impl_->commands.size();
            --impl_->commandCount;
        }
        ++processed;
    }
    return Result::Ok;
}

auto GeneratorPool::reset() noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->active > impl_->events.size() - impl_->eventCount) return Result::QueueRejected;
    for (auto& slot : impl_->slots) if (slot->id) {
        impl_->emit(GeneratorEventKind::Stop, *slot);
        slot->instance.reset();
        slot->id = 0;
    }
    impl_->active = 0;
    impl_->commandCount = 0;
    impl_->commandHead = 0;
    return Result::Ok;
}

auto GeneratorPool::evaluate(GeneratorInstanceId id, double time) noexcept -> Result
{
    if (!std::isfinite(time)) return Result::InvalidArgument;
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    auto elapsed = time - slot->start;
    if (!std::isfinite(elapsed) || elapsed < 0) return Result::InvalidArgument;
    auto result = slot->instance->updateAsync(elapsed, inlineEnqueue, nullptr);
    return result == Result::Ok ? slot->instance->result() : result;
}

auto GeneratorPool::value(GeneratorInstanceId id, const std::uint8_t nodeId[16],
                          double out[4], std::uint8_t& width) noexcept -> Result
{
    if (!nodeId || !out) return Result::InvalidArgument;
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    auto slot = impl_->find(id);
    return slot ? slot->instance->valueComponents(nodeId, out, width) : Result::InvalidHandle;
}

auto GeneratorPool::popEvent(GeneratorEvent& out) noexcept -> bool
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (!impl_->eventCount) return false;
    out = impl_->events[impl_->eventHead];
    impl_->eventHead = (impl_->eventHead + 1) % impl_->events.size();
    --impl_->eventCount;
    return true;
}

auto GeneratorPool::activeCount() const noexcept -> std::size_t
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    return impl_->active;
}
}
