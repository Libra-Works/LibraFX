#include "liberafx/LbSpawnableHost.h"
#include "liberafx/LbEffectWorld.h"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <iterator>
#include <stdexcept>

namespace liberafx::particle_benchmarks {
using Clock = std::chrono::steady_clock;
double milliseconds(Clock::time_point start) {
    return std::chrono::duration<double, std::milli>(Clock::now() - start).count();
}
void require(Result result) {
    if (result != Result::Ok) throw std::runtime_error("Native operation failed: " + std::to_string(static_cast<int>(result)));
}
void report(const char* phase, std::vector<double> samples) {
    double sum = 0;
    for (auto sample : samples) sum += sample;
    std::sort(samples.begin(), samples.end());
    auto percentile = [&](double value) { return samples[static_cast<std::size_t>(std::ceil(value * samples.size())) - 1]; };
    std::cout << phase << ',' << sum / samples.size() << ',' << percentile(.5) << ','
              << percentile(.95) << ',' << percentile(.99) << ',' << samples.back() << '\n';
}
}

int main(int argc, char** argv) {
    using namespace liberafx;
    using namespace particle_benchmarks;
    try {
        if (argc < 4 || argc > 8) throw std::runtime_error("Usage: particle_host_benchmarks <CPU .lfxp> <instances 1..64> <frames 30..3600> [--interpreter] [--bulk] [--world-workers 1..16]");
        auto options = SpawnableHostOptions{};
        bool bulk=false;
        auto worldWorkers=std::size_t{};
        for (auto index=4;index<argc;++index) {
            if (std::string(argv[index])=="--interpreter") options.cpuParticleBatching=false;
            else if (std::string(argv[index])=="--bulk") bulk=true;
            else if (std::string(argv[index])=="--world-workers" && index+1<argc) {
                worldWorkers=std::stoul(argv[++index]);
                if (!worldWorkers || worldWorkers>16) throw std::runtime_error("Invalid world worker limit.");
            }
            else throw std::runtime_error("Unknown execution mode.");
        }
        const auto instances = std::stoi(argv[2]), frames = std::stoi(argv[3]);
        if (instances < 1 || instances > 64 || frames < 30 || frames > 3600) throw std::runtime_error("Invalid benchmark limits.");
#ifdef NDEBUG
        std::cout << "# configuration=Release\n";
#else
        throw std::runtime_error("Use a Release build for particle measurements.");
#endif
        auto stream = std::ifstream(argv[1], std::ios::binary);
        if (!stream) throw std::runtime_error("Could not read fixture.");
        const auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(stream), {});
        auto package = SpawnablePackage{};
        require(loadSpawnablePackage(bytes.data(), bytes.size(), package));
        if (package.cpuPrograms.size() < 24) throw std::runtime_error("Fixture must contain CPU particles.");
        std::array<std::uint8_t,16> system{};
        std::copy_n(package.cpuPrograms.data() + 8, 16, system.data());
        std::vector<std::unique_ptr<SpawnableHost>> hosts(instances);
        auto world=std::unique_ptr<EffectWorld>{};
        std::vector<EffectFrame> submitted(instances);
        const auto worldStart=Clock::now();
        if (worldWorkers) {
            auto worldOptions=EffectWorldOptions{}; worldOptions.workers=worldWorkers;
            worldOptions.maximumEffects=static_cast<std::size_t>(instances);
            require(EffectWorld::create(worldOptions,world));
        }
        const auto worldPreparation=milliseconds(worldStart);
        auto start = Clock::now();
        for (auto& host : hosts) require(SpawnableHost::create(package, options, host));
        const auto create = milliseconds(start);
        start = Clock::now();
        const auto advanceAll=[&](double time,double delta) {
            if (!world) { for (auto& host:hosts) require(host->advance(time,delta)); return; }
            for (auto index=0;index<instances;++index) submitted[index]={hosts[index].get(),time,delta};
            require(world->advance(submitted.data(),submitted.size()));
            for (const auto& frame:submitted) require(frame.result);
        };
        advanceAll(0,0);
        const auto spawn = milliseconds(start);
        std::size_t initialCount = 0;
        require(hosts.front()->cpuParticleCount(system.data(), initialCount));
        if (initialCount == 0) throw std::runtime_error("Fixture must emit an initial burst.");
        std::vector<double> simulation, extraction;
        double checksum = 0;
        std::array<CpuParticleSnapshot,256> copied;
        for (int frame = 1; frame <= frames + 30; ++frame) {
            start = Clock::now();
            advanceAll(frame / 60.0, 1.0 / 60);
            const auto step = milliseconds(start);
            start = Clock::now();
            for (auto& host : hosts) {
                std::size_t count = 0;
                require(host->cpuParticleCount(system.data(), count));
                if (count != initialCount) throw std::runtime_error("Particle count changed during the measurement.");
                for (std::size_t index = 0; index < count; ++index) {
                    CpuParticleSnapshot particle{};
                    if (bulk) {
                        if (index%copied.size()==0) {
                            std::size_t written=0;
                            require(host->cpuParticleRead({},system.data(),index,copied.data(),nullptr,copied.size(),written));
                            if (written!=std::min(copied.size(),count-index)) throw std::runtime_error("Incomplete bulk read.");
                        }
                        particle=copied[index%copied.size()];
                    } else require(host->cpuParticleAt(system.data(),index,particle));
                    checksum += particle.position[1] + particle.color[3];
                }
            }
            const auto read = milliseconds(start);
            if (frame > 30) { simulation.push_back(step); extraction.push_back(read); }
        }
        std::cout << std::fixed << std::setprecision(6)
                  << "# instances=" << instances << ",particles_per_instance=" << initialCount << ",measured_frames=" << frames
                  << ",warmup_frames=30,dt=1/60,no_drawing,no_Niagara_comparison\n"
                  << "# shared_world_workers=" << (world ? world->stats().workers : 0) << ",world_preparation_ms=" << worldPreparation << '\n'
                  << "# last_executing_lanes=" << (world ? world->stats().lastExecutingLanes : 1) << '\n'
                  << "# particle_batching=" << (options.cpuParticleBatching ? "auto" : "disabled") << '\n'
                  << "# snapshot_reads=" << (bulk ? "bulk256" : "individual") << '\n'
                  << "# create_all_ms=" << create << ",initial_spawn_all_ms=" << spawn << ",checksum=" << checksum << '\n'
                  << "phase,mean_ms,p50_ms,p95_ms,p99_ms,max_ms\n";
        report("simulation_all_instances", std::move(simulation));
        report("snapshot_reads_all_instances", std::move(extraction));
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
