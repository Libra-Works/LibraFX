#include "ProgramFixture.h"
#include "liberafx/LbSpawnablePackage.h"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <vector>

namespace {
void require(bool condition)
{
    if (!condition)
    {
        std::abort();
    }
}
void put(std::uint8_t* destination, std::uint64_t value, int size)
{
    for (auto index = 0; index < size; ++index)
    {
        destination[index] = static_cast<std::uint8_t>(value >> (index * 8));
    }
}
}

int main()
{
    auto bytes = std::vector<std::uint8_t>(65, 0);
    std::memcpy(bytes.data(), "LFXP", 4);
    put(bytes.data() + 4, 5, 2);
    put(bytes.data() + 6, 24, 2);
    put(bytes.data() + 8, bytes.size(), 8);
    put(bytes.data() + 16, 13, 4);
    auto package = liberafx::SpawnablePackage{};
    require(liberafx::loadSpawnablePackage(bytes.data(), bytes.size(), package) == liberafx::Result::Ok);
    require(package.packageVersion == 13 && package.templates.empty());
    for (auto version = 1u; version < 5; ++version)
    {
        put(bytes.data() + 4, version, 2);
        put(bytes.data() + 16, version + 8, 4);
        require(liberafx::loadSpawnablePackage(bytes.data(), bytes.size(), package) ==
            liberafx::Result::UnsupportedBinary);
    }
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
    auto current = runtime_test_fixture::promoteSimpleProgram(raw);
    auto program = liberafx::Program{};
    require(liberafx::Program::load(current.data(), current.size(), program) == liberafx::Result::Ok);
    for (auto version = 1u; version < 11; ++version)
    {
        current[4] = static_cast<std::uint8_t>(version);
        require(liberafx::Program::load(current.data(), current.size(), program) ==
            liberafx::Result::UnsupportedBinary);
    }
    current[4] = 11;
    auto oldIo = std::vector<std::uint8_t>(8 + 24 + 2, 0);
    std::memcpy(oldIo.data(), "LFIO", 4);
    oldIo[4] = 1;
    oldIo[8 + 15] = 1;
    oldIo[8 + 17] = 1;
    oldIo[8 + 20] = 1;
    oldIo[8 + 24] = 'a';
    current.insert(current.begin() + 16 + 256 + 16, oldIo.begin(), oldIo.end());
    require(liberafx::Program::load(current.data(), current.size(), program) ==
        liberafx::Result::InvalidBinary);
    return 0;
}
