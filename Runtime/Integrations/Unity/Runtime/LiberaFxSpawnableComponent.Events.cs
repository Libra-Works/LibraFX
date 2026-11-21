using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiberaFx.Unity
{
    public sealed partial class LiberaFxSpawnableComponent
    {
        private void DrainEvents(NativeSpawnableHost current)
        {
            ++eventDrainDepth;
            try { DrainEventBatch(current); }
            finally
            {
                if (--eventDrainDepth == 0) cancelledBatchScopes.Clear();
            }
        }

        private void DrainEventBatch(NativeSpawnableHost current)
        {
            var events = new List<SpawnableHostEvent>();
            while (host == current)
            {
                var result = current.PopEvent(out var item);
                if (result != NativeResult.Ok)
                {
                    FailPlayback(result, "Native lifecycle event read failed: " + result + ".", current);
                    return;
                }
                if (item == null) break;
                events.Add(item);
            }
            ProcessHostEventBatch(current, events);
            if (host != current) return;
            while (host == current && !effectForced)
            {
                var result = current.PopGameIoEvent(out var ioEvent);
                if (result != NativeResult.Ok)
                {
                    FailPlayback(result, "Native Game I/O event read failed: " + result + ".", current);
                    return;
                }
                if (ioEvent == null) break;
                InvokeScriptAction(ioEvent, current);
                if (host != current || effectForced)
                {
                    return;
                }
                InvokeGameIoEvent(ioEvent);
                if (host != current) return;
            }
        }

        private void ProcessHostEventBatch(NativeSpawnableHost current, List<SpawnableHostEvent> events)
        {
            var terminalScopes = new HashSet<string>(StringComparer.Ordinal);
            var wholeEffectTerminated = false;
            try
            {
                foreach (var item in events)
                    if (item.Kind == ForceTerminatedEvent && IsWholeEffect(item))
                        wholeEffectTerminated = true;
                    else if (item.Kind == StopEvent || item.Kind == DestroyEvent || item.Kind == ForceTerminatedEvent)
                        terminalScopes.Add(ScopeKey(item.ContainerId, item.InstanceId));
            }
            catch (Exception error)
            {
                FailPlayback(NativeResult.InternalError,
                    "Could not identify a native render scope: " + error.Message, current);
                return;
            }

            foreach (var item in events)
            {
                if (host != current) return;
                if (item.Kind != ForceTerminatedEvent &&
                    (effectForced || cancelledBatchScopes.Contains(ScopeKey(item.ContainerId, item.InstanceId))))
                    continue;
                try
                {
                    var skipSpawn = item.Kind == SpawnEvent &&
                        (wholeEffectTerminated || terminalScopes.Contains(ScopeKey(item.ContainerId, item.InstanceId)));
                    ProcessHostEvent(current, item, skipSpawn);
                }
                catch (Exception error)
                {
                    FailPlayback(NativeResult.InternalError,
                        "Could not create or retire render scope: " + error.Message, current);
                    return;
                }
                if (host != current) return;
                InvokeHostEvent(item);
                if (host != current) return;
            }
        }

        private void ProcessHostEvent(NativeSpawnableHost current, SpawnableHostEvent item, bool skipSpawn)
        {
            if (item.Kind == ForceTerminatedEvent && IsWholeEffect(item))
            {
                effectForced = rootFinished = true;
                ReleaseAllScopes(); // includes every GPU owner
                return;
            }
            if (item.Kind == SignalEvent) return;
            var scopeKey = ScopeKey(item.ContainerId, item.InstanceId);
            if (item.Kind == SpawnEvent)
            {
                if (skipSpawn) return;
                var templateId = GuidFromBigEndian(item.ContainerId);
                if (!templateDefinitions.TryGetValue(templateId, out var definition))
                    throw new InvalidOperationException("Spawn event references unknown template " + templateId + ".");
                if (instanceScopes.ContainsKey(scopeKey))
                    throw new InvalidOperationException("Duplicate active Spawn event for instance " + item.InstanceId + ".");
                var scope = LiberaFxRenderScope.CreatePending(transform, definition, false,
                    item.ContainerId, item.InstanceId, item.Time, item.Seed, suppressPreviewDebugDraw,
                    renderStatistics, current.CoordinateBasis);
                try
                {
                    scope.Initialize(current, elapsed);
                    instanceScopes.Add(scopeKey, scope);
                }
                catch
                {
                    scope.Dispose();
                    throw;
                }
            }
            else if (item.Kind == StopEvent || item.Kind == DestroyEvent || item.Kind == ForceTerminatedEvent)
            {
                // Stop values may already be finalized; release without reading them.
                // Destroy is a natural completion notification, not a synthetic Stop.
                if (item.Kind == ForceTerminatedEvent) cancelledBatchScopes.Add(scopeKey);
                ReleaseChildScope(scopeKey);
            }
            else throw new InvalidOperationException("Unknown native lifecycle event kind " + item.Kind + ".");
        }

        private static bool IsWholeEffect(SpawnableHostEvent item) =>
            IsWholeEffect(item.ContainerId, item.InstanceId);

        private static bool IsWholeEffect(byte[] containerId, ulong instanceId)
        {
            if (instanceId != 0 || containerId == null || containerId.Length != 16)
            {
                return false;
            }
            foreach (var value in containerId)
            {
                if (value != 0)
                {
                    return false;
                }
            }
            return true;
        }

        private void UpdateRenderScopes(NativeSpawnableHost current)
        {
            rootRenderScope?.Update(current, elapsed);
            if (host != current) return;
            if (instanceScopes.Count == 0) return;
            var scopes = new List<LiberaFxRenderScope>(instanceScopes.Values);
            foreach (var scope in scopes)
            {
                if (host != current) return;
                scope.Update(current, elapsed);
            }
        }

        private NativeResult FailPlayback(NativeResult result, string message,
            NativeInstance.NativeSpawnableHostBase candidate)
        {
            LastError = message;
            if (candidate != null)
            {
                if (candidate == host && debugging)
                {
                    candidate.DetachDebugger();
                    debugging = debugPending = false;
                }
                for (var attempt = 0; attempt < 16; ++attempt)
                {
                    var stop = candidate.Stop();
                    if (stop != NativeResult.QueueRejected) break;
                }
            }
            if (host == candidate) host = null;
            ReleaseAllScopes();
            candidate?.Dispose();
            templateDefinitions.Clear();
            skeletalPoseSources.Clear();
            sourceInitialAdvancePending = false;
            rootFinished = false;
            effectForced = false;
            elapsed = 0;
            Debug.LogError("LiberaFx playback failed: " + message, this);
            InvokeRuntimeError(result);
            return result;
        }

        private void ReleasePlayback(NativeInstance.NativeSpawnableHostBase current)
        {
            if (host == current)
            {
                host = null;
                debugging = debugPending = false;
            }
            ReleaseAllScopes();
            templateDefinitions.Clear();
            skeletalPoseSources.Clear();
            sourceInitialAdvancePending = false;
            current?.Dispose();
            elapsed = 0;
            rootFinished = false;
            effectForced = false;
        }

        private void ReleaseAllScopes()
        {
            var root = rootRenderScope;
            var children = new List<LiberaFxRenderScope>(instanceScopes.Values);
            rootRenderScope = null;
            instanceScopes.Clear();
            root?.Dispose();
            foreach (var child in children) child.Dispose();
        }

        private void ReleaseChildScope(string key)
        {
            if (!instanceScopes.TryGetValue(key, out var scope)) return;
            instanceScopes.Remove(key);
            scope.Dispose();
        }

        private NativeResult Report(NativeResult result, string message)
        {
            LastError = message;
            Debug.LogError("LiberaFx playback error: " + message, this);
            InvokeRuntimeError(result);
            return result;
        }

        private void InvokeHostEvent(SpawnableHostEvent item)
        {
            var handlers = HostEventReceived;
            if (handlers == null) return;
            foreach (Action<SpawnableHostEvent> handler in handlers.GetInvocationList())
            {
                try { handler(item); }
                catch (Exception error) { Debug.LogException(error, this); }
            }
        }

        private void InvokeGameIoEvent(GameIoEvent item)
        {
            var handlers = GameIoEventReceived;
            if (handlers == null) return;
            foreach (Action<GameIoEvent> handler in handlers.GetInvocationList())
            {
                try { handler(item); }
                catch (Exception error) { Debug.LogException(error, this); }
            }
        }

        private void InvokeRuntimeError(NativeResult result)
        {
            var handlers = RuntimeError;
            if (handlers == null) return;
            foreach (Action<NativeResult> handler in handlers.GetInvocationList())
            {
                try { handler(result); }
                catch (Exception error) { Debug.LogException(error, this); }
            }
        }
    }
}
