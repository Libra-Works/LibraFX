#include "liberafx/LbSpawnableHost.h"
#include "../Core/CpuParticles/LbCpuParticlesInternal.h"
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>

namespace liberafx::cpu_particle_batch_tests {
void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
void check(Result result) { require(result == Result::Ok, "Unexpected native result."); }
void compare(const char* path, bool expectBatch, bool expectError = false, Result expectedFailure = Result::DivideByZero,
    int errorFrame = 1, bool expectSpawn = true, int expectFusion = -1, const char* referencePath = nullptr, bool automaticBatching = true)
{
    auto stream = std::ifstream(path, std::ios::binary);
    require(stream.good(), "Could not read compiled CPU fixture.");
    const auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(stream), {});
    auto package = SpawnablePackage{};
    check(loadSpawnablePackage(bytes.data(),bytes.size(),package));
    auto referencePackage=SpawnablePackage{};
    if (referencePath) {
        auto referenceStream=std::ifstream(referencePath,std::ios::binary);
        require(referenceStream.good(),"Could not read the unoptimized reference package.");
        const auto original=std::vector<std::uint8_t>(std::istreambuf_iterator<char>(referenceStream),{});
        check(loadSpawnablePackage(original.data(),original.size(),referencePackage));
        require(package.cpuPrograms.size()<referencePackage.cpuPrograms.size(),"Ahead-of-time compilation did not shrink the CPU binary.");
    }
    std::vector<std::unique_ptr<cpu_particle_detail::System>> systems;
    check(cpu_particle_detail::parse(package.cpuPrograms.data(), package.cpuPrograms.size(),systems));
    require(!systems.empty(), "No CPU systems in the fixture.");
    for (auto& system : systems) {
        check(system->initialize(nullptr,0,true));
        require(system->updateBatch.enabled == expectBatch, "The intended batch/fallback execution path was not selected.");
        require(system->spawnBatch.enabled == (expectBatch && expectSpawn), "The intended birth execution path was not selected.");
        if (expectBatch && expectSpawn) require(!system->instances && system->sharedSpawn && system->sharedUpdate,
            "Isolated stages retained per-particle VM storage.");
        if (expectFusion>=0) {
            const auto fused=std::count_if(system->updateBatch.values.begin(),system->updateBatch.values.end(),
                [](const auto& op) { return op.op==cpu_particle_detail::BatchOp::LifetimeColor; });
            if (fused!=expectFusion) throw std::runtime_error(std::string(path)+": expected "+std::to_string(expectFusion)+
                " fused lifetime operations, got "+std::to_string(fused));
        }
    }
    auto automatic = std::unique_ptr<SpawnableHost>{}, reference = std::unique_ptr<SpawnableHost>{};
    auto options = SpawnableHostOptions{};
    options.cpuParticleBatching=automaticBatching;
    check(SpawnableHost::create(package,options,automatic));
    options.cpuParticleBatching = false;
    check(SpawnableHost::create(referencePath ? referencePackage : package,options,reference));
    auto time = 0.0;
    for (auto frame=0;frame<300;++frame) {
        if (frame==40 || frame==60 || frame==90) {
            const auto scale = frame==40 ? 0.0 : frame==60 ? .25 : 1.0;
            check(automatic->setTimeScale(scale)); check(reference->setTimeScale(scale));
        }
        const auto delta = frame==0 ? 0.0 : expectError ? .25 : frame%7==0 ? .1 : frame%3==0 ? 1.0/120 : 1.0/60;
        time += delta;
        const auto actual = automatic->advance(time,delta), expected = reference->advance(time,delta);
        require(actual == expected, "Batch and interpreter returned different status codes.");
        if (expectError && frame == errorFrame) {
            require(actual == expectedFailure, "Batched evaluation did not preserve the first failure in particle order.");
            return;
        }
        check(actual);
        for (const auto& system : systems) {
            auto count = std::size_t{}, otherCount = std::size_t{};
            check(automatic->cpuParticleCount(system->id.data(),count));
            check(reference->cpuParticleCount(system->id.data(),otherCount));
            require(count==otherCount, "Batch changed particle emission or retirement.");
            std::array<CpuParticleSnapshot,39> copied{};
            std::array<CpuParticleTiming,39> clocks{};
            copied.front().id=copied.back().id=UINT64_MAX;
            clocks.front().age=clocks.back().age=-123;
            for (auto index=0u;index<count;++index) {
                if (index%37==0) {
                    std::size_t written=999;
                    check(automatic->cpuParticleRead({},system->id.data(),index,copied.data()+1,clocks.data()+1,37,written));
                    require(written==std::min(std::size_t{37},count-index),"Bulk read returned the wrong range length.");
                    require(copied.front().id==UINT64_MAX && copied.back().id==UINT64_MAX &&
                        clocks.front().age==-123 && clocks.back().age==-123,"Bulk read overran caller buffers.");
                }
                auto a = CpuParticleSnapshot{}, b = CpuParticleSnapshot{};
                check(automatic->cpuParticleAt(system->id.data(),index,a));
                check(reference->cpuParticleAt(system->id.data(),index,b));
                require(std::memcmp(&a,&b,sizeof(a))==0, "Batch changed full particle state or stable ordering.");
                require(std::memcmp(&a,&copied[1+index%37],sizeof(a))==0,"Bulk read changed snapshot values or ordering.");
                auto ta = CpuParticleTiming{}, tb = CpuParticleTiming{};
                check(automatic->cpuParticleTiming({},system->id.data(),index,ta));
                check(reference->cpuParticleTiming({},system->id.data(),index,tb));
                require(ta.age==tb.age && ta.life==tb.life, "Batch changed particle age/lifetime.");
                require(ta.age==clocks[1+index%37].age && ta.life==clocks[1+index%37].life,"Bulk read changed animation timing.");
            }
            std::size_t written=999;
            check(automatic->cpuParticleRead({},system->id.data(),count,copied.data(),nullptr,1,written));
            require(written==0,"Bulk read at the end must be empty.");
            require(automatic->cpuParticleRead({},system->id.data(),count+1,copied.data(),nullptr,1,written)==Result::InvalidArgument && written==0,
                "Bulk read must reject an index past the live range.");
        }
        auto a = CpuParticleEvent{}, b = CpuParticleEvent{};
        for (;;) {
            const auto hasA=automatic->popCpuParticleEvent(a), hasB=reference->popCpuParticleEvent(b);
            require(hasA==hasB, "Batch changed event count.");
            if (!hasA) break;
            require(std::memcmp(&a,&b,sizeof(a))==0, "Batch changed event payload or ordering.");
        }
    }
    check(automatic->stop()); check(reference->stop());
    for (const auto& system : systems) {
        auto count=std::size_t{};
        check(automatic->cpuParticleCount(system->id.data(),count));
        require(count==0,"Stop retained batched particles.");
    }
}
}
int main(int argc, char** argv)
{
    if (argc < 4) { std::cout << "Supply batch, emission/retirement and fallback fixtures (last).\n"; return 77; }
    try {
        for (auto index=1;index<argc-1;++index) {
            const auto mode=std::string(argv[index]);
            if (mode == "--aot") {
                if (index+2 >= argc-1) throw std::runtime_error("Supply original and preoptimized packages.");
                const auto* original=argv[++index];
                const auto* optimized=argv[++index];
                liberafx::cpu_particle_batch_tests::compare(optimized,true,false,liberafx::Result::DivideByZero,1,true,1,original);
                liberafx::cpu_particle_batch_tests::compare(optimized,true,false,liberafx::Result::DivideByZero,1,true,1,original,false);
            } else if (mode == "--fused" || mode == "--unfused") {
                if (++index >= argc-1) throw std::runtime_error("Supply the lifetime-color fixture.");
                liberafx::cpu_particle_batch_tests::compare(argv[index],true,false,liberafx::Result::DivideByZero,1,true,
                    mode=="--fused" ? 1 : 0);
            } else if (mode == "--spawn-fallback") {
                if (++index >= argc-1) throw std::runtime_error("Supply the unsupported Spawn fixture.");
                liberafx::cpu_particle_batch_tests::compare(argv[index],true,false,liberafx::Result::DivideByZero,1,false);
            } else if (mode == "--birth-divide" || mode == "--birth-mixed") {
                if (++index >= argc-1) throw std::runtime_error("Supply the birth failure fixture.");
                liberafx::cpu_particle_batch_tests::compare(argv[index],true,true,
                    mode=="--birth-mixed" ? liberafx::Result::InvalidArgument : liberafx::Result::DivideByZero,0);
            } else if (mode == "--divide-zero" || mode == "--mixed-error") {
                if (++index >= argc-1) throw std::runtime_error("Supply the division-by-zero fixture.");
                liberafx::cpu_particle_batch_tests::compare(argv[index],true,true,
                    mode=="--mixed-error" ? liberafx::Result::InvalidArgument : liberafx::Result::DivideByZero);
            } else liberafx::cpu_particle_batch_tests::compare(argv[index],true);
        }
        liberafx::cpu_particle_batch_tests::compare(argv[argc-1],false);
        std::cout << "Batch/interpreter full state, timing, stable IDs, events, pause/slow, emission/retirement and Stop agree.\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
