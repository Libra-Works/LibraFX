#include "liberafx/LbInterop.h"
#include "liberafx/LbSpawnablePackage.h"
#include <algorithm>
#include <array>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <thread>
#include <vector>

namespace liberafx::interop_pool_tests {
void require(bool valid,const char* message) { if (!valid) throw std::runtime_error(message); }
void check(LibraResult result) { if (result!=LIBRA_OK) throw std::runtime_error("Unexpected pool ABI result: "+std::to_string(result)); }
void fixture(const char* path) {
    auto stream=std::ifstream(path,std::ios::binary); require(stream.good(),"Missing compiled pool ABI fixture.");
    const auto bytes=std::vector<std::uint8_t>(std::istreambuf_iterator<char>(stream),{});
    LibraEffectWorldHandle world=0;
    check(libra_effect_world_create(3,2,&world));
    auto wrongWorldOwner=LibraResult{LIBRA_OK};
    std::thread worldOther([&] { wrongWorldOwner=libra_effect_world_advance(world,nullptr,0); }); worldOther.join();
    require(wrongWorldOwner==LIBRA_BUSY,"ABI effect world ignored owner thread.");
    auto package=SpawnablePackage{};
    require(loadSpawnablePackage(bytes.data(),bytes.size(),package)==Result::Ok && package.cpuPrograms.size()>24,"Invalid CPU fixture.");
    std::array<std::uint8_t,16> id{}; std::copy_n(package.cpuPrograms.data()+8,16,id.data());
    auto options=LibraSpawnableHostPoolOptions{3,2,128,10000};
    LibraSpawnableHostPoolHandle pool=0; check(libra_spawnable_host_pool_create(bytes.data(),bytes.size(),&options,&pool));
    LibraSpawnableHostPoolStats stats{}; check(libra_spawnable_host_pool_stats(pool,&stats));
    require(stats.capacity==3 && stats.active==0 && stats.available==3 && stats.preparationWorkers<=2,"Invalid pool ABI stats.");
    std::array<LibraSpawnableHostHandle,3> held{};
    for (auto& handle:held) check(libra_spawnable_host_pool_acquire(pool,&handle));
    for (auto handle : held)
    {
        std::uint32_t flags = 0;
        check(libra_spawnable_host_execution_capabilities(handle, &flags));
        require(flags == 15, "Builtin prepared host lost execution capabilities.");
    }
    std::array<LibraEffectFrame,3> frames{};
    for (auto index=0u;index<held.size();++index) { frames[index]={held[index],0,0,LIBRA_NOT_READY,0}; }
    const auto saved=frames[1]; frames[1]=frames[0];
    require(libra_effect_world_advance(world,frames.data(),3)==LIBRA_INVALID_ARGUMENT,"ABI duplicate frame was not rejected.");
    frames[1]=saved;
    frames[2].reserved=1;
    require(libra_effect_world_advance(world,frames.data(),3)==LIBRA_INVALID_ARGUMENT,"ABI accepted reserved frame flags.");
    frames[2].reserved=0;
    for (auto step=0;step<30;++step) {
        for (auto& frame:frames) { frame.time=step/60.0; frame.delta=step==0 ? 0 : 1.0/60; }
        check(libra_effect_world_advance(world,frames.data(),3));
        for (const auto& frame:frames) { check(frame.result); }
    }
    LibraSpawnableHostHandle extra=123;
    require(libra_spawnable_host_pool_acquire(pool,&extra)==LIBRA_INSUFFICIENT_WORK && extra==0,"Full ABI pool grew.");
    require(libra_spawnable_host_pool_release(pool)==LIBRA_BUSY,"Pool freed checked-out hosts.");
    for (auto handle:held) check(libra_spawnable_host_release(handle));
    auto wrongOwner=LibraResult{LIBRA_OK};
    std::thread other([&] { LibraSpawnableHostHandle value=123; wrongOwner=libra_spawnable_host_pool_acquire(pool,&value); }); other.join();
    require(wrongOwner==LIBRA_BUSY,"ABI pool ignored the owner thread.");
    LibraSpawnableHostHandle stale=0;
    for (auto playback=0;playback<8;++playback) {
        LibraSpawnableHostHandle host=0,reference=0;
        check(libra_spawnable_host_pool_acquire(pool,&host));
        require(host!=stale,"Recycled ABI host reused a stale handle.");
        if (stale) require(libra_spawnable_host_advance(stale,0,0)==LIBRA_INVALID_HANDLE,"Old ABI handle revived.");
        check(libra_spawnable_host_create(bytes.data(),bytes.size(),32,4*1024*1024,128,10000,&reference));
        if (playback%2==0) {
            const double position[]={1,2,3};
            require(libra_spawnable_host_set_game_property(host,"position",position,3)==
                libra_spawnable_host_set_game_property(reference,"position",position,3),"Pooled ABI property setup changed.");
        }
        for (auto frame=0;frame<60;++frame) {
            const auto time=frame/60.0, delta=frame==0 ? 0.0 : 1.0/60;
            const auto actual=libra_spawnable_host_advance(host,time,delta), expected=libra_spawnable_host_advance(reference,time,delta);
            require(actual==expected,"Pooled ABI changed execution status."); if (actual!=LIBRA_OK) break;
            std::uint32_t a=0,b=0;
            check(libra_spawnable_host_cpu_particle_count(host,id.data(),&a));
            check(libra_spawnable_host_cpu_particle_count(reference,id.data(),&b));
            require(a==b,"Pooled ABI changed particle count.");
            for (auto i=0u;i<a;++i) {
                LibraCpuParticleSnapshot left{},right{};
                check(libra_spawnable_host_cpu_particle_at(host,id.data(),i,&left));
                check(libra_spawnable_host_cpu_particle_at(reference,id.data(),i,&right));
                require(std::memcmp(&left,&right,sizeof(left))==0,"Pooled ABI changed particle state.");
            }
        }
        if (playback%2==0) check(libra_spawnable_host_force_terminate(host,0,nullptr,0));
        check(libra_spawnable_host_release(host)); check(libra_spawnable_host_release(reference)); stale=host;
        require(libra_spawnable_host_release(host)==LIBRA_INVALID_HANDLE,"ABI double return succeeded.");
    }
    check(libra_spawnable_host_pool_stats(pool,&stats)); require(stats.available==3 && stats.active==0,"ABI pool leaked a slot.");
    check(libra_spawnable_host_pool_release(pool));
    require(libra_effect_world_advance(world,frames.data(),3)==LIBRA_INVALID_HANDLE,"ABI reused stale frame handles.");
    check(libra_effect_world_release(world));
    require(libra_effect_world_release(world)==LIBRA_INVALID_HANDLE,"ABI double world release succeeded.");
    require(libra_spawnable_host_pool_release(pool)==LIBRA_INVALID_HANDLE,"ABI pool double release succeeded.");
}
}
int main(int argc,char** argv) {
    try {
        std::uint32_t flags = 123;
        if (libra_spawnable_host_execution_capabilities(0, &flags) != LIBRA_INVALID_HANDLE || flags != 0)
            throw std::runtime_error("Invalid capabilities handle retained flags.");
        if (libra_spawnable_host_execution_capabilities(0, nullptr) != LIBRA_INVALID_ARGUMENT)
            throw std::runtime_error("Null capabilities output was accepted.");
        LibraSpawnableHostPoolHandle pool=123;
        if (libra_spawnable_host_pool_create(nullptr,0,nullptr,&pool)!=LIBRA_INVALID_ARGUMENT || pool!=0)
            throw std::runtime_error("Invalid pool input did not clear its output.");
        LibraSpawnableHostHandle host=123;
        if (libra_spawnable_host_pool_acquire(0,&host)!=LIBRA_INVALID_HANDLE || host!=0)
            throw std::runtime_error("Invalid pool handle did not clear its output.");
        for (auto index=1;index<argc;++index) liberafx::interop_pool_tests::fixture(argv[index]);
        std::cout<<"Pool C ABI capacity, owner thread, stale handles and fresh/reused snapshots agree.\n";
        return 0;
    } catch (const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
}
