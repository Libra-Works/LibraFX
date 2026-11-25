#include "ProgramFixture.h"
#include "liberafx/LbLivePreview.h"
#include "liberafx/host/LbPackagePreviewContract.h"

#include <array>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <limits>
#include <string>
#include <vector>

namespace {
void requireAt(bool condition, int line) { if (!condition) { std::fprintf(stderr, "LivePreviewTests:%d\n", line); std::abort(); } }
#define require(condition) requireAt((condition), __LINE__)
void put(std::uint8_t* p, std::uint64_t value, int count)
{
    for (auto i = 0; i < count; ++i) p[i] = static_cast<std::uint8_t>(value >> (i * 8));
}
void putDouble(std::uint8_t* p, double value)
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(bits));
    put(p, bits, 8);
}
}

int main()
{
    using namespace liberafx::livepreview;
    auto bytes = std::array<std::uint8_t, 52>{};
    std::memcpy(bytes.data(), "LFXP", 4);
    put(bytes.data() + 4, 1, 2);
    put(bytes.data() + 6, 1, 2);
    put(bytes.data() + 8, 10, 8);
    putDouble(bytes.data() + 24, 2.5);
    putDouble(bytes.data() + 32, 1);
    put(bytes.data() + 40, 3, 4);
    put(bytes.data() + 44, 4, 4);
    std::memcpy(bytes.data() + 48, "LFXB", 4);
    auto frame = FrameView{};
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::Ok);
    require(frame.kind == Kind::Program && frame.sequence == 10 && frame.payload == bytes.data() + 48 &&
            frame.payloadLength == 4 && frame.timeSeconds == 2.5 && frame.flags == 3);
    auto cursor = SequenceCursor{};
    require(cursor.validate(frame) == SequenceResult::Accept && !cursor.hasProgram());
    auto playback = frame;
    playback.kind = Kind::Playback;
    playback.sequence = 11;
    playback.baseSequence = 10;
    require(cursor.validate(playback) == SequenceResult::Resync);
    require(cursor.commit(frame) == SequenceResult::Accept && cursor.hasProgram());
    require(cursor.validate(playback) == SequenceResult::Accept && cursor.sequence() == 10);
    require(cursor.commit(playback) == SequenceResult::Accept);
    require(cursor.validate(playback) == SequenceResult::Resync);
    require(cursor.validate(frame) == SequenceResult::Stale);
    frame.sequence = 11;
    require(cursor.commit(frame) == SequenceResult::Accept);
    auto error = frame;
    error.kind = Kind::CompileError;
    error.sequence = 12;
    require(cursor.commit(error) == SequenceResult::Accept && cursor.hasProgram());
    playback.sequence = 13;
    playback.baseSequence = 11;
    require(cursor.validate(playback) == SequenceResult::Resync);
    playback.baseSequence = 12;
    require(cursor.commit(playback) == SequenceResult::Accept);
    cursor.reset();
    require(!cursor.hasProgram() && cursor.validate(playback) == SequenceResult::Resync);

    auto control = std::array<std::uint8_t, headerBytes + 2>{};
    std::memcpy(control.data(), "LFXP", 4);
    put(control.data() + 4, 1, 2);
    put(control.data() + 6, 4, 2);
    put(control.data() + 44, 2, 4);
    std::memcpy(control.data() + headerBytes, "{}", 2);
    require(decodeFrame(control.data(), control.size(), frame) == DecodeResult::Ok);
    require(frame.kind == Kind::Control && cursor.commit(frame) == SequenceResult::Resync &&
            !cursor.hasProgram());
    put(control.data() + 8, 1, 8);
    require(decodeFrame(control.data(), control.size(), frame) == DecodeResult::InvalidFrame);
    put(control.data() + 8, 0, 8);
    control[headerBytes] = 0xff;
    require(decodeFrame(control.data(), control.size(), frame) == DecodeResult::InvalidFrame);
    auto scene = std::vector<std::uint8_t>(headerBytes + maxErrorBytes + 1, ' ');
    std::memset(scene.data(), 0, headerBytes);
    std::memcpy(scene.data(), "LFXP", 4);
    put(scene.data() + 4, 1, 2);
    put(scene.data() + 6, 5, 2);
    put(scene.data() + 44, maxErrorBytes + 1, 4);
    require(decodeFrame(scene.data(), scene.size(), frame) == DecodeResult::Ok &&
        frame.kind == Kind::Scene && cursor.commit(frame) == SequenceResult::Resync);
    put(scene.data() + 6, 4, 2);
    require(decodeFrame(scene.data(), scene.size(), frame) == DecodeResult::InvalidFrame);
    put(scene.data() + 6, 5, 2);
    put(scene.data() + 8, 14, 8);
    require(decodeFrame(scene.data(), scene.size(), frame) == DecodeResult::Ok);
    require(cursor.validate(frame) == SequenceResult::Resync);
    auto sceneBase = FrameView{};
    sceneBase.kind = Kind::Program;
    sceneBase.sequence = 13;
    require(cursor.commit(sceneBase) == SequenceResult::Accept);
    require(cursor.commit(frame) == SequenceResult::Accept && cursor.sequence() == 14);
    require(cursor.validate(frame) == SequenceResult::Stale);
    auto nextPlayback = FrameView{};
    nextPlayback.kind = Kind::Playback;
    nextPlayback.sequence = 15;
    nextPlayback.baseSequence = 14;
    require(cursor.commit(nextPlayback) == SequenceResult::Accept);
    cursor.reset();

    require(decodeFrame(nullptr, bytes.size(), frame) == DecodeResult::InvalidFrame);
    require(decodeFrame(bytes.data(), 47, frame) == DecodeResult::InvalidFrame);
    require(decodeFrame(bytes.data(), 51, frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 44, 0xffffffffu, 4);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 44, 4, 4);
    put(bytes.data() + 40, 4, 4);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 40, 0, 4);
    putDouble(bytes.data() + 24, std::numeric_limits<double>::quiet_NaN());
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    putDouble(bytes.data() + 24, 0);
    putDouble(bytes.data() + 32, -1);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    putDouble(bytes.data() + 32, std::numeric_limits<double>::infinity());
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    putDouble(bytes.data() + 32, 1);
    put(bytes.data() + 6, 9, 2);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 6, 2, 2);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 44, 0, 4);
    require(decodeFrame(bytes.data(), headerBytes, frame) == DecodeResult::Ok);
    put(bytes.data() + 6, 3, 2);
    put(bytes.data() + 44, 4, 4);
    bytes[48] = 0xc0;
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    auto largeError = std::array<std::uint8_t, headerBytes + maxErrorBytes + 1>{};
    std::memcpy(largeError.data(), "LFXP", 4);
    put(largeError.data() + 4, 1, 2);
    put(largeError.data() + 6, 3, 2);
    put(largeError.data() + 44, maxErrorBytes + 1, 4);
    require(decodeFrame(largeError.data(), largeError.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 6, 1, 2);
    put(bytes.data() + 16, 1, 8);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);

    // Core PackagePreviewContract tests
    {
        using namespace liberafx::host;
        struct MockAdapter final : public IPackagePreviewAdapter {
            bool hasMaterials = false;
            bool curves = false;
            bool loaded = false;
            bool playingState = false;
            double rateState = 0.0;
            std::vector<ParameterDeltaUpdate> applied;
            auto loadPackage(const std::uint8_t*, std::size_t, bool playing, double rate) -> liberafx::Result override {
                loaded = true;
                playingState = playing;
                rateState = rate;
                return liberafx::Result::Ok;
            }
            auto setPlayback(bool playing, double rate) -> liberafx::Result override {
                playingState = playing;
                rateState = rate;
                return liberafx::Result::Ok;
            }
            auto applyParameterDelta(const ParameterDeltaUpdate* updates, std::size_t count) -> liberafx::Result override {
                applied.assign(updates, updates + count);
                return liberafx::Result::Ok;
            }
            void resetPreview() override { loaded = false; }
            auto hasBuiltinSpriteMaterials() const -> bool override { return hasMaterials; }
            auto supportsCurveDeltas() const noexcept -> bool override { return curves; }
        } adapter;

        FrameView progFrame{};
        progFrame.kind = Kind::Program;
        progFrame.flags = 1;
        progFrame.playbackRate = 1.5;
        const std::uint8_t dummyPkg[4]{'L', 'F', 'X', 'P'};
        progFrame.payload = dummyPkg;
        progFrame.payloadLength = 4;
        require(dispatchPackagePreviewFrame(progFrame, adapter) == liberafx::Result::NotReady);
        adapter.hasMaterials = true;
        require(dispatchPackagePreviewFrame(progFrame, adapter) == liberafx::Result::Ok &&
                adapter.loaded && adapter.playingState && adapter.rateState == 1.5);

        const char* deltaJson =
            R"({"type":"parameter_delta","sequence":5,"updates":[{"containerId":"00000000-0000-0000-0000-000000000000","nodeId":"00112233-4455-6677-8899-aabbccddeeff","inputSlot":2,"component":1,"isInt32":false,"intValue":0,"floatValue":42.5}]})";
        FrameView deltaFrame{};
        deltaFrame.kind = Kind::Scene;
        deltaFrame.payload = reinterpret_cast<const std::uint8_t*>(deltaJson);
        deltaFrame.payloadLength = std::strlen(deltaJson);
        deltaFrame.sequence = 4;
        require(dispatchPackagePreviewFrame(deltaFrame, adapter) == liberafx::Result::InvalidArgument);
        require(adapter.applied.empty());
        deltaFrame.sequence = 5;
        require(dispatchPackagePreviewFrame(deltaFrame, adapter) == liberafx::Result::Ok);
        require(adapter.applied.size() == 1 && adapter.applied[0].inputSlot == 2 &&
                adapter.applied[0].component == 1 && !adapter.applied[0].isInt32 &&
                adapter.applied[0].floatValue == 42.5f && adapter.applied[0].nodeId[0] == 0x00 &&
                adapter.applied[0].nodeId[15] == 0xff);

        auto curveJson = std::string{
            R"({"type":"parameter_delta","sequence":6,"updates":[{"kind":"fcurve","containerId":"00000000-0000-0000-0000-000000000000","nodeId":"00000000-0000-0000-0000-000000000001","keys":[]}]})"};
        deltaFrame.sequence = 6;
        deltaFrame.payload = reinterpret_cast<const std::uint8_t*>(curveJson.data());
        deltaFrame.payloadLength = curveJson.size();
        adapter.applied.clear();
        require(dispatchPackagePreviewFrame(deltaFrame, adapter) == liberafx::Result::UnsupportedBinary);
        require(adapter.applied.empty());
        adapter.curves = true;
        require(dispatchPackagePreviewFrame(deltaFrame, adapter) == liberafx::Result::Ok);
        require(adapter.applied.size() == 1 && adapter.applied[0].isCurve);

        auto preciseJson = std::string(deltaJson);
        auto valuePos = preciseJson.find("42.5");
        preciseJson.replace(valuePos, 4, "1.0000000000000002");
        auto parsed = std::vector<ParameterDeltaUpdate>{};
        require(parseParameterDeltaJson(preciseJson.data(), preciseJson.size(), parsed) == liberafx::Result::Ok);
        require(parsed.size() == 1 && parsed[0].floatValue == 1.0000000000000002);

        auto invalidValues = std::array<const char*, 8>{
            "1.5", "2147483648", "-2147483649", "1e999", "+1", "01", "1.", "NaN"};
        for (auto value : invalidValues)
        {
            auto invalid = std::string(deltaJson);
            auto start = invalid.find("\"intValue\":0");
            invalid.replace(start, std::strlen("\"intValue\":0"), std::string("\"intValue\":") + value);
            parsed.assign(1, {});
            require(parseParameterDeltaJson(invalid.data(), invalid.size(), parsed) == liberafx::Result::InvalidArgument);
            require(parsed.empty());
        }
        auto malformed = std::array<std::string, 5>{
            R"({"type":"parameter_delta","sequence":1,"updates":[],"updates":[]})",
            R"({"type":"parameter_delta","updates":[]})",
            R"({"type":"parameter_delta","sequence":-1,"updates":[]})",
            R"({"type":"parameter_delta","sequence":1.0,"updates":[]})",
            R"({"type":"parameter_delta","sequence":1,"updates":[],"unknown":1})"};
        for (const auto& invalid : malformed)
        {
            parsed.assign(1, {});
            require(parseParameterDeltaJson(invalid.data(), invalid.size(), parsed) == liberafx::Result::InvalidArgument);
            require(parsed.empty());
        }
        auto partial = std::string(deltaJson);
        auto end = partial.find("]}");
        partial.insert(end, ",{}");
        parsed.assign(1, {});
        require(parseParameterDeltaJson(partial.data(), partial.size(), parsed) == liberafx::Result::InvalidArgument);
        require(parsed.empty());

        const char* badJson = R"({"type":"other","updates":[]})";
        std::vector<ParameterDeltaUpdate> out;
        require(parseParameterDeltaJson(badJson, std::strlen(badJson), out) == liberafx::Result::UnsupportedBinary);
    }
    {
        using namespace liberafx;
        auto raw = std::vector<std::uint8_t>(16 + 256 + 2 * 40, 0);
        std::memcpy(raw.data(), "LFXB", 4);
        put(raw.data() + 4, 4, 2);
        raw[6] = 1;
        put(raw.data() + 8, 1, 4);
        put(raw.data() + 12, 256, 2);
        raw[16 + 15] = 1;
        raw[16 + 16] = 42;
        raw[16 + 17] = 1;
        std::memset(raw.data() + 16 + 24, 0xff, 8);
        put(raw.data() + 16 + 228, 2, 4);
        raw[272 + 32] = 1;
        putDouble(raw.data() + 312, 1);
        putDouble(raw.data() + 320, 1);
        auto package = SpawnablePackage{};
        package.autoDestroyWhenIdle = false;
        package.parentBytecode = runtime_test_fixture::promoteSimpleProgram(raw);
        auto host = std::unique_ptr<SpawnableHost>{};
        require(SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
        auto id = std::array<std::uint8_t, 16>{};
        id[15] = 1;
        auto json = std::string{
            R"({"type":"parameter_delta","sequence":1,"updates":[{"kind":"fcurve","containerId":"00000000-0000-0000-0000-000000000000","nodeId":"00000000-0000-0000-0000-000000000001","keys":[{"time":0,"value":10,"inSlope":0,"outSlope":0,"interpolation":1},{"time":1,"value":20,"inSlope":0,"outSlope":0,"interpolation":1}]}]})"};
        require(host::applyParameterDeltaJson(*host, json.data(), json.size()) == Result::Ok);
        require(host->advance(0.5, 0.5) == Result::Ok);
        auto result = std::array<double, 4>{};
        auto width = std::uint8_t{};
        require(host->rootValue(id.data(), result.data(), width) == Result::Ok && result[0] == 15);
        auto invalid = json;
        auto offset = invalid.find("\"time\":1");
        invalid.replace(offset, std::strlen("\"time\":1"), "\"time\":0");
        require(host::applyParameterDeltaJson(*host, invalid.data(), invalid.size()) == Result::InvalidArgument);
        auto reset = std::string{
            R"({"type":"parameter_delta","sequence":2,"updates":[{"kind":"fcurve","containerId":"00000000-0000-0000-0000-000000000000","nodeId":"00000000-0000-0000-0000-000000000001","reset":true}]})"};
        require(host::applyParameterDeltaJson(*host, reset.data(), reset.size()) == Result::Ok);
        require(host->advance(0.6, 0.1) == Result::Ok);
        require(host->rootValue(id.data(), result.data(), width) == Result::Ok && result[0] == 0.6);
    }
    {
        using namespace liberafx;
        auto package = SpawnablePackage{};
        package.autoDestroyWhenIdle = false;
        auto raw = std::vector<std::uint8_t>(16 + 96, 0);
        std::memcpy(raw.data(), "LFXB", 4);
        raw[4] = 2;
        raw[6] = 1;
        raw[8] = 1;
        raw[12] = 96;
        raw[16 + 15] = 1;
        raw[16 + 16] = 1;
        raw[16 + 17] = 1;
        std::memset(raw.data() + 16 + 24, 0xff, 8);
        putDouble(raw.data() + 16 + 32, 2.0);
        package.parentBytecode = runtime_test_fixture::promoteSimpleProgram(raw);
        auto host = std::unique_ptr<SpawnableHost>{};
        require(SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
        require(host->advance(0, 0) == Result::Ok);
        auto validId = std::array<std::uint8_t, 16>{};
        validId[15] = 1;
        auto updates = std::array<host::ParameterDeltaUpdate, 2>{};
        updates[0].nodeId = validId;
        updates[0].floatValue = 1.0000000000000002;
        updates[1].nodeId[15] = 2;
        updates[1].floatValue = 3.0;
        auto delta = std::string{
            R"({"type":"parameter_delta","sequence":1,"updates":[{"containerId":"00000000-0000-0000-0000-000000000000","nodeId":"00000000-0000-0000-0000-000000000001","inputSlot":0,"component":0,"isInt32":false,"intValue":0,"floatValue":4.0},{"containerId":"00000000-0000-0000-0000-000000000000","nodeId":"00000000-0000-0000-0000-000000000002","inputSlot":0,"component":0,"isInt32":false,"intValue":0,"floatValue":5.0}]})"};
        require(host::applyParameterDeltaJson(*host, delta.data(), delta.size()) == Result::NodeNotFound);
        require(host->setNodeParameters(updates.data(), updates.size()) == Result::NodeNotFound);
        require(host->advance(1, 1) == Result::Ok);
        auto result = std::array<double, 4>{};
        auto width = std::uint8_t{};
        require(host->rootValue(validId.data(), result.data(), width) == Result::Ok && result[0] == 2.0);
        require(host->setNodeParameters(updates.data(), 1) == Result::Ok);
        require(host->advance(2, 1) == Result::Ok);
        require(host->rootValue(validId.data(), result.data(), width) == Result::Ok &&
            result[0] == 1.0000000000000002);
        auto reset = std::string{
            R"({"type":"parameter_delta","sequence":2,"updates":[{"containerId":"00000000-0000-0000-0000-000000000000","nodeId":"00000000-0000-0000-0000-000000000001","inputSlot":0,"component":0,"reset":true}]})"};
        require(host::applyParameterDeltaJson(*host, reset.data(), reset.size()) == Result::Ok);
        require(host->advance(3, 1) == Result::Ok);
        require(host->rootValue(validId.data(), result.data(), width) == Result::Ok && result[0] == 2.0);
    }
    {
        using namespace liberafx;
        auto package = SpawnablePackage{};
        package.autoDestroyWhenIdle = false;
        auto raw = std::vector<std::uint8_t>(16 + 4 * 96, 0);
        std::memcpy(raw.data(), "LFXB", 4);
        raw[4] = 2;
        raw[6] = 1;
        raw[8] = 4;
        raw[12] = 96;
        for (auto row = 0u; row < 4; ++row)
        {
            auto offset = 16 + row * 96;
            raw[offset + 15] = static_cast<std::uint8_t>(row + 1);
            raw[offset + 16] = 9;
            raw[offset + 17] = 4;
            std::memset(raw.data() + offset + 24, 0xff, 8);
            putDouble(raw.data() + offset + 32 + row * 8, 1);
        }
        package.parentBytecode = runtime_test_fixture::promoteSimpleProgram(raw);
        auto host = std::unique_ptr<SpawnableHost>{};
        require(SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
        require(host->advance(0, 0) == Result::Ok);
        auto check = [&](bool identity)
        {
            for (auto row = 0u; row < 4; ++row)
            {
                auto id = std::array<std::uint8_t, 16>{};
                id[15] = static_cast<std::uint8_t>(row + 1);
                auto values = std::array<double, 4>{};
                auto width = std::uint8_t{};
                require(host->rootValue(id.data(), values.data(), width) == Result::Ok && width == 4);
                for (auto column = 0u; column < 4; ++column)
                {
                    auto expected = identity ? (row == column ? 1.0 : 0.0)
                                             : static_cast<double>(row * 4 + column + 1);
                    require(values[column] == expected);
                }
            }
        };
        auto matrixJson = std::string{
            R"({"type":"parameter_delta","sequence":1,"updates":[{"kind":"matrix","containerId":"00000000-0000-0000-0000-000000000000","rowNodeIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002","00000000-0000-0000-0000-000000000003","00000000-0000-0000-0000-000000000004"],"values":[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16]}]})"};
        auto invalid = matrixJson;
        auto rowPosition = invalid.find("00000000-0000-0000-0000-000000000004");
        require(rowPosition != std::string::npos);
        invalid.replace(rowPosition, 36, "00000000-0000-0000-0000-000000000063");
        require(host::applyParameterDeltaJson(*host, invalid.data(), invalid.size()) == Result::NodeNotFound);
        require(host->advance(1, 1) == Result::Ok);
        check(true);
        invalid = matrixJson;
        rowPosition = invalid.find("00000000-0000-0000-0000-000000000004");
        invalid.replace(rowPosition, 36, "00000000-0000-0000-0000-000000000003");
        auto parsed = std::vector<host::ParameterDeltaUpdate>{};
        require(host::parseParameterDeltaJson(invalid.data(), invalid.size(), parsed) == Result::InvalidArgument && parsed.empty());
        invalid = matrixJson;
        invalid.replace(invalid.find("15,16"), 5, "15");
        require(host::parseParameterDeltaJson(invalid.data(), invalid.size(), parsed) == Result::InvalidArgument && parsed.empty());
        require(host::applyParameterDeltaJson(*host, matrixJson.data(), matrixJson.size()) == Result::Ok);
        require(host->advance(2, 1) == Result::Ok);
        check(false);
        auto resetJson = std::string{
            R"({"type":"parameter_delta","sequence":2,"updates":[{"kind":"matrix","containerId":"00000000-0000-0000-0000-000000000000","rowNodeIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002","00000000-0000-0000-0000-000000000003","00000000-0000-0000-0000-000000000004"],"reset":true}]})"};
        require(host::applyParameterDeltaJson(*host, resetJson.data(), resetJson.size()) == Result::Ok);
        require(host->advance(3, 1) == Result::Ok);
        check(true);
    }
    if (auto folder = std::getenv("LIBERAFX_INSTANCE_GAMEIO_FIXTURES"))
    {
        using namespace liberafx;
        auto file = std::ifstream(std::filesystem::path(folder) / "RootMatrixGameIo.lfxp", std::ios::binary);
        require(file.good());
        auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), {});
        auto package = SpawnablePackage{};
        require(loadSpawnablePackage(bytes.data(), bytes.size(), package) == Result::Ok);
        auto rows = std::vector<std::array<std::uint8_t, 16>>{};
        for (const auto& port : package.gameIoPorts)
        {
            if (port.name == "local" && port.width == 16 && port.runtimeNodeIds)
            {
                rows = *port.runtimeNodeIds;
            }
        }
        require(rows.size() == 4);
        auto host = std::unique_ptr<SpawnableHost>{};
        require(SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
        require(host->advance(0, 0) == Result::Ok);
        auto json = std::string{
            R"({"type":"parameter_delta","sequence":1,"updates":[{"kind":"matrix","containerId":"00000000-0000-0000-0000-000000000000","rowNodeIds":[)"};
        for (auto index = std::size_t{}; index < rows.size(); ++index)
        {
            if (index != 0)
            {
                json += ',';
            }
            json += '"';
            for (auto byte = std::size_t{}; byte < rows[index].size(); ++byte)
            {
                if (byte == 4 || byte == 6 || byte == 8 || byte == 10)
                {
                    json += '-';
                }
                constexpr auto digits = "0123456789abcdef";
                json += digits[rows[index][byte] >> 4];
                json += digits[rows[index][byte] & 15];
            }
            json += '"';
        }
        json += R"(],"values":[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16]}]})";
        require(host::applyParameterDeltaJson(*host, json.data(), json.size()) == Result::Ok);
        require(host->advance(1, 1) == Result::Ok);
        for (auto row = std::size_t{}; row < rows.size(); ++row)
        {
            auto values = std::array<double, 4>{};
            auto width = std::uint8_t{};
            require(host->rootValue(rows[row].data(), values.data(), width) == Result::Ok && width == 4);
            for (auto column = std::size_t{}; column < 4; ++column)
            {
                require(values[column] == static_cast<double>(row * 4 + column + 1));
            }
        }
    }
    return 0;
}
