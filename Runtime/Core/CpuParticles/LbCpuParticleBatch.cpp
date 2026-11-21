#include "LbCpuParticlesInternal.h"
#include "../LbVfxRuntimeBinaryInternal.h"
#include "liberafx/math/LbRandomMath.h"

namespace liberafx::cpu_particle_detail {
using namespace vfx_runtime_detail;

void System::buildBatches()
{
    buildBatch(1);
    // Births can be reordered only when both stages have no external effects.
    if (updateBatch.enabled) buildBatch(0);
}

void System::buildBatch(std::uint32_t stageIndex)
{
    const auto& stage = stages[stageIndex];
    // Start with the validated linear V3 subset. Stateful/logical nodes, IO,
    // fanout, guards and later formats retain their full interpreter semantics.
    if (stage.program.version() != 3 || stage.program.nodeCount() > ParticleBatchPlan::maximumNodes ||
        !stage.io.empty() || !nonzero(stage.entry)) return;
    auto plan = ParticleBatchPlan{};
    const auto count = stage.program.nodeCount();
    std::vector<DecodedBatchInstruction> signals(count);
    plan.sourceNodes.resize(count);
    auto append = [&](const DecodedBatchInstruction& decoded, std::vector<BatchInstruction>& target) {
        auto op=BatchInstruction{};
        op.output=static_cast<std::uint16_t>(decoded.output);
        op.op=decoded.op;
        op.width=decoded.width;
        for (auto slot=0u;slot<4;++slot) {
            const auto& input=decoded.inputs[slot];
            if (input.source!=noInput) {
                op.inputs[slot]=static_cast<std::uint16_t>(input.source*4+input.component);
                continue;
            }
            auto offset=std::size_t{};
            while (offset<plan.constants.size() && std::memcmp(plan.constants.data()+offset,input.constants.data(),4*sizeof(double))!=0)
                offset+=4;
            if (offset==plan.constants.size()) plan.constants.insert(plan.constants.end(),input.constants.begin(),input.constants.end());
            op.inputs[slot]=static_cast<std::uint16_t>(0x8000u|offset);
        }
        target.push_back(op);
    };
    std::vector<bool> isSignal(count), visited(count);
    auto entry = noInput;
    for (std::uint32_t index = 0; index < count; ++index) {
        const auto* bytes = instruction(stage.bytes.data(), 3, index);
        auto decoded = DecodedBatchInstruction{};
        std::copy_n(bytes,16,decoded.sourceNode.begin());
        plan.sourceNodes[index]=decoded.sourceNode;
        decoded.output = index;
        decoded.width = bytes[17];
        const auto opcode = bytes[16];
        if (opcode == customParameterOpcode || opcode == customSignalOpcode) {
            const auto* binding = stage.bindings[index].descriptor;
            if (!binding || binding->stateBytes != 0 || binding->initialize || binding->cleanup) return;
            for (auto input = 0u; input < bytes[18]; ++input) {
                auto& target = decoded.inputs[input];
                target.source = read32(bytes+48+input*8);
                target.component = bytes[53+input*8];
                for (auto lane = 0u; lane < 4; ++lane) target.constants[lane] = readDouble(bytes+80+input*32+lane*8);
            }
            if (binding->function == System::invoke) {
                const auto* context = static_cast<const Callback*>(binding->context);
                if (!context || context->system != this) return;
                const auto operation = context->operation;
                if (opcode == customSignalOpcode) {
                    if (bytes[19] != 1 || (operation != 5 && (operation < 0x20 || operation > 0x25))) return;
                    if (operation == 5) {
                        if (decoded.sourceNode != stage.entry) return;
                        entry = index;
                    } else decoded.op = static_cast<BatchOp>(static_cast<unsigned>(BatchOp::SetPosition) + operation-0x20);
                    isSignal[index] = true;
                    signals[index] = decoded;
                    continue;
                }
                if (operation == 0x43) decoded.op = BatchOp::Sphere;
                else if (operation == 0x49) {
                    // Offline kernels use constant RGB. A valid hand-produced
                    // kernel with varying RGB retains the general VM semantics.
                    for (auto slot=0u;slot<3;++slot) if (decoded.inputs[slot].source!=noInput) return;
                    decoded.op = BatchOp::LifetimeColor;
                }
                else {
                    if (operation < 0x10 || operation > 0x18) return;
                    decoded.op = static_cast<BatchOp>(static_cast<unsigned>(BatchOp::Position) + operation-0x10);
                }
            } else {
                auto builtinCount = std::size_t{};
                const auto* builtins = builtinNodes(builtinCount);
                const CustomNodeDescriptor* canonical = nullptr;
                for (auto candidate = 0u; candidate < builtinCount; ++candidate)
                    if (std::memcmp(builtins[candidate].typeId,bytes+24,16)==0) canonical = builtins+candidate;
                const auto mathPrefix = std::array<std::uint8_t,15>{0x74,0x30,0x5a,0xc1,0x91,0xb2,0x4c,0x33,0x8d,0x44,0,0,0,0,0};
                if (!canonical || canonical->function != binding->function || binding->context ||
                    opcode != customParameterOpcode || std::memcmp(bytes+24,mathPrefix.data(),15)!=0) return;
                if (bytes[39] == 20 && decoded.width == 1) decoded.op = BatchOp::Clamp;
                else if (bytes[39] == 47 && decoded.width == 4) decoded.op = BatchOp::Compose;
                else return;
            }
        } else {
            for (auto input = 0u; input < 2; ++input) {
                auto& target = decoded.inputs[input];
                target.source = read32(bytes+24+input*4);
                target.component = bytes[21+input];
                for (auto lane = 0u; lane < 4; ++lane) target.constants[lane] = readDouble(bytes+32+input*32+lane*8);
            }
            if (opcode == 1 || (opcode >= 7 && opcode <= 10) || opcode == 38 || opcode == 39)
                decoded.op = BatchOp::Copy;
            else if (opcode >= 3 && opcode <= 22)
                decoded.op = static_cast<BatchOp>(static_cast<unsigned>(BatchOp::Add) + (opcode-3)%4);
            else if (opcode >= 23 && opcode <= 25) decoded.op = BatchOp::Broadcast;
            else return;
        }
        append(decoded,plan.values);
    }
    if (entry == noInput) return;
    auto next = entry;
    while (next != noInput) {
        if (next >= count || !isSignal[next] || visited[next]) return;
        visited[next] = true;
        if (next != entry) append(signals[next],plan.commands);
        next = read32(instruction(stage.bytes.data(),3,next)+208);
    }
    for (auto index = 0u; index < count; ++index) if (isSignal[index] && !visited[index]) return;
    optimizeBatch(plan);
    // Eligible stages have no persistent state or mutable inputs. A single VM
    // serves unbatched births and ordered error fallback.
    auto& work = stageIndex == 1 ? sharedUpdateWork : sharedSpawnWork;
    auto& handle = stageIndex == 1 ? sharedUpdateEntry : sharedSpawnEntry;
    work.allocate(stage.program.workBytes(), stage.program.workAlignment());
    auto reference = std::make_unique<Instance>();
    if (reference->initialize(stage.program, static_cast<double*>(work.data),
            stage.program.workBytes()/sizeof(double)) != Result::Ok ||
        reference->bindSignal(stage.entry.data(), handle) != Result::Ok) return;
    (stageIndex == 1 ? sharedUpdate : sharedSpawn) = std::move(reference);
    stages[stageIndex].work.reset();
    stages[stageIndex].defaults.reset();
    plan.enabled = true;
    (stageIndex == 1 ? updateBatch : spawnBatch) = std::move(plan);
}

void System::optimizeBatch(ParticleBatchPlan& plan)
{
    constexpr auto constantBit=std::uint16_t{0x8000};
    std::array<std::uint16_t,ParticleBatchPlan::maximumNodes*4> aliases;
    for (auto index=0u;index<aliases.size();++index) aliases[index]=static_cast<std::uint16_t>(index);
    const auto alias = [&](std::uint16_t address) noexcept {
        return address&constantBit ? address : aliases[address];
    };
    // Copy and color conversion are identity operations on already validated
    // doubles. Redirect consumers without touching the original debug mapping.
    auto written=std::size_t{};
    for (auto op : plan.values) {
        for (auto& input : op.inputs) input=alias(input);
        if (op.op==BatchOp::Copy) {
            for (auto lane=0u;lane<op.width;++lane) aliases[op.output*4+lane]=static_cast<std::uint16_t>(op.inputs[0]+lane);
        } else plan.values[written++]=op;
    }
    plan.values.resize(written);
    for (auto& op : plan.commands) for (auto& input : op.inputs) input=alias(input);

    std::array<unsigned,ParticleBatchPlan::maximumNodes> uses{};
    const auto inputCount = [](BatchOp op) noexcept -> unsigned {
        switch (op) {
        case BatchOp::Position: case BatchOp::Velocity: case BatchOp::Color: case BatchOp::Size:
        case BatchOp::Life: case BatchOp::Age: case BatchOp::Id: case BatchOp::Seed: case BatchOp::Delta: return 0;
        case BatchOp::Clamp: case BatchOp::LifetimeColor: return 3;
        case BatchOp::Compose: return 4;
        case BatchOp::Add: case BatchOp::Subtract: case BatchOp::Multiply: case BatchOp::Divide: case BatchOp::Sphere: return 2;
        default: return 1;
        }
    };
    const auto countUses = [&](const auto& operations) {
        for (const auto& op : operations) for (auto slot=0u;slot<inputCount(op.op);++slot)
            if (!(op.inputs[slot]&constantBit)) ++uses[op.inputs[slot]/4];
    };
    countUses(plan.values); countUses(plan.commands);
    const auto source = [&](std::uint16_t address, BatchOp kind) noexcept -> const BatchInstruction* {
        if ((address&constantBit) || address%4!=0 || uses[address/4]!=1) return nullptr;
        for (const auto& op : plan.values) if (op.output==address/4 && op.op==kind) return &op;
        return nullptr;
    };
    const auto constant = [&](std::uint16_t address, double value) noexcept {
        return (address&constantBit) && plan.constants[address&0x7fffu]==value;
    };
    std::array<bool,ParticleBatchPlan::maximumNodes> removed{};
    for (auto& op : plan.values) {
        if (op.op!=BatchOp::Compose || op.width!=4 || !(op.inputs[0]&constantBit) ||
            !(op.inputs[1]&constantBit) || !(op.inputs[2]&constantBit)) continue;
        const auto* subtract=source(op.inputs[3],BatchOp::Subtract);
        if (!subtract || !constant(subtract->inputs[0],1)) continue;
        const auto* clamp=source(subtract->inputs[1],BatchOp::Clamp);
        if (!clamp || !constant(clamp->inputs[1],0) || !constant(clamp->inputs[2],1)) continue;
        const auto* divide=source(clamp->inputs[0],BatchOp::Divide);
        if (!divide) continue;
        const auto* age=source(divide->inputs[0],BatchOp::Age);
        const auto* life=source(divide->inputs[1],BatchOp::Life);
        if (!age || !life) continue;
        // Preserve each binary64 intermediate and finite check. Additional
        // consumers prevent fusion, so no observable register is discarded.
        for (const auto* folded : {subtract,clamp,divide,age,life}) removed[folded->output]=true;
        op.op=BatchOp::LifetimeColor;
    }
    plan.values.erase(std::remove_if(plan.values.begin(),plan.values.end(),
        [&](const auto& op) { return removed[op.output]; }),plan.values.end());
}

auto System::updateBatches(double time, const HostQualitySnapshot& quality) noexcept -> Result
{
    constexpr auto lanes = ParticleBatchPlan::batchSize;
    std::array<Slot*,lanes> particles{};
    auto cursor = std::uint32_t{};
    while (cursor < capacity) {
        auto size = std::size_t{};
        while (cursor < capacity && size < lanes) {
            auto& slot = slots[cursor++];
            if (!slot.active) continue;
            slot.age += stepDelta;
            if (slot.age >= slot.life) { slot.active = false; --live; }
            else particles[size++] = &slot;
        }
        if (size == 0) continue;
        auto referenceOnValueError = [&]() noexcept -> Result {
            // Batched pure evaluation visits instructions before particles.
            // Replay this untouched block in particle order to preserve which
            // failure wins (including an earlier particle's Signal failure).
            updateBatch.enabled = false;
            for (auto p=0u;p<size;++p) {
                const auto status=run(*particles[p],1,time,quality);
                if (status!=Result::Ok) return status;
                if (particles[p]->age>=particles[p]->life) { particles[p]->active=false; --live; }
            }
            for (;cursor<capacity;++cursor) {
                auto& slot=slots[cursor];
                if (!slot.active) continue;
                slot.age+=stepDelta;
                if (slot.age>=slot.life) { slot.active=false; --live; continue; }
                const auto status=run(slot,1,time,quality);
                if (status!=Result::Ok) return status;
                if (slot.age>=slot.life) { slot.active=false; --live; }
            }
            return Result::Ok;
        };
        auto commandsStarted = false;
        const auto status = evaluateBatch(updateBatch,particles.data(),size,&commandsStarted);
        if (status != Result::Ok) {
            // Commands already execute in particle order. Only pure-value
            // failures need replay to determine the first observable failure.
            if (commandsStarted) return status;
            return referenceOnValueError();
        }
    }
    return Result::Ok;
}

auto System::evaluateBatch(const ParticleBatchPlan& plan, Slot* const* particles, std::size_t size, bool* commandsStarted) noexcept -> Result
{
    constexpr auto lanes = ParticleBatchPlan::batchSize;
    // Node/component/particle scratch keeps arithmetic contiguous and bounded.
    std::array<double,ParticleBatchPlan::maximumNodes*4*lanes> values;
    auto input = [&](std::uint16_t address, unsigned lane, std::size_t particle) noexcept {
        return address&0x8000u ? plan.constants[(address&0x7fffu)+lane] : values[(address+lane)*lanes+particle];
    };
    for (const auto& op : plan.values) {
        if (op.op == BatchOp::LifetimeColor) {
            const auto red=input(op.inputs[0],0,0), green=input(op.inputs[1],0,0), blue=input(op.inputs[2],0,0);
            for (auto p=0u;p<size;++p) {
                if (particles[p]->life==0) return Result::InvalidArgument;
                const auto ratio=particles[p]->age/particles[p]->life;
                if (!std::isfinite(ratio)) return Result::InvalidArgument;
                values[(op.output*4)*lanes+p]=red;
                values[(op.output*4+1)*lanes+p]=green;
                values[(op.output*4+2)*lanes+p]=blue;
                values[(op.output*4+3)*lanes+p]=1-std::max(0.0,std::min(ratio,1.0));
            }
            continue;
        }
        if (op.op == BatchOp::Sphere) {
            for (auto p=0u;p<size;++p) {
                const auto radius=input(op.inputs[1],0,p);
                if (radius<0) return Result::InvalidArgument;
                const auto point=math::randomSpherePoint(seed,static_cast<std::uint32_t>(particles[p]->draw.id));
                for (auto lane=0u;lane<3;++lane) {
                    const auto value=input(op.inputs[0],lane,p)+point[lane]*radius;
                    if (!std::isfinite(value)) return Result::InvalidArgument;
                    values[(op.output*4+lane)*lanes+p]=value;
                }
            }
            continue;
        }
        for (auto lane = 0u; lane < op.width; ++lane) {
            auto* output = values.data() + (op.output*4+lane)*lanes;
            // Dispatch each decoded operation once for a particle block.
            switch (op.op) {
            case BatchOp::Copy: for (auto p=0u;p<size;++p) output[p]=input(op.inputs[0],lane,p); break;
            case BatchOp::Broadcast: for (auto p=0u;p<size;++p) output[p]=input(op.inputs[0],0,p); break;
            case BatchOp::Add: for (auto p=0u;p<size;++p) output[p]=input(op.inputs[0],lane,p)+input(op.inputs[1],lane,p); break;
            case BatchOp::Subtract: for (auto p=0u;p<size;++p) output[p]=input(op.inputs[0],lane,p)-input(op.inputs[1],lane,p); break;
            case BatchOp::Multiply: for (auto p=0u;p<size;++p) output[p]=input(op.inputs[0],lane,p)*input(op.inputs[1],lane,p); break;
            case BatchOp::Divide:
                for (auto p=0u;p<size;++p) {
                    const auto divisor=input(op.inputs[1],lane,p);
                    if (divisor==0) return Result::InvalidArgument;
                    output[p]=input(op.inputs[0],lane,p)/divisor;
                } break;
            case BatchOp::Clamp:
                for (auto p=0u;p<size;++p) {
                    const auto lower=input(op.inputs[1],0,p), upper=input(op.inputs[2],0,p);
                    if (lower>upper) return Result::InvalidArgument;
                    output[p]=std::max(lower,std::min(input(op.inputs[0],0,p),upper));
                } break;
            case BatchOp::Compose: for (auto p=0u;p<size;++p) output[p]=input(op.inputs[lane],0,p); break;
            case BatchOp::Position: for (auto p=0u;p<size;++p) output[p]=particles[p]->draw.position[lane]; break;
            case BatchOp::Velocity: for (auto p=0u;p<size;++p) output[p]=particles[p]->velocity[lane]; break;
            case BatchOp::Color: for (auto p=0u;p<size;++p) output[p]=particles[p]->draw.color[lane]; break;
            case BatchOp::Size: for (auto p=0u;p<size;++p) output[p]=particles[p]->draw.size[lane]; break;
            case BatchOp::Life: for (auto p=0u;p<size;++p) output[p]=particles[p]->life; break;
            case BatchOp::Age: for (auto p=0u;p<size;++p) output[p]=particles[p]->age; break;
            case BatchOp::Id: for (auto p=0u;p<size;++p) output[p]=static_cast<double>(particles[p]->draw.id); break;
            case BatchOp::Seed: for (auto p=0u;p<size;++p) output[p]=seed; break;
            case BatchOp::Delta: for (auto p=0u;p<size;++p) output[p]=stepDelta; break;
            default: return Result::InvalidBinary;
            }
            for (auto p=0u;p<size;++p) if (!std::isfinite(output[p])) return Result::InvalidArgument;
        }
    }
    // All values are sampled before Signals, exactly as in the V3 VM.
    // Fuse the validated local mutation chain without observers/callbacks.
    // The Update caller requests ordered retirement as well as the marker
    // that distinguishes pure failures from already committed commands.
    if (commandsStarted) *commandsStarted = true;
    for (auto p=0u;p<size;++p) {
        auto& slot=*particles[p];
        for (const auto& op : plan.commands) {
            if (op.op==BatchOp::Move) {
                for (auto lane=0u;lane<3;++lane) {
                    slot.velocity[lane]+=input(op.inputs[0],lane,p)*stepDelta;
                    slot.draw.position[lane]+=slot.velocity[lane]*stepDelta;
                    if (!std::isfinite(slot.velocity[lane]) || !std::isfinite(slot.draw.position[lane])) return Result::InvalidArgument;
                }
            } else {
                auto* target = op.op==BatchOp::SetPosition ? slot.draw.position : op.op==BatchOp::SetVelocity ? slot.velocity.data() :
                    op.op==BatchOp::SetColor ? slot.draw.color : op.op==BatchOp::SetSize ? slot.draw.size : &slot.life;
                const auto width = op.op==BatchOp::SetColor ? 4u : op.op==BatchOp::SetSize ? 2u : op.op==BatchOp::SetLife ? 1u : 3u;
                if ((op.op==BatchOp::SetLife || op.op==BatchOp::SetSize) &&
                    (input(op.inputs[0],0,p)<0 || (width==2 && input(op.inputs[0],1,p)<0))) return Result::InvalidArgument;
                for (auto lane=0u;lane<width;++lane) target[lane]=input(op.inputs[0],lane,p);
            }
        }
        if (commandsStarted && slot.age>=slot.life) { slot.active=false; --live; }
    }
    return Result::Ok;
}

auto System::spawnBatches(std::uint32_t count, double time, const HostQualitySnapshot& quality) noexcept -> Result
{
    constexpr auto lanes = ParticleBatchPlan::batchSize;
    std::array<Slot*,lanes> particles{}, updating{};
    auto cursor=std::uint32_t{};
    const auto reset = [&](Slot& slot, std::uint64_t id) noexcept {
        slot.draw={}; slot.draw.id=id; slot.velocity={}; slot.age=0;
        slot.realStart=realTime; slot.life=2; slot.active=false;
    };
    while (count!=0) {
        auto size=std::size_t{};
        const auto firstId=nextId;
        while (cursor<capacity && size<lanes && size<count) {
            auto& slot=slots[cursor++];
            if (slot.active) continue;
            reset(slot,nextId++);
            particles[size++]=&slot;
        }
        if (size==0) return Result::InsufficientWork;
        auto status=evaluateBatch(spawnBatch,particles.data(),size);
        if (status==Result::Ok) {
            auto updateCount=std::size_t{};
            for (auto p=0u;p<size;++p) if (particles[p]->life>0) updating[updateCount++]=particles[p];
            if (updateCount!=0) status=evaluateBatch(updateBatch,updating.data(),updateCount);
        }
        if (status!=Result::Ok) {
            // Restore the speculative block, then replay Spawn -> Update per
            // particle so an earlier Update failure precedes a later Spawn one.
            for (auto p=0u;p<size;++p) reset(*particles[p],0);
            nextId=firstId;
            spawnBatch.enabled=false;
            for (auto p=0u;p<size;++p) {
                auto& slot=*particles[p];
                reset(slot,nextId++);
                status=run(slot,0,time,quality);
                if (status==Result::Ok && slot.life>0) status=run(slot,1,time,quality);
                if (status!=Result::Ok) return status;
                slot.active=slot.life>0;
                live+=slot.active ? 1 : 0;
            }
        } else {
            for (auto p=0u;p<size;++p) {
                auto& slot=*particles[p];
                slot.active=slot.life>0;
                live+=slot.active ? 1 : 0;
            }
        }
        count-=static_cast<std::uint32_t>(size);
    }
    return Result::Ok;
}
}
