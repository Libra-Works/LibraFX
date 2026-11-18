#pragma once

#include "CoreMinimal.h"
#include "ProfilingDebugging/CpuProfilerTrace.h"
#include "liberafx/platform/LbProfiler.h"
#include "liberafx/platform/LbProfileRecorder.h"
#include "LibraProfileMeasurement.h"
#include <array>

class FLibraCpuProfiler final
{
public:
    void Initialize(TConstArrayView<uint8> bytecode, uint32 nodeCount, bool nodes, bool recording = false)
    {
        recordingEnabled = recording;
        counters.Reset();
        nodeIds.Reset();
        if (recording)
        {
            auto count = static_cast<size_t>(liberafx::ProfileEvent::Count) + nodeCount;
            counters = TUniquePtr<liberafx::ProfileCounter[]>(new liberafx::ProfileCounter[count]);
            recorder.initialize(counters.Get(), count);
        }
        else recorder.initialize(nullptr, 0);
#if CPUPROFILERTRACE_ENABLED
        nodeSpecs.Reset();
#endif
        if (!nodes) return;
#if CPUPROFILERTRACE_ENABLED
        nodeSpecs.Reserve(nodeCount);
#endif
        nodeIds.Reserve(nodeCount);
        auto version = bytecode[4];
        auto header = version == 1 ? 12u : 16u;
        auto stride = version == 1 ? 44u : version == 2 ? 96u : 256u;
        for (auto index = 0u; index < nodeCount; ++index)
        {
            auto bytes = bytecode.GetData() + header + index * stride;
            auto id = FString{};
            id.Reserve(32);
            for (auto lane = 0u; lane < 16; ++lane) id += FString::Printf(TEXT("%02x"), bytes[lane]);
            auto name = FString::Printf(TEXT("Libra.Node.%s"), *id);
            auto guid = FGuid{};
            FGuid::ParseExact(id, EGuidFormats::Digits, guid);
            nodeIds.Add(guid);
#if CPUPROFILERTRACE_ENABLED
            nodeSpecs.Add(FCpuProfilerTrace::OutputDynamicEventType(*name));
#endif
        }
    }

    auto GetHooks(bool nodes = false, bool accessors = false) -> liberafx::ProfilingHooks
    {
        return {this, &Begin, &End, nodes, accessors};
    }

    bool Snapshot(TArray<FLibraProfileMeasurement>& out, uint64& dropped) const
    {
        out.Reset();
        dropped = recorder.dropped();
        if (!recordingEnabled) return false;
        for (auto index = size_t{0}; index < recorder.capacity(); ++index)
        {
            auto value = liberafx::ProfileMeasurement{};
            if (!recorder.read(index, value)) continue;
            auto& row = out.AddDefaulted_GetRef();
            row.Event = UTF8_TO_TCHAR(liberafx::profileEventName(value.event));
            if (value.nodeIndex < static_cast<uint32>(nodeIds.Num())) row.NodeId = nodeIds[value.nodeIndex];
            row.Calls = value.calls;
            row.InclusiveNs = value.inclusiveNs;
            row.ExclusiveNs = value.exclusiveNs;
            row.MinNs = value.minNs;
            row.MaxNs = value.maxNs;
        }
        return true;
    }

private:
    static auto Begin(void* context, liberafx::ProfileEvent event, uint32 nodeIndex) noexcept -> liberafx::ProfileToken
    {
        auto profiler = static_cast<FLibraCpuProfiler*>(context);
        auto token = profiler->recordingEnabled ?
            liberafx::ProfileRecorder::begin(&profiler->recorder, event, nodeIndex) << 1 : uint64{0};
#if CPUPROFILERTRACE_ENABLED
        if (!CpuChannel) return token;
        if (event == liberafx::ProfileEvent::Node && nodeIndex < static_cast<uint32>(profiler->nodeSpecs.Num()))
        {
            FCpuProfilerTrace::OutputBeginEvent(profiler->nodeSpecs[nodeIndex]);
        }
        else
        {
            static const auto specs = []
            {
                auto result = std::array<uint32, static_cast<size_t>(liberafx::ProfileEvent::Count)>{};
                for (auto index = size_t{0}; index < result.size(); ++index)
                    result[index] = FCpuProfilerTrace::OutputDynamicEventType(
                        liberafx::profileEventName(static_cast<liberafx::ProfileEvent>(index)));
                return result;
            }();
            auto index = static_cast<size_t>(event);
            if (index >= specs.size()) return token;
            FCpuProfilerTrace::OutputBeginEvent(specs[index]);
        }
        token |= 1;
#endif
        return token;
    }
    static void End(void* context, liberafx::ProfileToken token) noexcept
    {
#if CPUPROFILERTRACE_ENABLED
        if ((token & 1) != 0) FCpuProfilerTrace::OutputEndEvent();
#endif
        if ((token >> 1) != 0)
            liberafx::ProfileRecorder::end(&static_cast<FLibraCpuProfiler*>(context)->recorder, token >> 1);
    }
#if CPUPROFILERTRACE_ENABLED
    TArray<uint32> nodeSpecs;
#endif
    TArray<FGuid> nodeIds;
    TUniquePtr<liberafx::ProfileCounter[]> counters;
    liberafx::ProfileRecorder recorder;
    bool recordingEnabled = false;
};
