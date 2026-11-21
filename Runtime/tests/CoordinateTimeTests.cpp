#include "liberafx/LbCoordinateBasis.h"
#include "liberafx/LbSpawnableHost.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <algorithm>
#include <array>
#include <cassert>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <vector>

namespace {
using namespace liberafx;
void coordinates() {
    const auto original = std::array<double, 3>{1.25, -2.5, 4};
    auto permutation = std::array<int, 3>{1, 2, 3};
    do {
        for (auto signs = 0u; signs < 8; ++signs) {
            auto basis = CoordinateBasis{permutation, 100};
            for (auto i = 0u; i < 3; ++i) if (signs & (1u << i)) basis.axes[i] *= -1;
            for (const auto kind : {SpatialValueKind::Position, SpatialValueKind::Vector,
                SpatialValueKind::Direction, SpatialValueKind::Normal, SpatialValueKind::AngularVector}) {
                auto values = original;
                assert(convertCoordinates(basis, kind, true, values.data(), 3, values.data()) == Result::Ok);
                assert(convertCoordinates(basis, kind, false, values.data(), 3, values.data()) == Result::Ok);
                for (auto i = 0u; i < 3; ++i) assert(std::abs(values[i] - original[i]) < 1e-10);
            }
            auto matrix = std::array<double, 16>{2,0,0,1, 0,3,0,2, 0,0,4,3, 0,0,0,1};
            const auto saved = matrix;
            assert(convertCoordinates(basis, SpatialValueKind::Transform, true, matrix.data(), 16, matrix.data()) == Result::Ok);
            assert(convertCoordinates(basis, SpatialValueKind::Transform, false, matrix.data(), 16, matrix.data()) == Result::Ok);
            for (auto i = 0u; i < 16; ++i) assert(std::abs(matrix[i] - saved[i]) < 1e-10);
        }
    } while (std::next_permutation(permutation.begin(), permutation.end()));
    auto position = std::array<double, 3>{1,2,3};
    assert(convertCoordinates(CoordinateBasis::unreal(), SpatialValueKind::Position, true, position.data(), 3, position.data()) == Result::Ok);
    assert((position == std::array<double,3>{300,100,200})); // BS2: depth X, horizontal Y, up Z.
    auto direction = std::array<double,3>{0,1,0};
    assert(convertCoordinates(CoordinateBasis::unreal(), SpatialValueKind::Direction, true, direction.data(), 3, direction.data()) == Result::Ok);
    assert((direction == std::array<double,3>{0,0,1}));
    auto unchanged = position;
    assert(convertCoordinates({{1,1,3},1}, SpatialValueKind::Position, true, original.data(), 3, position.data()) == Result::InvalidArgument);
    assert(position == unchanged);
    assert(!validCoordinateBasis({{1,2,3}, std::numeric_limits<double>::infinity()}));
}

// A Time node followed by the five shipped host clock nodes. No external fixture
// or engine host is needed, so pause/rate changes are exercised in every Core CI.
auto clocks() -> SpawnablePackage {
    auto package = SpawnablePackage{};
    package.autoDestroyWhenIdle = false;
    auto& bytes = package.parentBytecode;
    bytes.resize(16 + 6 * 256);
    std::memcpy(bytes.data(), "LFXB", 4); bytes[4] = 5; bytes[6] = 1; bytes[8] = 6; bytes[13] = 1;
    for (auto index = 0u; index < 6; ++index) {
        auto* op = bytes.data() + 16 + index * 256;
        op[15] = static_cast<std::uint8_t>(index + 1);
        op[16] = index == 0 ? 2 : 40; op[17] = 1;
        if (index == 0) { std::memset(op + 24, 255, 8); continue; }
        const auto type = std::array<std::uint8_t,16>{0xc9,0x82,0xe1,0xbe,0x8f,0x78,0x4d,0x28,
            0x9a,0x65,0x4f,0x4a,0xa4,0x52,0x49,static_cast<std::uint8_t>(0xe5 + index)};
        std::copy(type.begin(), type.end(), op + 24); op[40] = 1;
        for (auto input = 0u; input < 4; ++input) std::memset(op + 48 + input * 8, 255, 4);
        std::memset(op + 208, 255, 16);
    }
    return package;
}
void timeScale() {
    auto host = std::unique_ptr<SpawnableHost>{};
    assert(SpawnableHost::create(clocks(), {}, host) == Result::Ok);
    assert(host->setTimeScale(.25) == Result::Ok);
    assert(host->advance(0,0) == Result::Ok);
    assert(host->advance(4,4) == Result::Ok);
    auto verify = [&host](std::array<double,6> expected) {
        for (auto i = 0u; i < 6; ++i) {
            auto id = std::array<std::uint8_t,16>{}; id[15] = static_cast<std::uint8_t>(i + 1);
            auto value = std::array<double,4>{}; auto width = std::uint8_t{};
            assert(host->rootValue(id.data(), value.data(), width) == Result::Ok && width == 1);
            assert(std::abs(value[0] - expected[i]) < 1e-9);
        }
    };
    verify({1,1,1,4,4,4});
    assert(host->setTimeScale(0) == Result::Ok);
    assert(host->advance(6,2) == Result::Ok);
    verify({1,1,0,6,6,2});
    assert(host->setTimeScale(2) == Result::Ok);
    assert(host->advance(7,1) == Result::Ok);
    verify({3,3,2,7,7,1});
    const auto clock = host->timeSnapshot();
    assert(clock.realTime == 7 && clock.effectTime == 3 && clock.timeScale == 2);
    assert(host->setTimeScale(-1) == Result::InvalidArgument);
    assert(host->advance(6,1) == Result::InvalidArgument);
    assert(host->timeSnapshot().realTime == 7);
}
}
int main() { coordinates(); timeScale(); }
