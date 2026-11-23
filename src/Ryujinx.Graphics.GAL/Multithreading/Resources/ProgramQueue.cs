using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading.Resources
{
    /// <summary>
    /// A structure handling multithreaded compilation for programs.
    /// </summary>
    class ProgramQueue
    {
        private const int MaxConcurrentCompilations = 8;

        private readonly IRenderer _renderer;

        private readonly Queue<IProgramRequest> _toCompile;
        private readonly List<ThreadedProgram> _inProgress;

        // Guards _generation and _stopped. This lock is never held while ProcessQueue() runs.
        private readonly object _sync = new();

        // Serializes ProcessQueue() against Stop(), so a queue in the middle of processing
        // cannot touch the backend renderer after its resources start being released.
        private readonly object _processLock = new();

        private long _generation;
        private volatile bool _stopped;

        public ProgramQueue(IRenderer renderer)
        {
            _renderer = renderer;

            _toCompile = new Queue<IProgramRequest>();
            _inProgress = new List<ThreadedProgram>();
        }

        public void Add(IProgramRequest request)
        {
            if (_stopped)
            {
                return;
            }

            lock (_toCompile)
            {
                _toCompile.Enqueue(request);
            }
        }

        public void ProcessQueue()
        {
            lock (_processLock)
            {
                if (_stopped)
                {
                    return;
                }

                for (int i = 0; i < _inProgress.Count; i++)
                {
                    ThreadedProgram program = _inProgress[i];

                    ProgramLinkStatus status = program.Base.CheckProgramLink(false);

                    if (status != ProgramLinkStatus.Incomplete)
                    {
                        Unsubscribe(program.Base);

                        program.Compiled = true;
                        _inProgress.RemoveAt(i--);
                    }
                }

                int freeSpace = MaxConcurrentCompilations - _inProgress.Count;

                for (int i = 0; i < freeSpace; i++)
                {
                    // Begin compilation of some programs in the compile queue.
                    IProgramRequest program;

                    lock (_toCompile)
                    {
                        if (!_toCompile.TryDequeue(out program))
                        {
                            break;
                        }
                    }

                    if (program.Threaded.Base == null)
                    {
                        program.Threaded.Base = program.Create(_renderer);
                    }

                    Subscribe(program.Threaded.Base);

                    if (program.Threaded.Base != null)
                    {
                        ProgramLinkStatus status = program.Threaded.Base.CheckProgramLink(false);

                        if (status != ProgramLinkStatus.Incomplete)
                        {
                            // This program is already compiled. Keep going through the queue.
                            Unsubscribe(program.Threaded.Base);

                            program.Threaded.Compiled = true;
                            i--;
                            continue;
                        }
                    }

                    _inProgress.Add(program.Threaded);
                }
            }
        }

        /// <summary>
        /// Process the queue until the given program has finished compiling.
        /// This will begin compilation of other programs on the queue as well.
        /// </summary>
        /// <param name="program">The program to wait for</param>
        /// <returns>True if the program completed, false if the queue was stopped.</returns>
        public bool WaitForProgram(ThreadedProgram program)
        {
            while (true)
            {
                long generation;
                bool compiled;

                lock (_sync)
                {
                    if (_stopped)
                    {
                        return false;
                    }

                    compiled = program.Compiled;

                    if (!compiled)
                    {
                        generation = _generation;
                    }
                    else
                    {
                        generation = -1;
                    }
                }

                // Must run on the render thread and outside _sync: ProcessQueue calls back
                // into the backend renderer and can complete the program we are waiting for.
                ProcessQueue();

                lock (_sync)
                {
                    if (_stopped)
                    {
                        return false;
                    }

                    if (!program.Compiled)
                    {
                        if (_generation == generation)
                        {
                            // No compilation state change happened between reading the generation
                            // and reacquiring the lock, so it is safe to sleep until the next one.
                            Monitor.Wait(_sync);
                        }

                        continue;
                    }
                }

                // The waited program is done, but its completion may have raced ahead of other
                // in-progress programs in the same ProcessQueue pass. Run one more pass so the
                // queue settles and no completed program is left occupying a compile slot.
                ProcessQueue();

                lock (_sync)
                {
                    if (_stopped)
                    {
                        return false;
                    }

                    return program.Compiled;
                }
            }
        }

        /// <summary>
        /// Stops the queue and wakes any thread waiting for a program compilation.
        /// No ProcessQueue() call will touch the backend renderer after this method returns.
        /// </summary>
        public void Stop()
        {
            lock (_sync)
            {
                _stopped = true;
                _generation++;
                Monitor.PulseAll(_sync);
            }

            // Wait for a ProcessQueue() that may already be in flight. Any later call observes
            // _stopped and returns without touching the backend renderer.
            lock (_processLock)
            {
            }
        }

        private void Subscribe(IProgram program)
        {
            if (program is IProgramCompilationNotifier notifier)
            {
                notifier.CompilationCompleted += OnCompilationCompleted;
            }
        }

        private void Unsubscribe(IProgram program)
        {
            if (program is IProgramCompilationNotifier notifier)
            {
                notifier.CompilationCompleted -= OnCompilationCompleted;
            }
        }

        private void OnCompilationCompleted()
        {
            lock (_sync)
            {
                _generation++;
                Monitor.PulseAll(_sync);
            }
        }
    }
}
