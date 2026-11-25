#include "liberafx/LbInterop.h"
#include "liberafx/LbExecutionDebugInterop.h"
#if defined(_WIN32)
#define NOMINMAX
#include <Windows.h>
#include <chrono>
#include <thread>
#endif
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <algorithm>
#include <array>
#include <cassert>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>
#if defined(_MSC_VER) && defined(_DEBUG)
#include <crtdbg.h>
#endif

// Package-host coverage retained from the retired standalone interop suite.
namespace {
#if defined(_WIN32)
static void verifyPackageDebugger(const std::filesystem::path& path)
{
    auto file = std::ifstream(path / "GameIo.lfxp", std::ios::binary);
    auto bytes = std::vector<std::uint8_t>{std::istreambuf_iterator<char>(file), {}};
    assert(!bytes.empty());
    auto hash = UINT64_C(14695981039346656037);
    for (auto b : bytes) hash = (hash ^ b) * UINT64_C(1099511628211);
    auto host = LibraSpawnableHostHandle{}, foreign = LibraSpawnableHostHandle{};
    for (auto* handle : {&host, &foreign})
        assert(libra_spawnable_host_create(bytes.data(), bytes.size(), 8, 4 * 1024 * 1024, 128, 10000, handle) == LIBRA_OK);
    assert(libra_spawnable_host_advance(host, 0, 0) == LIBRA_OK);
    assert(libra_spawnable_host_fire_game_signal(host, "spawn") == LIBRA_OK);
    assert(libra_spawnable_host_fire_game_signal(host, "spawn") == LIBRA_OK);
    auto spawned = std::vector<LibraSpawnableHostEvent>{};
    for (;;) {
        auto item = LibraSpawnableHostEvent{};
        auto has = uint32_t{};
        assert(libra_spawnable_host_next_event(host, &item, &has) == LIBRA_OK);
        if (!has) break;
        if (item.kind == 1) spawned.push_back(item);
    }
    assert(spawned.size() == 2 && spawned[0].instanceId != spawned[1].instanceId);
    auto generation = uint64_t{};
    assert(libra_spawnable_debug_generation(host, &generation) == LIBRA_OK && generation != 0);
    assert(libra_spawnable_debug_attach(foreign, generation, nullptr, 0, "") == LIBRA_INVALID_HANDLE);
    auto verify = [&](const LibraSpawnableHostEvent* child, double time) {
        auto invitation = std::string("a3b102031405060708090a0b");
        invitation.back() = child ? '2' : '1';
        auto processId = static_cast<std::uint32_t>(GetCurrentProcessId());
        for (auto shift = 28; shift >= 0; shift -= 4)
            invitation += "0123456789abcdef"[(processId >> shift) & 15];
        assert(invitation.size() == 32);
        auto pipe = CreateNamedPipeA(("\\\\.\\pipe\\LiberaFx.ExecutionDebug." + invitation).c_str(),
            PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 0, nullptr);
        assert(pipe != INVALID_HANDLE_VALUE);
        assert(libra_spawnable_debug_attach(host, generation, child ? child->containerId : nullptr,
            child ? child->instanceId : 0, invitation.c_str()) == LIBRA_OK);
        auto until = [](auto predicate) {
            auto end = std::chrono::steady_clock::now() + std::chrono::seconds(4);
            while (!predicate()) {
                assert(std::chrono::steady_clock::now() < end);
                std::this_thread::sleep_for(std::chrono::milliseconds(2));
            }
        };
        until([&] { return ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED; });
        auto exchange = [&](uint8_t op) {
            auto command = std::array<uint8_t, 32>{};
            auto frame = std::array<uint8_t, 984>{};
            std::memcpy(command.data(), "LFD2", 4);
            command[4] = op;
            for (auto i = 0u; i < 8; ++i) command[8 + i] = static_cast<uint8_t>(hash >> (8 * i));
            auto count = DWORD{};
            assert(WriteFile(pipe, command.data(), static_cast<DWORD>(command.size()), &count, nullptr) && count == command.size());
            until([&] { return PeekNamedPipe(pipe, nullptr, 0, nullptr, &count, nullptr) && count >= frame.size(); });
            assert(ReadFile(pipe, frame.data(), static_cast<DWORD>(frame.size()), &count, nullptr) && count == frame.size());
            return frame;
        };
        auto attached = exchange(1);
        assert(attached[5] == 0 && attached[952] == static_cast<uint8_t>(generation));
        assert(attached[960] == (child ? static_cast<uint8_t>(child->instanceId) : 0));
        if (child) assert(std::memcmp(attached.data() + 968, child->containerId, 16) == 0);
        assert(exchange(4)[5] == 0);
        assert(libra_spawnable_debug_submit(host, 0, time, .01, nullptr, 0, nullptr, nullptr) == LIBRA_OK);
        until([&] { return exchange(0)[4] == 2; });
        auto count = uint64_t{};
        assert(libra_spawnable_host_active_count(host, &count) == LIBRA_BUSY);
        assert(libra_spawnable_host_fire_game_signal(host, "spawn") == LIBRA_BUSY);
        assert(libra_spawnable_debug_submit(host, 0, time, .01, nullptr, 0, nullptr, nullptr) == LIBRA_BUSY);
        assert(exchange(5)[5] == 0);
        until([&] { return exchange(0)[4] == 2; });
        CloseHandle(pipe); // Lose the editor while the real worker is paused.
        auto work = static_cast<LibraResult>(LIBRA_NOT_READY);
        until([&] { return libra_spawnable_debug_poll(host, &work) == LIBRA_OK; });
        assert(work == LIBRA_OK);
        assert(libra_spawnable_debug_detach(host) == LIBRA_OK);
        assert(libra_spawnable_host_active_count(host, &count) == LIBRA_OK && count == 2);
    };
    verify(nullptr, .1);
    verify(&spawned[0], .2);
    assert(libra_spawnable_host_force_terminate(host, 1, spawned[0].containerId, spawned[0].instanceId) == LIBRA_OK);
    assert(libra_spawnable_debug_attach(host, generation, spawned[0].containerId, spawned[0].instanceId, "") == LIBRA_INVALID_HANDLE);
    assert(libra_spawnable_host_release(host) == LIBRA_OK && libra_spawnable_host_release(foreign) == LIBRA_OK);
}

#endif

void verifyCpuParticleReadFixture(const std::filesystem::path& directory)
{
    auto file=std::ifstream(directory / "CpuFountain-1000.lfxp",std::ios::binary);
    assert(file.good());
    const auto bytes=std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file),{});
    auto idFile=std::ifstream(directory / "CpuFountain-1000.id",std::ios::binary);
    auto id=std::array<std::uint8_t,16>{}, scope=std::array<std::uint8_t,16>{};
    idFile.read(reinterpret_cast<char*>(id.data()),id.size());
    assert(idFile.gcount()==16);
    LibraSpawnableHostHandle host=0;
    assert(libra_spawnable_host_create(bytes.data(),bytes.size(),8,4*1024*1024,128,10000,&host)==LIBRA_OK);
    assert(libra_spawnable_host_advance(host,0,0)==LIBRA_OK);
    assert(libra_spawnable_host_advance(host,1.0/60,1.0/60)==LIBRA_OK);
    std::uint32_t count=0;
    assert(libra_spawnable_host_cpu_particle_count(host,id.data(),&count)==LIBRA_OK && count==1000);
    std::array<LibraCpuParticleSnapshot,258> snapshots{};
    std::array<LibraCpuParticleTiming,258> timings{};
    snapshots.front().id=snapshots.back().id=UINT64_MAX;
    timings.front().age=timings.back().age=-123;
    for (auto first=0u;first<count;first+=256) {
        std::uint32_t written=999;
        assert(libra_spawnable_host_scoped_cpu_particle_read(host,scope.data(),0,id.data(),first,
            snapshots.data()+1,timings.data()+1,256,&written)==LIBRA_OK && written==std::min(256u,count-first));
        assert(snapshots.front().id==UINT64_MAX && snapshots.back().id==UINT64_MAX && timings.front().age==-123 && timings.back().age==-123);
        for (auto index=0u;index<written;++index) {
            auto expected=LibraCpuParticleSnapshot{};
            auto age=0.0, life=0.0;
            assert(libra_spawnable_host_cpu_particle_at(host,id.data(),first+index,&expected)==LIBRA_OK);
            assert(std::memcmp(&expected,&snapshots[index+1],sizeof(expected))==0);
            assert(libra_spawnable_host_scoped_cpu_particle_timing(host,scope.data(),0,id.data(),first+index,&age,&life)==LIBRA_OK);
            assert(age==timings[index+1].age && life==timings[index+1].life);
        }
    }
    std::uint32_t written=999;
    assert(libra_spawnable_host_scoped_cpu_particle_read(host,scope.data(),0,id.data(),count,snapshots.data(),nullptr,1,&written)==LIBRA_OK && written==0);
    assert(libra_spawnable_host_scoped_cpu_particle_read(host,scope.data(),0,id.data(),count+1,snapshots.data(),nullptr,1,&written)==LIBRA_INVALID_ARGUMENT && written==0);
    assert(libra_spawnable_host_release(host)==LIBRA_OK);
}


}
int main(int argc, char** argv)
{
#if defined(_MSC_VER) && defined(_DEBUG)
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
#endif
    if (argc != 3) {
        std::cout << "Supply --cpu or --debug and a compiler-produced fixture directory.\n";
        return 77;
    }
    const auto mode = std::string(argv[1]);
    if (mode == "--cpu") verifyCpuParticleReadFixture(argv[2]);
#if defined(_WIN32)
    else if (mode == "--debug") verifyPackageDebugger(argv[2]);
#endif
    else return 1;
    return 0;
}
