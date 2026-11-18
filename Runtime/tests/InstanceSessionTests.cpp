#include "liberafx/LbInstanceSession.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <array>
#include <cassert>
#include <chrono>
#include <cstring>
#include <thread>
#include <utility>

namespace {
using namespace liberafx;
using Clock = std::chrono::steady_clock;
void waitUntil(Clock::time_point deadline)
{
    assert(Clock::now() < deadline); // A regression must fail, not hang indefinitely.
    std::this_thread::yield();
}

auto perform(InstanceSession& session, InstanceRequest request) -> InstanceCompletion
{
    auto ticket = RequestId{};
    assert(session.submit(request, ticket) == Result::Ok);
    auto completion = InstanceCompletion{};
    assert(session.takeCompletion(ticket, completion) == Result::NotReady);
    auto processed = std::size_t{};
    assert(session.process(1, processed) == Result::Ok && processed == 1);
    assert(session.takeCompletion(ticket, completion) == Result::Ok);
    assert(session.takeCompletion(ticket, completion) == Result::InvalidHandle);
    return completion;
}

auto scalar(double value) -> std::array<std::uint8_t, 56>
{
    auto bytes = std::array<std::uint8_t, 56>{};
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = bytes[6] = bytes[8] = 1;
    bytes[12 + 15] = bytes[12 + 16] = 1;
    for (auto index = 12 + 20; index < 12 + 28; ++index) bytes[index] = 0xff;
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(value));
    for (auto index = 0u; index < 8; ++index)
        bytes[12 + 28 + index] = static_cast<std::uint8_t>(bits >> (index * 8));
    return bytes;
}

void nativeTransactions()
{
    auto firstBytes = scalar(3);
    auto secondBytes = scalar(9);
    auto failureBytes = scalar(9);
    failureBytes[12 + 16] = 6; // Divide defaults 9 / 0: valid graph, failed initial evaluation.
    auto firstProgram = Program{}, secondProgram = Program{}, failureProgram = Program{};
    assert(Program::load(firstBytes.data(), firstBytes.size(), firstProgram) == Result::Ok);
    assert(Program::load(secondBytes.data(), secondBytes.size(), secondProgram) == Result::Ok);
    assert(Program::load(failureBytes.data(), failureBytes.size(), failureProgram) == Result::Ok);
    alignas(16) auto firstWork = std::array<double, 8>{}, secondWork = std::array<double, 8>{};
    alignas(16) auto firstDefaults = std::array<double, 8>{}, secondDefaults = std::array<double, 8>{};
    alignas(16) auto failureWork = std::array<double, 8>{}, failureDefaults = std::array<double, 8>{};
    auto duration = Lifetime{};
    assert(Lifetime::finite(2, duration) == Result::Ok);
    auto first = LifecycleSessionEffect(firstProgram, firstWork.data(), firstWork.size(),
        firstDefaults.data(), firstDefaults.size(), duration);
    auto second = LifecycleSessionEffect(secondProgram, secondWork.data(), secondWork.size(),
        secondDefaults.data(), secondDefaults.size(), duration);
    auto invalid = LifecycleSessionEffect(secondProgram, nullptr, 0, nullptr, 0);
    auto alias = LifecycleSessionEffect(secondProgram, firstWork.data(), firstWork.size(),
        firstDefaults.data(), firstDefaults.size());
    auto failing = LifecycleSessionEffect(failureProgram, failureWork.data(), failureWork.size(),
        failureDefaults.data(), failureDefaults.size());
    auto slots = std::array<InstanceSlot, 1>{};
    auto queue = std::array<InstanceRequestStorage, 4>{};
    auto session = InstanceSession({123, 456}, slots.data(), slots.size(), queue.data(), queue.size());
    auto created = perform(session, {InstanceRequestKind::Create, {}, &first});
    assert(created.result == Result::Ok && !created.returnedEffect);
    auto id = created.instance;
    auto value = std::array<double, 4>{};
    auto width = std::uint8_t{};
    auto read = [&]() {
        assert(session.read(id, firstBytes.data() + 12, value.data(), width) == Result::Ok);
        assert(width == 1);
        return value[0];
    };
    assert(read() == 3);
    auto replacement = perform(session, {InstanceRequestKind::Replace, id, &second,
        ReplacementPolicy::PreserveState});
    assert(replacement.result == Result::UnsupportedBinary && replacement.returnedEffect == &second);
    assert(read() == 3);
    replacement = perform(session, {InstanceRequestKind::Replace, id, &invalid, ReplacementPolicy::Restart});
    assert(replacement.result != Result::Ok && replacement.returnedEffect == &invalid);
    assert(read() == 3);
    replacement = perform(session, {InstanceRequestKind::Replace, id, &alias, ReplacementPolicy::Restart});
    assert(replacement.result == Result::InvalidArgument && replacement.returnedEffect == &alias);
    assert(read() == 3);
    auto finished = false;
    assert(session.advance(id, 1, 1, finished) == Result::Ok && !finished);
    replacement = perform(session, {InstanceRequestKind::Replace, id, &failing, ReplacementPolicy::Restart});
    assert(replacement.result == Result::DivideByZero && replacement.returnedEffect == &failing);
    assert(read() == 3 && firstDefaults[0] == 3);
    assert(session.advance(id, 0.5, 0.5, finished) == Result::InvalidArgument); // Old age was preserved too.
    replacement = perform(session, {InstanceRequestKind::Replace, id, &second, ReplacementPolicy::Restart});
    assert(replacement.result == Result::Ok && replacement.returnedEffect == &first);
    assert(read() == 9); // Same opaque ID; new graph, fresh lifetime starting at zero.
    assert(session.advance(id, 1, 1, finished) == Result::Ok && !finished);
    assert(session.advance(id, 2, 1, finished) == Result::Ok && finished);
    assert(read() == 9); // Natural expiry did NOT release the slot or its memory.
    auto foreign = id;
    ++foreign.session.high;
    assert(session.read(foreign, firstBytes.data() + 12, value.data(), width) == Result::InvalidHandle);
    auto destroyed = perform(session, {InstanceRequestKind::Destroy, id});
    assert(destroyed.result == Result::Ok && destroyed.returnedEffect == &second);
    assert(session.read(id, firstBytes.data() + 12, value.data(), width) == Result::InvalidHandle);
    auto recreated = perform(session, {InstanceRequestKind::Create, {}, &first});
    assert(recreated.result == Result::Ok && recreated.instance.slot == id.slot &&
        recreated.instance.generation != id.generation);
    assert(perform(session, {InstanceRequestKind::Destroy, id}).result == Result::InvalidHandle);
    assert(perform(session, {InstanceRequestKind::Destroy, recreated.instance}).result == Result::Ok);
    assert(session.close() == Result::Ok);
}

struct Probe final : SessionEffect {
    unsigned starts = 0, stops = 0, releases = 0, advances = 0;
    bool ending = false, done = false;
    std::atomic<bool>* entered = nullptr;
    std::atomic<bool>* resume = nullptr;
    Result start() noexcept override { ++starts; ending = done = false; return Result::Ok; }
    Result advance(double, double) noexcept override
    {
        ++advances;
        if (entered) {
            entered->store(true);
            auto deadline = Clock::now() + std::chrono::seconds(30);
            while (!resume->load()) waitUntil(deadline);
        }
        done = ending && advances >= 2; // Simulates pending owned work.
        return Result::Ok;
    }
    Result naturalEnd() noexcept override { ending = true; return Result::Ok; }
    Result forceStop() noexcept override { ++stops; return Result::Ok; }
    Result read(const std::uint8_t[16], double[4], std::uint8_t&) const noexcept override { return Result::Ok; }
    bool finished() const noexcept override { return done; }
    void release() noexcept override { ++releases; }
};

void boundedAndForeign()
{
    auto a = Probe{}, b = Probe{};
    auto slots = std::array<InstanceSlot, 1>{}, otherSlots = std::array<InstanceSlot, 1>{};
    auto queue = std::array<InstanceRequestStorage, 2>{}, otherQueue = std::array<InstanceRequestStorage, 2>{};
    // Same host nonce still cannot cross two manager incarnations.
    auto session = InstanceSession({1, 2}, slots.data(), slots.size(), queue.data(), queue.size());
    auto other = InstanceSession({1, 2}, otherSlots.data(), otherSlots.size(), otherQueue.data(), otherQueue.size());
    auto ticket = RequestId{}, second = RequestId{}, rejected = RequestId{};
    assert(session.submit({InstanceRequestKind::Create, {}, &a}, ticket) == Result::Ok);
    assert(other.submit({InstanceRequestKind::Create, {}, &a}, rejected) == Result::Busy);
    assert(session.submit({InstanceRequestKind::Create, {}, &b}, second) == Result::Ok);
    auto processed = std::size_t{};
    assert(session.process(2, processed) == Result::Ok && processed == 2);
    // Unconsumed completion cells also count against the bound.
    assert(session.submit({InstanceRequestKind::Create, {}, &b}, rejected) == Result::QueueRejected);
    auto completion = InstanceCompletion{};
    assert(other.takeCompletion(ticket, completion) == Result::InvalidHandle);
    assert(session.takeCompletion(ticket, completion) == Result::Ok && completion.result == Result::Ok);
    auto id = completion.instance;
    assert(other.submit({InstanceRequestKind::Destroy, id}, rejected) == Result::InvalidHandle);
    assert(session.takeCompletion(second, completion) == Result::Ok &&
        completion.result == Result::InsufficientWork && completion.returnedEffect == &b);
    assert(b.starts == 0); // Full instance table did not initialize a resource.
    assert(perform(session, {InstanceRequestKind::NaturalEnd, id}).result == Result::Ok);
    auto finished = false;
    assert(session.advance(id, 1, 1, finished) == Result::Ok && !finished && a.stops == 0);
    assert(session.close() == Result::NotReady); // Never force-stop on shutdown.
    assert(session.advance(id, 2, 1, finished) == Result::Ok && finished && a.stops == 0);
    assert(perform(session, {InstanceRequestKind::Destroy, id}).result == Result::Ok);
    assert(a.stops == 1 && a.releases == 1);
    assert(session.close() == Result::Ok && other.close() == Result::Ok);
}

void jobBoundary()
{
    auto entered = std::atomic<bool>{false}, resume = std::atomic<bool>{false};
    auto effect = Probe{};
    effect.entered = &entered;
    effect.resume = &resume;
    auto slots = std::array<InstanceSlot, 1>{};
    auto queue = std::array<InstanceRequestStorage, 2>{};
    auto session = InstanceSession({3, 4}, slots.data(), slots.size(), queue.data(), queue.size());
    auto id = perform(session, {InstanceRequestKind::Create, {}, &effect}).instance;
    auto destroy = RequestId{};
    auto producer = std::thread([&]() {
        auto deadline = Clock::now() + std::chrono::seconds(30);
        while (!entered.load()) waitUntil(deadline);
        // Submission does not wait on a long-running evaluation.
        assert(session.submit({InstanceRequestKind::Destroy, id}, destroy) == Result::Ok);
        auto processed = std::size_t{};
        assert(session.process(1, processed) == Result::Busy);
        assert(session.close() == Result::Busy);
        assert(effect.stops == 0 && effect.releases == 0);
        resume.store(true);
    });
    auto finished = false;
    assert(session.advance(id, 1, 1, finished) == Result::Ok);
    producer.join();
    assert(effect.stops == 0);
    auto processed = std::size_t{};
    assert(session.process(1, processed) == Result::Ok && processed == 1);
    auto prematureReuse = RequestId{};
    assert(session.submit({InstanceRequestKind::Create, {}, &effect}, prematureReuse) == Result::Busy);
    auto completion = InstanceCompletion{};
    assert(session.takeCompletion(destroy, completion) == Result::Ok &&
        completion.result == Result::Ok && completion.returnedEffect == &effect);
    assert(effect.stops == 1 && effect.releases == 1);
    assert(session.close() == Result::Ok);
}

void exhaustedGenerationAndOrder()
{
    auto effect = Probe{};
    auto slots = std::array<InstanceSlot, 1>{};
    auto queue = std::array<InstanceRequestStorage, 3>{};
    auto session = InstanceSession({7, 8}, slots.data(), slots.size(), queue.data(), queue.size());
    // Controlled source-test injection, before any producer/owner operations.
    slots[0].generation = UINT64_MAX;
    auto id = perform(session, {InstanceRequestKind::Create, {}, &effect}).instance;
    assert(id.generation == UINT64_MAX);
    auto first = RequestId{}, second = RequestId{};
    assert(session.submit({InstanceRequestKind::Destroy, id}, first) == Result::Ok);
    assert(session.submit({InstanceRequestKind::Destroy, id}, second) == Result::Ok);
    auto processed = std::size_t{};
    assert(session.process(3, processed) == Result::Ok && processed == 2);
    auto completion = InstanceCompletion{};
    assert(session.takeCompletion(first, completion) == Result::Ok &&
        completion.result == Result::Ok && completion.returnedEffect == &effect);
    assert(session.takeCompletion(second, completion) == Result::Ok && completion.result == Result::InvalidHandle);
    assert(effect.stops == 1 && effect.releases == 1);
    assert(perform(session, {InstanceRequestKind::Create, {}, &effect}).result == Result::InsufficientWork);
    assert(effect.starts == 1); // Exhausted slot never wraps.
    assert(session.close() == Result::Ok);
}

void concurrentChurn()
{
    constexpr auto workerCount = std::size_t{4};
    auto slots = std::array<InstanceSlot, workerCount>{};
    auto queue = std::array<InstanceRequestStorage, 2>{}; // Deliberately less than worker count.
    auto session = InstanceSession({5, 6}, slots.data(), slots.size(), queue.data(), queue.size());
    auto effects = std::array<Probe, workerCount>{};
    auto workers = std::array<std::thread, workerCount>{};
    auto completed = std::atomic<std::size_t>{0};
    for (auto index = std::size_t{}; index < workerCount; ++index) {
        workers[index] = std::thread([&, index]() {
            auto transact = [&](InstanceRequest request) {
                auto deadline = Clock::now() + std::chrono::seconds(30);
                auto ticket = RequestId{};
                auto result = Result::QueueRejected;
                while ((result = session.submit(request, ticket)) == Result::QueueRejected)
                    waitUntil(deadline);
                assert(result == Result::Ok);
                auto completion = InstanceCompletion{};
                while ((result = session.takeCompletion(ticket, completion)) == Result::NotReady)
                    waitUntil(deadline);
                assert(result == Result::Ok && completion.result == Result::Ok);
                return completion;
            };
            for (auto iteration = 0u; iteration < 64; ++iteration) {
                auto created = transact({InstanceRequestKind::Create, {}, &effects[index]});
                transact({InstanceRequestKind::Destroy, created.instance});
            }
            ++completed;
        });
    }
    auto deadline = Clock::now() + std::chrono::seconds(30);
    while (completed.load() != workerCount) {
        auto processed = std::size_t{};
        assert(session.process(2, processed) == Result::Ok);
        waitUntil(deadline);
    }
    for (auto& worker : workers) worker.join();
    for (auto& effect : effects)
        assert(effect.starts == 64 && effect.stops == 64 && effect.releases == 64);
    assert(session.close() == Result::Ok);
}

void existingPackageHost()
{
    auto bytes = std::array<std::uint8_t, 16 + 96>{};
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 2; bytes[6] = 1; bytes[8] = 1; bytes[12] = 96;
    bytes[16 + 15] = bytes[16 + 16] = bytes[16 + 17] = 1;
    for (auto index = 16 + 24; index < 16 + 32; ++index) bytes[index] = 0xff;
    auto scalarBytes = scalar(3);
    std::memcpy(bytes.data() + 16 + 32, scalarBytes.data() + 12 + 28, 8);
    auto makeHost = [&]() {
        auto package = SpawnablePackage{};
        package.packageVersion = 1;
        package.autoDestroyWhenIdle = false;
        package.parentBytecode.assign(bytes.begin(), bytes.end());
        auto host = std::unique_ptr<SpawnableHost>{};
        assert(SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
        return host;
    };
    auto host = makeHost(), candidateHost = makeHost();
    auto effect = SpawnableSessionEffect(*host), candidate = SpawnableSessionEffect(*candidateHost);
    auto slots = std::array<InstanceSlot, 1>{};
    auto queue = std::array<InstanceRequestStorage, 2>{};
    auto session = InstanceSession({9, 10}, slots.data(), slots.size(), queue.data(), queue.size());
    auto created = perform(session, {InstanceRequestKind::Create, {}, &effect});
    assert(created.result == Result::Ok);
    auto replacement = perform(session, {InstanceRequestKind::Replace, created.instance,
        &candidate, ReplacementPolicy::Restart});
    assert(replacement.result == Result::UnsupportedBinary && replacement.returnedEffect == &candidate);
    assert(candidateHost->requestNaturalEnd() == Result::NotReady); // Never started on rejected replacement.
    auto value = std::array<double, 4>{};
    auto width = std::uint8_t{};
    assert(session.read(created.instance, bytes.data() + 16, value.data(), width) == Result::Ok &&
        width == 1 && value[0] == 3);
    struct Context final {
        SessionEffect* expected;
        SpawnableHost* host;
    };
    auto context = Context{&effect, host.get()};
    assert(session.access(created.instance, [](void* data, SessionEffect& actual) noexcept {
        auto& context = *static_cast<Context*>(data);
        assert(context.expected == &actual);
        auto event = SpawnableHostEvent{};
        assert(!context.host->popEvent(event));
        return Result::Ok;
    }, &context) == Result::Ok);
    assert(perform(session, {InstanceRequestKind::NaturalEnd, created.instance}).result == Result::Ok);
    auto finished = false;
    assert(session.advance(created.instance, 1, 1, finished) == Result::Ok && finished);
    assert(perform(session, {InstanceRequestKind::Destroy, created.instance}).result == Result::Ok);
    assert(session.close() == Result::Ok);
}
}

int main()
{
    nativeTransactions();
    boundedAndForeign();
    jobBoundary();
    concurrentChurn();
    exhaustedGenerationAndOrder();
    existingPackageHost();
}
