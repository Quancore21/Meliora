using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.HOS.Services.SurfaceFlinger;
using System;
using System.Diagnostics;

namespace Ryujinx.Headless.SDL2
{
    /// <summary>
    /// Meliora Adaptive Engine: a closed-loop render scale controller driven by the
    /// render loop itself.
    ///
    /// Every presented frame contributes its present-to-present interval to a ring
    /// buffer. Once per window (~1.6s at 60 FPS) the controller evaluates the 95th
    /// percentile frame time against the pacing budget. Frames are only considered
    /// "ours to fix" when the GPU thread is saturated at the same time (GPU-bound);
    /// slow frames with an idle GPU thread mean the bottleneck is on the CPU or in
    /// the game's own pacing, and dropping resolution cannot fix either.
    ///
    /// After three consecutive failing windows it steps the internal render scale
    /// down (x0.95, then x0.90, floor x0.85 of the scale the game was started with,
    /// never below 0.5x absolute), at most one step per 5s. After ~16s of green
    /// windows it climbs back towards the starting scale, one step per 10s.
    ///
    /// Only <see cref="GraphicsConfig.ResScale"/> is written: the scale the game was
    /// started with is the ceiling, no profile is touched, and the controller always
    /// climbs home when the load passes. Every decision is logged under
    /// "[Meliora] adaptive:" so a game log shows exactly what the controller saw.
    /// </summary>
    public static class AdaptivePerformance
    {
        private const int WindowSize = 96;               // samples per evaluation window
        private const int WarmupPresents = 90;           // shader cache load and first-frame spikes
        private const int WarmupMs = 5000;               // same, as a minimum wall-clock time
        private const double BudgetTolerance = 1.12;     // p95 may exceed the budget by this much
        private const int FailWindowsToStepDown = 3;     // ~5s of sustained overload
        private const int PassWindowsToStepUp = 10;      // ~16s of sustained green
        private const int DownCooldownMs = 5000;         // max one down-step per 5s
        private const int UpCooldownMs = 10000;          // max one up-step per 10s
        private const float DownStep = 0.95f;            // each down-step, cumulative
        private const float FloorFraction = 0.85f;       // floor relative to the starting scale
        private const float AbsoluteFloor = 0.5f;
        private const float UpStep = 1.06f;
        private const double GpuBusyForDownStep = 0.70;  // GPU thread busy fraction that means GPU-bound
        private const double GpuBusyForUpStep = 0.80;    // don't climb while the GPU is busier than this
        private const double GpuIdleForCpuBoundLog = 0.35;
        private const double MaxSampleMs = 100;          // clamp: pauses and loads are not frame times
        private const int LogEveryWindows = 6;           // periodic status line, ~10s apart

        private static readonly double[] _intervals = new double[WindowSize];

        private static bool _announced;
        private static bool _enabledCache;
        private static long _enabledCacheMs;

        private static long _lastIterationTicks = -1;
        private static long _lastPresentTicks = -1;
        private static long _sessionStartMs = -1;

        private static int _windowIndex;
        private static int _windowCount;
        private static int _warmupPresentsLeft = WarmupPresents;
        private static long _busyTicks;
        private static long _wallTicks;

        private static int _failStreak;
        private static int _passStreak;
        private static long _lastStepMs;
        private static long _lastCpuBoundLogMs;

        private static float _startScale = -1f;
        private static float _scale = -1f;

        /// <summary>
        /// Meliora: clears all per-game controller state. Called once per game launch,
        /// at the start of a new session's render loop.
        ///
        /// The class is static because there is one render loop per process, but a game
        /// can be stopped and another started inside the same app run: without the
        /// reset the next game would skip the warmup, and if the previous game ended
        /// with a lowered scale while startup code already reset ResScale, the
        /// controller would believe the new game started low and "step up" to a value
        /// the user never chose.
        /// </summary>
        public static void ResetSession()
        {
            _announced = false;
            _lastIterationTicks = -1;
            _lastPresentTicks = -1;
            _sessionStartMs = -1;
            _windowIndex = 0;
            _windowCount = 0;
            _warmupPresentsLeft = WarmupPresents;
            _busyTicks = 0;
            _wallTicks = 0;
            _failStreak = 0;
            _passStreak = 0;
            _lastStepMs = 0;
            _lastCpuBoundLogMs = 0;
            _startScale = -1f;
            _scale = -1f;
        }

        /// <summary>
        /// Called once per render loop iteration, after FIFO processing and presents.
        /// The render loop thread is the only caller, so no synchronization is needed.
        /// </summary>
        /// <param name="fifoBusyTicks">Time the iteration spent processing FIFO commands, in host ticks</param>
        /// <param name="presented">Number of frames presented during the iteration</param>
        public static void OnIterationEnd(long fifoBusyTicks, int presented)
        {
            long nowTicks = Stopwatch.GetTimestamp(); // host ticks: iteration and interval bookkeeping
            long nowMs = Environment.TickCount64;     // milliseconds: cooldowns and logging

            if (!_announced)
            {
                _announced = true;
                _enabledCache = Environment.GetEnvironmentVariable("MELIORA_ADAPTIVE") != "0";
                _enabledCacheMs = nowMs;

                Console.WriteLine($"[Meliora] adaptive: {(_enabledCache ? "on" : "off (MELIORA_ADAPTIVE=0)")}");
            }

            if (nowMs - _enabledCacheMs >= 5000)
            {
                // The settings toggle may have changed since; pick it up at the next launch.
                _enabledCache = Environment.GetEnvironmentVariable("MELIORA_ADAPTIVE") != "0";
                _enabledCacheMs = nowMs;
            }

            if (!_enabledCache)
            {
                return;
            }

            if (_lastIterationTicks < 0)
            {
                _startScale = GraphicsConfig.ResScale;
                _scale = _startScale;
                _lastIterationTicks = nowTicks;
                _lastPresentTicks = presented > 0 ? nowTicks : -1;

                Console.WriteLine($"[Meliora] adaptive: start {_startScale:0.00}x, floor {Math.Max(AbsoluteFloor, _startScale * FloorFraction):0.00}x, target {MelioraFramePacing.TargetHz} fps");

                return;
            }

            // Warmup: the first seconds are dominated by shader cache loading and JIT,
            // which would immediately trip the controller. Skip them entirely.
            if (_sessionStartMs < 0)
            {
                _sessionStartMs = nowMs;
            }

            if (_warmupPresentsLeft > 0 || nowMs - _sessionStartMs < WarmupMs)
            {
                if (presented > 0)
                {
                    _warmupPresentsLeft -= presented;
                    _lastPresentTicks = nowTicks;
                }

                _lastIterationTicks = nowTicks;
                return;
            }

            long wallDelta = Math.Max(0, nowTicks - _lastIterationTicks);
            _wallTicks += wallDelta;
            _busyTicks += Math.Clamp(fifoBusyTicks, 0, wallDelta);

            // Present-to-present intervals are the frame times the user actually sees.
            if (presented > 0)
            {
                long lastPresent = _lastPresentTicks > 0 ? _lastPresentTicks : nowTicks;

                for (int i = 0; i < presented; i++)
                {
                    // Extra frames presented back-to-back in one iteration are buffered
                    // catch-up, not real frame times; only the first gap counts.
                    long intervalTicks = i == 0 ? nowTicks - lastPresent : 0;

                    PushInterval(intervalTicks);
                }

                _lastPresentTicks = nowTicks;
            }

            _lastIterationTicks = nowTicks;

            if (_windowIndex >= WindowSize)
            {
                Evaluate(nowMs);
            }
        }

        private static void PushInterval(long intervalTicks)
        {
            if (_windowIndex >= WindowSize)
            {
                // The window is already full: a catch-up burst after a hitch may
                // present more frames in one iteration than the window holds. The
                // extra samples are the least meaningful ones, so drop them; the
                // evaluation below happens on the full window.
                return;
            }

            double intervalMs = intervalTicks / (double)Stopwatch.Frequency * 1000.0;

            _intervals[_windowIndex++] = Math.Min(intervalMs, MaxSampleMs);
        }

        private static void Evaluate(long nowMs)
        {
            _windowIndex = 0;
            _windowCount++;

            // Copy and sort to get percentiles without touching the live buffer.
            Span<double> sorted = stackalloc double[WindowSize];

            for (int i = 0; i < WindowSize; i++)
            {
                sorted[i] = _intervals[i];
            }

            sorted.Sort();

            double p95 = sorted[(WindowSize * 95) / 100];
            double median = sorted[WindowSize / 2];
            double busyFraction = _wallTicks > 0 ? _busyTicks / (double)_wallTicks : 0.0;

            _busyTicks = 0;
            _wallTicks = 0;

            // Budget: the pacing target, but never below what the game itself sustains
            // in this window. A game that steadily runs 30 FPS is not overloaded.
            double budgetMs = Math.Max(1000.0 / MelioraFramePacing.TargetHz, median) * BudgetTolerance;

            bool slow = p95 > budgetMs;
            bool periodicLog = _windowCount % LogEveryWindows == 0;

            if (slow)
            {
                _failStreak++;
                _passStreak = 0;

                if (busyFraction < GpuIdleForCpuBoundLog && nowMs - _lastCpuBoundLogMs >= 10000)
                {
                    _lastCpuBoundLogMs = nowMs;

                    Console.WriteLine($"[Meliora] adaptive: slow frames but GPU idle (gpuBusy={(int)(busyFraction * 100)}%), not touching scale (CPU-bound?)");
                }
            }
            else
            {
                _passStreak++;
                _failStreak = 0;
            }

            float floorScale = Math.Max(AbsoluteFloor, _startScale * FloorFraction);
            bool stepped = false;

            if (slow &&
                busyFraction >= GpuBusyForDownStep &&
                _failStreak >= FailWindowsToStepDown &&
                nowMs - _lastStepMs >= DownCooldownMs)
            {
                if (_scale > floorScale + 0.001f)
                {
                    float next = Math.Max(floorScale, _scale * DownStep);

                    Console.WriteLine($"[Meliora] adaptive: step down p95={p95:0.0}ms budget={budgetMs:0.0}ms gpuBusy={(int)(busyFraction * 100)}% scale={_scale:0.00}x -> {next:0.00}x");

                    _scale = next;
                    GraphicsConfig.ResScale = _scale;
                    _lastStepMs = nowMs;
                    _failStreak = 0;
                    _passStreak = 0;
                    stepped = true;
                }
                else if (periodicLog)
                {
                    Console.WriteLine($"[Meliora] adaptive: at floor ({floorScale:0.00}x) p95={p95:0.0}ms budget={budgetMs:0.0}ms gpuBusy={(int)(busyFraction * 100)}%");
                }
            }
            else if (!slow &&
                busyFraction <= GpuBusyForUpStep &&
                _passStreak >= PassWindowsToStepUp &&
                nowMs - _lastStepMs >= UpCooldownMs &&
                _scale < _startScale - 0.001f)
            {
                float next = _scale * UpStep;

                if (next > _startScale - 0.01f)
                {
                    // Snap home instead of asymptotically creeping towards it.
                    next = _startScale;
                }

                Console.WriteLine($"[Meliora] adaptive: step up p95={p95:0.0}ms budget={budgetMs:0.0}ms gpuBusy={(int)(busyFraction * 100)}% scale={_scale:0.00}x -> {next:0.00}x");

                _scale = next;
                GraphicsConfig.ResScale = _scale;
                _lastStepMs = nowMs;
                _passStreak = 0;
                stepped = true;
            }

            if (periodicLog && !stepped)
            {
                Console.WriteLine($"[Meliora] adaptive: p95={p95:0.0}ms median={median:0.0}ms budget={budgetMs:0.0}ms gpuBusy={(int)(busyFraction * 100)}% scale={_scale:0.00}x");
            }
        }
    }
}
