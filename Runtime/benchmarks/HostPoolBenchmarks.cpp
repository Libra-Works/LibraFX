#include "liberafx/LbSpawnableHostPool.h"
#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <iterator>
#include <stdexcept>

namespace liberafx::host_pool_benchmark {
using Clock=std::chrono::steady_clock;
double ms(Clock::time_point start) { return std::chrono::duration<double,std::milli>(Clock::now()-start).count(); }
void check(Result status) { if (status!=Result::Ok) throw std::runtime_error("Native result: "+std::to_string(static_cast<int>(status))); }
void report(const char* phase,std::vector<double> samples,std::size_t capacity) {
    auto sum=0.0; for (auto value:samples) sum+=value;
    std::sort(samples.begin(),samples.end());
    std::cout<<phase<<','<<sum/samples.size()<<','<<samples[samples.size()/2]<<','
        <<samples[static_cast<std::size_t>(std::ceil(.95*samples.size()))-1]<<','<<samples.back()<<','<<sum/samples.size()/capacity<<'\n';
}
}
int main(int argc,char** argv) {
    using namespace liberafx; using namespace host_pool_benchmark;
    try {
#ifndef NDEBUG
        throw std::runtime_error("Use a Release build.");
#endif
        if (argc!=5 && argc!=6) throw std::runtime_error("Usage: host_pool_benchmarks <CPU .lfxp> <capacity> <workers 1..16> <cycles 30..3600> [--fresh]");
        const auto fresh=argc==6 && std::string(argv[5])=="--fresh";
        if (argc==6 && !fresh) throw std::runtime_error("Unknown mode.");
        auto options=SpawnableHostPoolOptions{};
        options.capacity=static_cast<std::size_t>(std::stoul(argv[2]));
        options.preparationWorkers=static_cast<std::size_t>(std::stoul(argv[3]));
        const auto cycles=std::stoi(argv[4]);
        if (cycles<30 || cycles>3600) throw std::runtime_error("Invalid cycle count.");
        auto stream=std::ifstream(argv[1],std::ios::binary); if (!stream) throw std::runtime_error("Missing CPU fixture.");
        const auto bytes=std::vector<std::uint8_t>(std::istreambuf_iterator<char>(stream),{});
        auto package=SpawnablePackage{}; check(loadSpawnablePackage(bytes.data(),bytes.size(),package));
        if (package.cpuPrograms.size()<24) throw std::runtime_error("Expected CPU particles.");
        std::array<std::uint8_t,16> id{}; std::copy_n(package.cpuPrograms.data()+8,16,id.data());
        auto pool=std::unique_ptr<SpawnableHostPool>{};
        auto start=Clock::now(); if (!fresh) check(SpawnableHostPool::create(package,options,pool)); const auto preparation=ms(start);
        std::vector<SpawnableHostLease> leases(options.capacity);
        std::vector<SpawnableHost*> hosts(options.capacity);
        std::vector<std::unique_ptr<SpawnableHost>> freshHosts(options.capacity);
        std::vector<double> acquire,spawn,reset;
        acquire.reserve(cycles); spawn.reserve(cycles); reset.reserve(cycles);
        std::size_t particleCount=0; auto checksum=0.0;
        for (auto cycle=0;cycle<cycles+30;++cycle) {
            start=Clock::now();
            for (auto index=0u;index<options.capacity;++index) {
                if (fresh) { check(SpawnableHost::create(package,options.host,freshHosts[index])); hosts[index]=freshHosts[index].get(); }
                else check(pool->acquire(leases[index],hosts[index]));
            }
            const auto acquired=ms(start);
            start=Clock::now();
            for (auto* host:hosts) check(host->advance(0,0));
            const auto spawned=ms(start);
            for (auto* host:hosts) {
                std::size_t count=0; check(host->cpuParticleCount(id.data(),count));
                if (!count || (particleCount && count!=particleCount)) throw std::runtime_error("Reuse changed emission count.");
                particleCount=count;
                CpuParticleSnapshot snapshot{}; check(host->cpuParticleAt(id.data(),0,snapshot));
                if (snapshot.id!=1) throw std::runtime_error("Reuse leaked IDs.");
                checksum+=snapshot.color[3]+snapshot.position[1];
                check(host->advance(1.0/60,1.0/60));
            }
            start=Clock::now();
            if (fresh) for (auto& host:freshHosts) host.reset();
            else for (const auto& lease:leases) check(pool->release(lease));
            const auto released=ms(start);
            if (cycle>=30) { acquire.push_back(acquired); spawn.push_back(spawned); reset.push_back(released); }
        }
        std::cout<<std::fixed<<std::setprecision(6)<<"# capacity="<<options.capacity<<",particles_per_host="<<particleCount
            <<",preparation_workers="<<(fresh ? 0 : pool->stats().preparationWorkers)<<",prepare_all_ms="<<preparation<<",checksum="<<checksum
            <<",cycles="<<cycles<<",warmup=30,no_drawing,no_package_loading\nphase,mean_all_ms,p50_all_ms,p95_all_ms,max_all_ms,mean_per_host_ms\n";
        report(fresh ? "fresh_create" : "acquire",std::move(acquire),options.capacity);
        report("initial_emission",std::move(spawn),options.capacity);
        report(fresh ? "fresh_destroy" : "reset_and_return",std::move(reset),options.capacity);
        return 0;
    } catch (const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
}
