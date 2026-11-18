using System;
using System.Diagnostics;
using System.Threading;

namespace LiberaFx.Unity.Tests
{
    public static class InstanceSessionContractTests
    {
        private static void Assert(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Instance-session request contract failed.");
        }
        private static NativeInstanceSession Create(uint instances = 4, uint requests = 2)
        {
#if UNITY_5_3_OR_NEWER
            return new UnityInstanceSession(true, instances, requests);
#else
            return new NativeInstanceSession(true, instances, requests);
#endif
        }
        private static void OnWorker(Action action)
        {
            Exception failure = null;
            var worker = new Thread(() => {
                try { action(); } catch (Exception error) { failure = error; }
            });
            worker.Start();
            if (!worker.Join(30000)) throw new TimeoutException("Instance-session worker did not finish.");
            if (failure != null) throw new InvalidOperationException("Instance-session worker failed.", failure);
        }
        private static byte[] Scalar(double value, byte opcode = 1)
        {
            var bytes = new byte[56];
            bytes[0] = (byte)'L'; bytes[1] = (byte)'F'; bytes[2] = (byte)'X'; bytes[3] = (byte)'B';
            bytes[4] = bytes[6] = bytes[8] = 1;
            bytes[27] = 1; bytes[28] = opcode;
            for (var index = 32; index < 40; ++index) bytes[index] = 255;
            var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            for (var index = 0; index < 8; ++index) bytes[40 + index] = (byte)(bits >> (index * 8));
            return bytes;
        }
        private static SessionCompletion Complete(NativeInstanceSession session, SessionRequestId request)
        {
            Assert(session.TakeCompletion(request, out _) == NativeResult.NotReady);
            Assert(session.Process(1, out var processed) == NativeResult.Ok && processed == 1);
            SessionCompletion completion = default(SessionCompletion);
            OnWorker(() => Assert(session.TakeCompletion(request, out completion) == NativeResult.Ok));
            Assert(session.TakeCompletion(request, out _) == NativeResult.InvalidHandle);
            return completion;
        }
        private static void Destroy(NativeInstanceSession session, SessionInstanceId instance)
        {
            Assert(session.SubmitDestroy(instance, out var request) == NativeResult.Ok);
            Assert(Complete(session, request).Result == NativeResult.Ok);
        }

        private static void Main()
        {
            Run();
            Console.WriteLine("Instance-session native requests, ownership and restart contracts passed.");
        }
#if UNITY_EDITOR
        [UnityEditor.MenuItem("LiberaFx/Verify instance session requests")]
#endif
        public static void Run()
        {
            NativeInstanceSession.ValidateLayout();
            using (var session = Create())
            using (var other = Create())
            {
                var bytes = Scalar(3);
                var nodeId = new byte[16]; nodeId[15] = 1;
                var ticket = default(SessionRequestId);
                OnWorker(() => {
                    Assert(session.SubmitCreate(bytes, out ticket, 2, false) == NativeResult.Ok);
                    var rejected = false;
                    try { session.Process(1, out _); } catch (InvalidOperationException) { rejected = true; }
                    Assert(rejected);
                });
                Array.Clear(bytes, 0, bytes.Length); // native must not retain this array.
                var created = Complete(session, ticket);
                Assert(created.Result == NativeResult.Ok);
                var id = created.Instance;
                Assert(session.ReadValue(id, nodeId, out var value) == NativeResult.Ok && value.X == 3);
                Assert(other.ReadValue(id, nodeId, out _) == NativeResult.InvalidHandle);
                Assert(session.Advance(id, 1, 1, out var finished) == NativeResult.Ok && !finished);
                Assert(session.SubmitReplace(id, Scalar(9), SessionReplacementPolicy.PreserveState, out ticket) == NativeResult.Ok);
                Assert(Complete(session, ticket).Result == NativeResult.UnsupportedBinary);
                Assert(session.ReadValue(id, nodeId, out value) == NativeResult.Ok && value.X == 3);
                Assert(session.SubmitReplace(id, Scalar(9, 6), SessionReplacementPolicy.Restart, out ticket) == NativeResult.Ok);
                Assert(Complete(session, ticket).Result == NativeResult.DivideByZero);
                Assert(session.ReadValue(id, nodeId, out value) == NativeResult.Ok && value.X == 3);
                Assert(session.Advance(id, 0.5, 0.5, out _) == NativeResult.InvalidArgument);
                Assert(session.SubmitReplace(id, Scalar(9), SessionReplacementPolicy.Restart, out ticket, 2, false) == NativeResult.Ok);
                Assert(Complete(session, ticket).Result == NativeResult.Ok);
                Assert(session.ReadValue(id, nodeId, out value) == NativeResult.Ok && value.X == 9);
                Assert(session.Advance(id, 1, 1, out finished) == NativeResult.Ok && !finished);
                Assert(session.Advance(id, 2, 1, out finished) == NativeResult.Ok && finished);
                Assert(session.ReadValue(id, nodeId, out value) == NativeResult.Ok && value.X == 9);
                Assert(session.TryRelease() == NativeResult.NotReady); // No implicit force-stop.
                Destroy(session, id);
                Assert(session.ReadValue(id, nodeId, out _) == NativeResult.InvalidHandle);
            }
            using (var session = Create(1, 1))
            {
                Assert(session.SubmitCreate(Scalar(4), out var ticket) == NativeResult.Ok);
                Assert(session.Process(1, out _) == NativeResult.Ok);
                Assert(session.SubmitCreate(Scalar(5), out _) == NativeResult.QueueRejected);
                Assert(session.TakeCompletion(ticket, out var created) == NativeResult.Ok && created.Result == NativeResult.Ok);
                Destroy(session, created.Instance);
                Assert(session.SubmitCreate(Scalar(5), out ticket) == NativeResult.Ok);
                var fresh = Complete(session, ticket);
                Assert(fresh.Result == NativeResult.Ok && fresh.Instance.Generation != created.Instance.Generation);
                Assert(session.SubmitDestroy(created.Instance, out ticket) == NativeResult.Ok);
                Assert(Complete(session, ticket).Result == NativeResult.InvalidHandle);
                Assert(session.SubmitNaturalEnd(fresh.Instance, out ticket) == NativeResult.Ok);
                Assert(Complete(session, ticket).Result == NativeResult.Ok);
                Assert(session.Advance(fresh.Instance, 1, 1, out var finished) == NativeResult.Ok && finished);
                Destroy(session, fresh.Instance);
            }
            ConcurrentRequests();
        }
        private static void ConcurrentRequests()
        {
            using (var session = Create(4, 2))
            {
                var done = 0;
                Exception failure = null;
                var workers = new Thread[4];
                for (var index = 0; index < workers.Length; ++index)
                {
                    workers[index] = new Thread(() => {
                        try
                        {
                            var timer = Stopwatch.StartNew();
                            Action wait = () => {
                                if (timer.ElapsedMilliseconds > 30000) throw new TimeoutException();
                                Thread.Yield();
                            };
                            Func<SessionRequestId, SessionCompletion> consume = request => {
                                SessionCompletion completion;
                                NativeResult result;
                                while ((result = session.TakeCompletion(request, out completion)) == NativeResult.NotReady) wait();
                                Assert(result == NativeResult.Ok && completion.Result == NativeResult.Ok);
                                return completion;
                            };
                            var bytes = Scalar(7);
                            for (var iteration = 0; iteration < 16; ++iteration)
                            {
                                SessionRequestId request;
                                NativeResult result;
                                while ((result = session.SubmitCreate(bytes, out request)) == NativeResult.QueueRejected) wait();
                                Assert(result == NativeResult.Ok);
                                var created = consume(request);
                                while ((result = session.SubmitDestroy(created.Instance, out request)) == NativeResult.QueueRejected) wait();
                                Assert(result == NativeResult.Ok);
                                consume(request);
                            }
                        }
                        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
                        finally { Interlocked.Increment(ref done); }
                    });
                    workers[index].Start();
                }
                var timer = Stopwatch.StartNew();
                while (Volatile.Read(ref done) != workers.Length)
                {
                    Assert(session.Process(2, out _) == NativeResult.Ok);
                    if (timer.ElapsedMilliseconds > 35000) throw new TimeoutException("Owner pump timed out.");
                    Thread.Yield();
                }
                foreach (var worker in workers) Assert(worker.Join(1000));
                if (failure != null) throw new InvalidOperationException("Concurrent managed requests failed.", failure);
            }
        }
    }
}
