using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    [TestFixture]
    [NonParallelizable]
    public class ProgramQueueTests
    {
        private const int WaitTimeoutMs = 5000;

        [Test]
        public void WaitForSingleProgramCompletes()
        {
            ProgramQueue queue = new(null);
            (FakeProgram program, ThreadedProgram threaded, FakeRequest request) = CreateProgram(queue);

            Task<bool> wait = Task.Run(() => queue.WaitForProgram(threaded));

            Assert.That(SpinWait.SpinUntil(() => program.Polls > 0, WaitTimeoutMs), Is.True);

            program.Complete();

            Assert.That(wait.Wait(WaitTimeoutMs), Is.True, "WaitForProgram did not finish");
            Assert.That(wait.Result, Is.True);
            Assert.That(threaded.Compiled, Is.True);

            GC.KeepAlive(request);
        }

        [TestCase(1)]
        [TestCase(8)]
        [TestCase(9)]
        [TestCase(24)]
        public void WaitForLastProgramWithQueueSize(int count)
        {
            ProgramQueue queue = new(null);

            FakeProgram[] programs = new FakeProgram[count];
            ThreadedProgram[] threaded = new ThreadedProgram[count];
            FakeRequest[] requests = new FakeRequest[count];

            for (int i = 0; i < count; i++)
            {
                (programs[i], threaded[i], requests[i]) = CreateProgram(queue);
            }

            Task<bool> wait = Task.Run(() => queue.WaitForProgram(threaded[^1]));

            Assert.That(SpinWait.SpinUntil(() => programs[0].Polls > 0, WaitTimeoutMs), Is.True);

            for (int i = 0; i < count; i++)
            {
                programs[i].Complete();

                if ((i & 3) == 0)
                {
                    Thread.Yield();
                }
            }

            Assert.That(wait.Wait(WaitTimeoutMs), Is.True, $"WaitForProgram did not finish for {count} programs");
            Assert.That(wait.Result, Is.True);

            for (int i = 0; i < count; i++)
            {
                Assert.That(threaded[i].Compiled, Is.True, $"Program {i} was not marked as compiled");
            }

            GC.KeepAlive(requests);
        }

        [Test]
        public void CompileFailureIsATerminalState()
        {
            ProgramQueue queue = new(null);
            (FakeProgram program, ThreadedProgram threaded, _) = CreateProgram(queue);

            Task<bool> wait = Task.Run(() => queue.WaitForProgram(threaded));

            Assert.That(SpinWait.SpinUntil(() => program.Polls > 0, WaitTimeoutMs), Is.True);

            program.Complete(ProgramLinkStatus.Failure);

            Assert.That(wait.Wait(WaitTimeoutMs), Is.True, "WaitForProgram did not finish after a compile failure");
            Assert.That(wait.Result, Is.True);
            Assert.That(threaded.Compiled, Is.True);
        }

        [Test]
        public void CompletionAfterBackgroundExceptionDoesNotHang()
        {
            ProgramQueue queue = new(null);
            (FakeProgram program, ThreadedProgram threaded, _) = CreateProgram(queue);

            Task<bool> wait = Task.Run(() => queue.WaitForProgram(threaded));

            Assert.That(SpinWait.SpinUntil(() => program.Polls > 0, WaitTimeoutMs), Is.True);

            // Models a background task that fails before reporting a terminal state.
            Task.Run(() =>
            {
                try
                {
                    throw new InvalidOperationException("simulated background failure");
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    program.Complete(ProgramLinkStatus.Failure);
                }
            }).Wait(WaitTimeoutMs);

            Assert.That(wait.Wait(WaitTimeoutMs), Is.True, "WaitForProgram did not finish after a background exception");
            Assert.That(wait.Result, Is.True);
            Assert.That(threaded.Compiled, Is.True);
        }

        [Test]
        public void AlreadyCompiledProgramReturnsImmediately()
        {
            ProgramQueue queue = new(null);
            (FakeProgram program, ThreadedProgram threaded, _) = CreateProgram(queue);

            program.Complete();

            Assert.That(queue.WaitForProgram(threaded), Is.True);
            Assert.That(threaded.Compiled, Is.True);
        }

        [Test]
        public void StopWakesWaiterWithoutCompletedCompilation()
        {
            ProgramQueue queue = new(null);
            (FakeProgram program, ThreadedProgram threaded, _) = CreateProgram(queue);

            Task<bool> wait = Task.Run(() => queue.WaitForProgram(threaded));

            Assert.That(SpinWait.SpinUntil(() => program.Polls > 0, WaitTimeoutMs), Is.True);

            queue.Stop();

            Assert.That(wait.Wait(WaitTimeoutMs), Is.True, "WaitForProgram did not finish after Stop");
            Assert.That(wait.Result, Is.False);
        }

        [Test]
        public void StopWaitsForInFlightProcessQueue()
        {
            ProgramQueue queue = new(null);
            (FakeProgram program, _, FakeRequest request) = CreateProgram(queue);

            using ManualResetEventSlim entered = new(false);
            using ManualResetEventSlim release = new(false);

            program.OnCheck = () =>
            {
                entered.Set();
                release.Wait(WaitTimeoutMs);
            };

            queue.Add(request);

            Task pump = Task.Run(queue.ProcessQueue);

            Assert.That(entered.Wait(WaitTimeoutMs), Is.True, "ProcessQueue did not enter CheckProgramLink");

            Task stop = Task.Run(queue.Stop);

            Assert.That(stop.Wait(200), Is.False, "Stop returned while ProcessQueue was still running");

            release.Set();

            Assert.That(stop.Wait(WaitTimeoutMs), Is.True, "Stop did not finish after ProcessQueue was released");
            Assert.That(pump.Wait(WaitTimeoutMs), Is.True);

            int polls = program.Polls;

            queue.ProcessQueue();

            Assert.That(program.Polls, Is.EqualTo(polls), "ProcessQueue touched the backend after Stop");
        }

        [Test]
        public void CompletionRacingWaitDoesNotLoseWakeup()
        {
            const int Iterations = 300;

            for (int i = 0; i < Iterations; i++)
            {
                ProgramQueue queue = new(null);
                (FakeProgram program, ThreadedProgram threaded, _) = CreateProgram(queue);

                Task<bool> wait = Task.Run(() => queue.WaitForProgram(threaded));
                Task complete = Task.Run(() => program.Complete());

                Assert.That(wait.Wait(WaitTimeoutMs), Is.True, $"Iteration {i}: WaitForProgram did not finish");
                Assert.That(complete.Wait(WaitTimeoutMs), Is.True);
                Assert.That(wait.Result, Is.True, $"Iteration {i}: WaitForProgram returned false");
            }
        }

        [Test]
        public void StopAndProcessQueueStress()
        {
            const int Iterations = 100;

            for (int i = 0; i < Iterations; i++)
            {
                ProgramQueue queue = new(null);
                (FakeProgram program, ThreadedProgram threaded, FakeRequest request) = CreateProgram(queue);

                queue.Add(request);

                Task<bool> wait = Task.Run(() => queue.WaitForProgram(threaded));
                Task pump = Task.Run(() =>
                {
                    for (int j = 0; j < 8; j++)
                    {
                        queue.ProcessQueue();
                    }
                });

                queue.Stop();

                Assert.That(wait.Wait(WaitTimeoutMs), Is.True, $"Iteration {i}: waiter did not finish");
                Assert.That(wait.Result, Is.False);
                Assert.That(pump.Wait(WaitTimeoutMs), Is.True, $"Iteration {i}: pump did not finish");
                Assert.That(program.Polls, Is.GreaterThanOrEqualTo(0));
            }
        }

        private static (FakeProgram, ThreadedProgram, FakeRequest) CreateProgram(ProgramQueue queue)
        {
            ThreadedProgram threaded = new(null);
            FakeProgram program = new();
            FakeRequest request = new(threaded, program);

            queue.Add(request);

            return (program, threaded, request);
        }

        private sealed class FakeRequest : IProgramRequest
        {
            public ThreadedProgram Threaded { get; set; }

            private readonly FakeProgram _program;

            public FakeRequest(ThreadedProgram threaded, FakeProgram program)
            {
                Threaded = threaded;
                _program = program;
            }

            public IProgram Create(IRenderer renderer)
            {
                return _program;
            }
        }

        private sealed class FakeProgram : IProgram, IProgramCompilationNotifier
        {
            private readonly object _gate = new();

            private bool _completed;
            private ProgramLinkStatus _result = ProgramLinkStatus.Success;
            private int _polls;
            private Action _onCheck;

            public event Action CompilationCompleted;

            public int Polls => Volatile.Read(ref _polls);

            public Action OnCheck
            {
                set => _onCheck = value;
            }

            public void Complete(ProgramLinkStatus result = ProgramLinkStatus.Success)
            {
                lock (_gate)
                {
                    _result = result;
                    _completed = true;
                }

                CompilationCompleted?.Invoke();
            }

            public ProgramLinkStatus CheckProgramLink(bool blocking)
            {
                Interlocked.Increment(ref _polls);

                _onCheck?.Invoke();

                lock (_gate)
                {
                    return _completed ? _result : ProgramLinkStatus.Incomplete;
                }
            }

            public byte[] GetBinary()
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
    }
}
