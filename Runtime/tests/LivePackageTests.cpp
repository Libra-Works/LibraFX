#include "liberafx/host/LivePackage.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <fstream>
#include <iterator>
#include <string>

auto main(int argc, char** argv) -> int
{
    assert(liberafx::host::validateLiveRestart(nullptr, 0) != liberafx::Result::Ok);
    if (argc != 2) return 77; // Explicit compiler fixtures required; never fabricate acceptance.
    auto read = [&](const char* name) {
        auto stream = std::ifstream(std::string(argv[1]) + "/" + name, std::ios::binary);
        assert(stream);
        return std::vector<std::uint8_t>(std::istreambuf_iterator<char>(stream), {});
    };
    for (auto name : {"Live.before.lfxp", "Live.after.lfxp"}) {
        auto bytes = read(name);
        assert(liberafx::host::validateLiveRestart(bytes.data(), bytes.size()) == liberafx::Result::Ok);
        bytes.resize(bytes.size() / 2);
        assert(liberafx::host::validateLiveRestart(bytes.data(), bytes.size()) != liberafx::Result::Ok);
    }
    auto bytes = read("Live.unsupported.lfxp");
    assert(liberafx::host::validateLiveRestart(bytes.data(), bytes.size()) == liberafx::Result::UnsupportedBinary);
}
