#include "liberafx/HostSignalContext.h"
#include "liberafx/VfxRuntime.h"
#include "liberafx/SpawnableHost.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <limits>
#include <cstdlib>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <vector>
#if defined(_MSC_VER) && defined(_DEBUG)
#include <crtdbg.h>
#endif

namespace {
struct Delivery { unsigned first = 0, second = 0; std::int32_t received = 0; };
auto fieldId = liberafx::SignalFieldId{{1}};
liberafx::Result first(void* context, const liberafx::SignalPayload& payload) noexcept
{
    auto& delivery = *static_cast<Delivery*>(context);
    ++delivery.first;
    auto field = liberafx::SignalFieldValue{};
    auto result = payload.read(fieldId, liberafx::SignalValueKind::Int32, field);
    if (result == liberafx::Result::Ok) delivery.received = field.integer;
    return result;
}
liberafx::Result second(void* context, const liberafx::SignalPayload&) noexcept
{
    ++static_cast<Delivery*>(context)->second;
    return liberafx::Result::Ok;
}

void authoredQuality()
{
    using namespace liberafx;
    auto directory = std::getenv("LIBERAFX_QUALITY_FIXTURE_DIR");
    if (!directory) return;
    auto file = std::ifstream(std::filesystem::path(directory) / "AuthoredQuality.lfxb", std::ios::binary);
    assert(file.good());
    auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
    auto identities = std::ifstream(std::filesystem::path(directory) / "AuthoredQuality.ids", std::ios::binary);
    auto ids = std::array<SignalFieldId, 6>{};
    for (auto& id : ids) identities.read(reinterpret_cast<char*>(id.data()), id.size());
    assert(identities.good());
    auto bindings = std::array<CustomNodeBinding, 128>{};
    auto program = Program{};
    auto loadResult = Program::load(bytes.data(), bytes.size(), nullptr, 0,
        bindings.data(), bindings.size(), program);
    if (loadResult != Result::Ok)
        std::fprintf(stderr, "Authored quality load: %u\n", static_cast<unsigned>(loadResult));
    assert(loadResult == Result::Ok);
    for (auto availability : {HostQualityAvailability::Unprovided, HostQualityAvailability::Known,
        HostQualityAvailability::Unknown, HostQualityAvailability::Unsupported})
    for (auto tier : {HostQualityTier::Low, HostQualityTier::High})
    {
        alignas(64) double work[32768]{}, defaults[32768]{};
        auto instance = Instance{};
        assert(instance.initialize(program, work, 32768, defaults, 32768) == Result::Ok);
        auto snapshot = HostQualitySnapshot{availability, tier, HostQualitySource::EngineEffects, 42};
        assert(instance.setHostQuality(snapshot) == Result::Ok);
        assert(instance.updateAsync(0, [](void*, Job job, void* context) noexcept {
            job(context); return true;
        }, nullptr) == Result::Ok && instance.result() == Result::Ok);
        assert(instance.setHostQuality(snapshot) == Result::Busy);
        assert(instance.dispatchGeneratedSignals(10000) == Result::Ok);
        struct Observed final {
            Instance* instance = nullptr;
            SignalFieldId whenTrue{}, whenFalse{}, value{};
            unsigned trueCount = 0, falseCount = 0, count = 0;
            std::array<double, 4> values{};
            SignalFieldId capture{};
            unsigned captured = 0;
            std::array<double, 4> capturedInputs{};
        } observed{&instance, ids[3], ids[4], ids[5]};
        observed.capture = ids[1];
        assert(instance.setDebugHooks({&observed, [](void* context, const DebugNodeVisit& visit) noexcept {
            auto state = static_cast<Observed*>(context);
            if (!visit.signalSourceNodeId || std::memcmp(visit.nodeId, state->capture.data(), 16) != 0)
                return Result::Ok;
            assert(visit.inputCount == 1 && visit.inputs[0].connected &&
                visit.inputs[0].width == 4 && visit.inputs[0].kind == DebugValueKind::Numeric);
            assert(state->captured < state->capturedInputs.size());
            state->capturedInputs[state->captured++] = visit.inputs[0].components[0];
            return Result::Ok;
        }}) == Result::Ok);
        assert(instance.setSignalObserver([](void* context, const std::uint8_t node[16]) noexcept {
            auto state = static_cast<Observed*>(context);
            auto isTrue = std::memcmp(node, state->whenTrue.data(), 16) == 0;
            auto isFalse = std::memcmp(node, state->whenFalse.data(), 16) == 0;
            if (!isTrue && !isFalse) return Result::Ok;
            if (isTrue) ++state->trueCount;
            if (isFalse) ++state->falseCount;
            assert(state->count < state->values.size());
            auto result = state->instance->value(state->value.data(), state->values[state->count++]);
            assert(result == Result::Ok);
            return Result::Ok;
        }, &observed) == Result::Ok);
        auto result = instance.dispatchSignal(ids[0].data(), 0, 10000);
        if (availability != HostQualityAvailability::Known)
        {
            assert(result == (availability == HostQualityAvailability::Unprovided
                ? Result::NotReady : Result::InvalidValueType));
            assert(observed.count == 0);
            continue;
        }
        assert(result == Result::Ok && observed.count == 2 &&
            observed.values[0] == 1 && observed.values[1] == 2);
        assert(observed.captured == 2 && observed.capturedInputs[0] == 1 && observed.capturedInputs[1] == 2);
        assert(tier == HostQualityTier::High
            ? observed.trueCount == 2 && observed.falseCount == 0
            : observed.falseCount == 2 && observed.trueCount == 0);
        assert(instance.setHostQuality({HostQualityAvailability::Known,
            static_cast<HostQualityTier>(255), HostQualitySource::HostPolicy, 43}) == Result::InvalidValueType);
    }

    auto packageFile = std::ifstream(std::filesystem::path(directory) / "AuthoredQuality.lfxp", std::ios::binary);
    auto packageBytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(packageFile),
        std::istreambuf_iterator<char>());
    auto package = SpawnablePackage{};
    assert(loadSpawnablePackage(packageBytes.data(), packageBytes.size(), package) == Result::Ok);
    auto host = std::unique_ptr<SpawnableHost>{};
    assert(SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
    assert(host->setHostQuality({HostQualityAvailability::Known, HostQualityTier::Ultra,
        HostQualitySource::EngineEffects, 99}) == Result::Ok);
    assert(host->advance(0, 0) == Result::Ok);
    auto value = std::array<double, 4>{};
    auto width = std::uint8_t{};
    assert(host->rootValue(ids[2].data(), value.data(), width) == Result::Ok && value[0] == 1);
    assert(host->rootValue(ids[5].data(), value.data(), width) == Result::Ok && value[0] == 2);
}
}

int main()
{
#if defined(_MSC_VER) && defined(_DEBUG)
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    authoredQuality();
    using namespace liberafx;
    auto payload = SignalPayload{};
    auto field = SignalFieldValue{};
    field.id = fieldId;
    field.kind = SignalValueKind::Int32;
    field.integer = std::numeric_limits<std::int32_t>::min();
    assert(payload.append(field) == Result::Ok);
    assert(payload.append(field) == Result::InvalidArgument);
    auto copy = payload;
    field.integer = 42;
    auto read = SignalFieldValue{};
    assert(copy.read(fieldId, SignalValueKind::Int32, read) == Result::Ok &&
        read.integer == std::numeric_limits<std::int32_t>::min());
    assert(copy.read(fieldId, SignalValueKind::Scalar, read) == Result::InvalidValueType);
    auto quality = HostQualitySnapshot{};
    auto delivery = Delivery{};
    assert(dispatchQualitySignal(quality, QualityComparison::GreaterOrEqual, HostQualityTier::Medium,
        copy, first, second, &delivery) == Result::NotReady && !delivery.first && !delivery.second);
    quality.availability = HostQualityAvailability::Unknown;
    assert(dispatchQualitySignal(quality, QualityComparison::Equal, HostQualityTier::Low,
        copy, first, second, &delivery) == Result::InvalidValueType && !delivery.first);
    quality.availability = HostQualityAvailability::Known;
    quality.tier = HostQualityTier::High;
    assert(dispatchQualitySignal(quality, QualityComparison::GreaterOrEqual, HostQualityTier::Medium,
        copy, first, second, &delivery) == Result::Ok && delivery.first == 1 && !delivery.second &&
        delivery.received == std::numeric_limits<std::int32_t>::min());
    assert(dispatchQualitySignal(quality, QualityComparison::Less, HostQualityTier::Low,
        copy, first, second, &delivery) == Result::Ok && delivery.first == 1 && delivery.second == 1);
    field.kind = SignalValueKind::Boolean;
    field.integer = 2;
    assert(validateSignalField(field) == Result::InvalidValueType);
    field.kind = SignalValueKind::Scalar;
    field.integer = 0;
    field.components[0] = std::numeric_limits<double>::quiet_NaN();
    assert(validateSignalField(field) == Result::NonFinite);
    auto full = SignalPayload{};
    field.components[0] = 1;
    for (auto index = 0u; index < SignalPayload::capacity; ++index) {
        field.id[0] = static_cast<std::uint8_t>(index + 1);
        assert(full.append(field) == Result::Ok);
    }
    field.id[0] = 17;
    assert(full.append(field) == Result::QueueRejected && full.size() == SignalPayload::capacity);
}
