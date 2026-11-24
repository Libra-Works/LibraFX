#include "liberafx/LbLivePreview.h"
#include "liberafx/host/LbPackagePreviewContract.h"

#include <array>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <vector>

namespace {
void require(bool condition) { if (!condition) std::abort(); }
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
        require(dispatchPackagePreviewFrame(deltaFrame, adapter) == liberafx::Result::Ok);
        require(adapter.applied.size() == 1 && adapter.applied[0].inputSlot == 2 &&
                adapter.applied[0].component == 1 && !adapter.applied[0].isInt32 &&
                adapter.applied[0].floatValue == 42.5f && adapter.applied[0].nodeId[0] == 0x00 &&
                adapter.applied[0].nodeId[15] == 0xff);

        const char* badJson = R"({"type":"other","updates":[]})";
        std::vector<ParameterDeltaUpdate> out;
        require(parseParameterDeltaJson(badJson, std::strlen(badJson), out) == liberafx::Result::UnsupportedBinary);
    }
    return 0;
}
