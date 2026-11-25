#include "ProgramFixture.h"
#include "liberafx/LbSpawnableHostPool.h"
#include "liberafx/LbEffectWorld.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <new>
#include <stdexcept>
#include <thread>
#ifdef _WIN32
#include <malloc.h>
#endif

namespace allocation_check {
std::atomic<bool> enabled{false};
std::atomic<std::size_t> count{0};
void note() noexcept { if (enabled.load(std::memory_order_relaxed)) count.fetch_add(1,std::memory_order_relaxed); }
}
void* operator new(std::size_t bytes) {
    allocation_check::note(); if (auto* pointer=std::malloc(bytes ? bytes : 1)) return pointer;
    throw std::bad_alloc{};
}
void* operator new[](std::size_t bytes) { return ::operator new(bytes); }
void operator delete(void* pointer) noexcept { std::free(pointer); }
void operator delete[](void* pointer) noexcept { std::free(pointer); }
void operator delete(void* pointer,std::size_t) noexcept { std::free(pointer); }
void operator delete[](void* pointer,std::size_t) noexcept { std::free(pointer); }
void* operator new(std::size_t bytes,std::align_val_t alignment) {
    allocation_check::note();
#ifdef _WIN32
    auto* pointer=_aligned_malloc(bytes ? bytes : 1,static_cast<std::size_t>(alignment));
#else
    auto* pointer=static_cast<void*>(nullptr);
    if (posix_memalign(&pointer,static_cast<std::size_t>(alignment),bytes ? bytes : 1)!=0) pointer=nullptr;
#endif
    if (pointer) return pointer; throw std::bad_alloc{};
}
void* operator new[](std::size_t bytes,std::align_val_t alignment) { return ::operator new(bytes,alignment); }
void operator delete(void* pointer,std::align_val_t) noexcept {
#ifdef _WIN32
    _aligned_free(pointer);
#else
    std::free(pointer);
#endif
}
void operator delete[](void* pointer,std::align_val_t alignment) noexcept { ::operator delete(pointer,alignment); }
void operator delete(void* pointer,std::size_t,std::align_val_t alignment) noexcept { ::operator delete(pointer,alignment); }
void operator delete[](void* pointer,std::size_t,std::align_val_t alignment) noexcept { ::operator delete(pointer,alignment); }

namespace liberafx::pool_tests {
void require(bool valid,const char* message) { if (!valid) throw std::runtime_error(message); }
void check(Result result,const char* where="native") { if (result!=Result::Ok) throw std::runtime_error(std::string(where)+": "+std::to_string(static_cast<int>(result))); }
SpawnablePackage load(const char* path) {
    auto stream=std::ifstream(path,std::ios::binary); require(stream.good(),"Missing CPU pool fixture.");
    const auto bytes=std::vector<std::uint8_t>(std::istreambuf_iterator<char>(stream),{});
    auto package=SpawnablePackage{}; check(loadSpawnablePackage(bytes.data(),bytes.size(),package)); return package;
}
SpawnablePackage basic() {
    auto package=SpawnablePackage{};
    package.parentBytecode=std::vector<std::uint8_t>{'L','F','X','B',2,0,1,0,0,0,0,0,96,0,0,0};
    package.parentBytecode[8]=1;
    package.parentBytecode.resize(16+96);
    package.parentBytecode[16+15]=1; package.parentBytecode[16+16]=1; package.parentBytecode[16+17]=1;
    std::fill_n(package.parentBytecode.data()+16+24,8,0xff);
    package.parentBytecode = runtime_test_fixture::promoteSimpleProgram(package.parentBytecode);
    return package;
}
void instanceCleanup() {
    struct Counts { unsigned initialized=0,cleaned=0; Instance* instance=nullptr; bool busy=false; } counts;
    auto descriptor=CustomNodeDescriptor{};
    descriptor.typeId[15]=0xf1; descriptor.abiVersion=1; descriptor.outputWidth=1;
    descriptor.stateBytes=8; descriptor.context=&counts;
    descriptor.initialize=[](void* state,std::size_t,void* context) noexcept -> Result {
        ++static_cast<Counts*>(context)->initialized; *static_cast<double*>(state)=7; return Result::Ok;
    };
    descriptor.cleanup=[](void*,std::size_t,void* context) noexcept { ++static_cast<Counts*>(context)->cleaned; };
    descriptor.function=[](CustomNodeInvocation& call) noexcept -> Result {
        auto& value=*static_cast<Counts*>(call.context);
        value.busy=value.instance->deinitialize()==Result::Busy;
        call.outputs[0]=*static_cast<double*>(call.state); return value.busy ? Result::Ok : Result::InvalidArgument;
    };
    auto bytes=std::vector<std::uint8_t>(16+256);
    std::copy_n("LFXB",4,bytes.data()); bytes[4]=3; bytes[6]=1; bytes[8]=1; bytes[13]=1;
    auto* record=bytes.data()+16; record[15]=1; record[16]=40; record[17]=1;
    std::copy_n(descriptor.typeId,16,record+24); record[40]=1;
    for (auto slot=0u;slot<4;++slot) { std::fill_n(record+48+slot*8,4,0xff); std::fill_n(record+208+slot*4,4,0xff); }
    bytes = runtime_test_fixture::promoteSimpleProgram(bytes);
    record = bytes.data() + 16;
    auto binding=CustomNodeBinding{}; auto program=Program{};
    check(Program::load(bytes.data(),bytes.size(),&descriptor,1,&binding,1,program));
    alignas(16) std::array<double,128> work{}; auto instance=Instance{}; counts.instance=&instance;
    check(instance.initialize(program,work.data(),work.size()));
    auto handle=ValueHandle{}; check(instance.bindValue(record,handle));
    check(instance.updateAsync(0,[](void*,Job run,void* context) noexcept { run(context); return true; },nullptr));
    check(instance.result()); require(counts.busy,"Deinitialization mutated a running Instance.");
    check(instance.deinitialize()); check(instance.deinitialize());
    require(counts.initialized==1 && counts.cleaned==1,"Deinitialization repeated or skipped cleanup.");
    check(instance.initialize(program,work.data(),work.size()));
    double value[4]{}; require(instance.value(handle,value)==Result::InvalidHandle,"Deinitialization revived an old value handle.");
    check(instance.deinitialize()); require(counts.initialized==2 && counts.cleaned==2,"Reinitialized state did not clean up exactly once.");
}
struct Reentrant final { SpawnableHostPool* pool; SpawnableHostLease lease; bool observed=false; };
Result beforeNode(void* context,const DebugNodeVisit&) noexcept {
    auto& state=*static_cast<Reentrant*>(context);
    state.observed=state.pool->release(state.lease)==Result::Busy;
    return state.observed ? Result::Ok : Result::InvalidArgument;
}
void contracts() {
    auto pool=std::unique_ptr<SpawnableHostPool>{}, other=std::unique_ptr<SpawnableHostPool>{};
    auto options=SpawnableHostPoolOptions{}; options.capacity=2;
    check(SpawnableHostPool::create(basic(),options,pool));
    check(SpawnableHostPool::create(basic(),options,other));
    auto a=SpawnableHostLease{}, b=SpawnableHostLease{}, c=SpawnableHostLease{}, foreign=SpawnableHostLease{};
    SpawnableHost *first=nullptr,*second=nullptr,*unused=nullptr,*foreignHost=nullptr;
    check(pool->acquire(a,first)); check(pool->acquire(b,second)); check(other->acquire(foreign,foreignHost));
    require(first!=second,"Checked-out slots alias.");
    require(pool->release(foreign)==Result::InvalidHandle,"A foreign lease released a local instance.");
    const auto before=allocation_check::count.load(); allocation_check::enabled=true;
    const auto full=pool->acquire(c,unused); allocation_check::enabled=false;
    require(full==Result::InsufficientWork && !unused && !c.generation,"A full pool grew or returned a host.");
    require(allocation_check::count.load()==before,"Exhaustion allocated memory.");
    auto crossThread=Result::Ok;
    std::thread wrongOwner([&] { crossThread=pool->lookup(a,unused); }); wrongOwner.join();
    require(crossThread==Result::Busy,"Pool calls ignored the owner thread.");
    const auto oldDebug=first->debugGeneration();
    auto reentrant=Reentrant{pool.get(),a};
    auto target=SpawnableDebugTarget{}; check(first->debugTarget({},target));
    check(first->setDebugHooks(target,{&reentrant,beforeNode}));
    check(first->setTimeScale(.25)); check(first->advance(0,0));
    require(reentrant.observed,"Reentrant pool release did not preserve the active lease."); check(first->advance(1,1));
    check(pool->release(a));
    require(pool->lookup(a,unused)==Result::InvalidHandle && !unused,"Released lease is still valid.");
    require(pool->release(a)==Result::InvalidHandle,"Double release succeeded.");
    check(pool->acquire(c,unused));
    require(unused==first && c.generation!=a.generation && unused->debugGeneration()!=oldDebug,"Reuse did not invalidate old identities.");
    require(unused->setDebugHooks(target,{})==Result::InvalidHandle,"An old debug target accessed a new lease.");
    require(unused->timeSnapshot().timeScale==1 && unused->timeSnapshot().realTime==0,"Reuse leaked clocks/time scale.");
    check(pool->release(c)); check(pool->release(b)); check(other->release(foreign));
    require(pool->stats().available==2 && pool->stats().active==0,"Pool accounting changed after reuse.");
    options.capacity=0; require(SpawnableHostPool::create(basic(),options,other)==Result::InvalidArgument,"Invalid capacity succeeded.");
    auto nested=basic(); nested.templates.emplace_back(); options.capacity=1;
    require(SpawnableHostPool::create(nested, options, other) != Result::Ok, "Malformed template was silently pooled.");
}
void compare(SpawnableHost& actual,SpawnableHost& expected,const std::array<std::uint8_t,16>& system) {
    std::size_t a=0,b=0; check(actual.cpuParticleCount(system.data(),a)); check(expected.cpuParticleCount(system.data(),b));
    require(a==b,"Recycled host changed particle count.");
    for (auto index=0u;index<a;++index) {
        auto left=CpuParticleSnapshot{},right=CpuParticleSnapshot{};
        auto lt=CpuParticleTiming{},rt=CpuParticleTiming{};
        check(actual.cpuParticleAt(system.data(),index,left)); check(expected.cpuParticleAt(system.data(),index,right));
        check(actual.cpuParticleTiming({},system.data(),index,lt)); check(expected.cpuParticleTiming({},system.data(),index,rt));
        require(std::memcmp(&left,&right,sizeof(left))==0 && lt.age==rt.age && lt.life==rt.life,"Recycled host differs from fresh state/timing.");
    }
    auto le=CpuParticleEvent{},re=CpuParticleEvent{};
    for (;;) { const auto l=actual.popCpuParticleEvent(le),r=expected.popCpuParticleEvent(re);
        require(l==r,"Recycled host changed CPU event count."); if (!l) break;
        require(std::memcmp(&le,&re,sizeof(le))==0,"Recycled host changed CPU event data."); }
    auto lg=NamedIoEvent{},rg=NamedIoEvent{};
    for (;;) { const auto l=actual.popGameIoEvent(lg),r=expected.popGameIoEvent(rg);
        require(l==r,"Recycled host changed named IO event count."); if (!l) break;
        require(lg.portIndex==rg.portIndex && lg.kind==rg.kind && lg.width==rg.width && lg.isColor==rg.isColor &&
            std::equal(std::begin(lg.values),std::end(lg.values),std::begin(rg.values)),"Recycled host changed named IO values/signals."); }
    auto lh=SpawnableHostEvent{},rh=SpawnableHostEvent{};
    for (;;) { const auto l=actual.popEvent(lh),r=expected.popEvent(rh);
        require(l==r,"Recycled host changed lifecycle event count."); if (!l) break;
        require(std::memcmp(&lh,&rh,sizeof(lh))==0,"Recycled host changed lifecycle event data."); }
}
void fixture(const char* path,bool batching) {
    const auto package=load(path); require(package.cpuPrograms.size()>24,"Expected a CPU fixture.");
    auto id=std::array<std::uint8_t,16>{}; std::copy_n(package.cpuPrograms.data()+8,16,id.data());
    auto options=SpawnableHostPoolOptions{}; options.capacity=4; options.host.cpuParticleBatching=batching;
    auto pool=std::unique_ptr<SpawnableHostPool>{}; check(SpawnableHostPool::create(package,options,pool),"pool create");
    std::array<SpawnableHostLease,4> preparedLeases{};
    std::array<SpawnableHost*,4> preparedHosts{};
    for (auto i=0u;i<preparedHosts.size();++i) check(pool->acquire(preparedLeases[i],preparedHosts[i]));
    for (auto i=0u;i<preparedHosts.size();++i) {
        auto reference=std::unique_ptr<SpawnableHost>{}; check(SpawnableHost::create(package,options.host,reference));
        if (!package.gameIoPorts.empty()) {
            const double position[]={static_cast<double>(i),2,3};
            check(preparedHosts[i]->setGameProperty("position",position,3)); check(reference->setGameProperty("position",position,3));
        }
        check(preparedHosts[i]->advance(0,0)); check(reference->advance(0,0));
        compare(*preparedHosts[i],*reference,id);
    }
    for (const auto& lease:preparedLeases) check(pool->release(lease));
    for (auto playback=0;playback<8;++playback) {
        auto reference=std::unique_ptr<SpawnableHost>{}; check(SpawnableHost::create(package,options.host,reference),"reference create");
        auto lease=SpawnableHostLease{}; SpawnableHost* host=nullptr;
        const auto before=allocation_check::count.load(); allocation_check::enabled=true;
        const auto acquired=pool->acquire(lease,host); allocation_check::enabled=false; check(acquired);
        require(allocation_check::count.load()==before,"Acquire allocated memory.");
        if (playback%2==0) {
            check(host->setCoordinateBasis({{2,3,1},100})); check(reference->setCoordinateBasis({{2,3,1},100}));
            if (!package.gameIoPorts.empty()) {
                const double position[]={10,20,30};
                check(host->setGameProperty("position",position,3)); check(reference->setGameProperty("position",position,3));
                const double matrix[]={1,0,0,10,0,1,0,20,0,0,1,30,0,0,0,1};
                check(host->setOwnerMatrix(matrix,16)); check(reference->setOwnerMatrix(matrix,16));
            }
        }
        auto time=0.0;
        auto failed=false;
        for (auto frame=0;frame<100;++frame) {
            if (frame==20 || frame==30) { const auto scale=frame==20 ? .25 : 1.0; check(host->setTimeScale(scale)); check(reference->setTimeScale(scale)); }
            const auto delta=frame==0 ? 0.0 : frame%7==0 ? .1 : 1.0/60; time+=delta;
            const auto actual=host->advance(time,delta), expected=reference->advance(time,delta);
            require(actual==expected,"Fresh/recycled hosts returned different errors.");
            if (actual!=Result::Ok) { failed=true; break; }
            if (frame==10 && !package.gameIoPorts.empty()) { check(host->fireGameSignal("trigger")); check(reference->fireGameSignal("trigger")); }
            compare(*host,*reference,id);
            for (const auto& port:package.gameIoPorts) if (port.kind==static_cast<std::uint8_t>(NamedIoKind::OwnerMatrix)) {
                require(port.runtimeNodeIds.has_value(),"Owner Matrix fixture lost runtime rows.");
                for (const auto& row:*port.runtimeNodeIds) {
                    double left[4]{},right[4]{}; std::uint8_t lw=0,rw=0;
                    check(host->rootValue(row.data(),left,lw)); check(reference->rootValue(row.data(),right,rw));
                    require(lw==rw && std::equal(std::begin(left),std::end(left),std::begin(right)),"Recycled Owner Matrix leaked prior inputs.");
                }
            }
        }
        if (!failed && playback%3==0) check(host->stop(),"stop");
        else if (!failed && playback%3==1) check(host->forceTerminate({}),"force terminate");
        const auto allocated=allocation_check::count.load(); allocation_check::enabled=true;
        const auto released=pool->release(lease); allocation_check::enabled=false; check(released,"pool release");
        require(allocation_check::count.load()==allocated,"Release/reset allocated memory.");
    }
}
}
static void sharedAllocationCheck(const char* path)
{
    using namespace liberafx;
    using namespace liberafx::pool_tests;
    auto stream=std::ifstream(path,std::ios::binary);
    const auto bytes=std::vector<std::uint8_t>(std::istreambuf_iterator<char>(stream),{});
    auto package=SpawnablePackage{}; check(loadSpawnablePackage(bytes.data(),bytes.size(),package));
    auto pool=std::unique_ptr<SpawnableHostPool>{}; auto options=SpawnableHostPoolOptions{};
    options.capacity=4; options.preparationWorkers=2;
    check(SpawnableHostPool::create(package,options,pool));
    auto world=std::unique_ptr<EffectWorld>{}; auto worldOptions=EffectWorldOptions{};
    worldOptions.minimumParallelWork=0; check(EffectWorld::create(worldOptions,world));
    std::array<SpawnableHostLease,4> leases{}; std::array<EffectFrame,4> frames{};
    for (auto i=0u;i<frames.size();++i) { check(pool->acquire(leases[i],frames[i].host)); }
    for (auto step=0;step<4;++step) {
        for (auto& frame:frames) { frame.time=step/60.0; frame.delta=step ? 1.0/60 : 0; }
        const auto before=allocation_check::count.load(); allocation_check::enabled=true;
        const auto result=world->advance(frames.data(),frames.size()); allocation_check::enabled=false;
        check(result); for (const auto& frame:frames) { check(frame.result); }
        require(allocation_check::count.load()==before,"Shared Core frame allocated after preparation.");
    }
    for (const auto& lease:leases) { check(pool->release(lease)); }
}
void hierarchicalAllocationCheck(const char *path)
{
    using namespace liberafx;
    using namespace liberafx::pool_tests;
    auto options = SpawnableHostPoolOptions{};
    options.capacity = 2;
    auto pool = std::unique_ptr<SpawnableHostPool>{};
    check(SpawnableHostPool::create(load(path), options, pool));
    for (auto cycle = 0; cycle < 3; ++cycle)
    {
        auto lease = SpawnableHostLease{};
        SpawnableHost *host = nullptr;
        auto before = allocation_check::count.load();
        allocation_check::enabled = true;
        auto result = pool->acquire(lease, host);
        allocation_check::enabled = false;
        check(result);
        require(allocation_check::count.load() == before, "Hierarchical acquire allocated memory.");
        check(host->advance(0, 0));
        check(host->advance(.25, .25));
        before = allocation_check::count.load();
        allocation_check::enabled = true;
        result = pool->release(lease);
        allocation_check::enabled = false;
        check(result);
        require(allocation_check::count.load() == before, "Hierarchical reset/return allocated memory.");
    }
}
int main(int argc,char** argv) {
    try {
        liberafx::pool_tests::instanceCleanup();
        liberafx::pool_tests::contracts();
        for (auto index=1;index<argc;++index) {
            if (std::strcmp(argv[index], "--hierarchy") == 0)
            {
                liberafx::pool_tests::require(index + 1 < argc, "Missing hierarchy allocation fixture.");
                hierarchicalAllocationCheck(argv[++index]);
                continue;
            }
            liberafx::pool_tests::fixture(argv[index],true);
            liberafx::pool_tests::fixture(argv[index],false);
            sharedAllocationCheck(argv[index]);
        }
        std::cout<<"Pool preparation, allocation-free acquire/reset, lease isolation and fresh/reused state agree.\n";
        return 0;
    } catch (const std::exception& error) { allocation_check::enabled=false; std::cerr<<error.what()<<'\n'; return 1; }
}
