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
    /// buffer. Once per window (~1.6s at 60 FPS) the controller evaluates the median
    /// frame time against the pacing target. The median, not the 95th percentile, is
    /// the gate: single slow frames (shader compiles, allocations) are not fixable by
    /// lowering the resolution, and the GPU thread busy fraction is polluted by the
    /// same stalls, so only a sustained median slowdown backed by a saturated GPU
    /// thread justifies a step down.
    ///
    /// After three consecutive slow, GPU-bound windows it steps the internal render
    /// scale down (x0.95, floor x0.90 of the scale the game was started with, never
    /// below 0.5x absolute, MELIORA_FLOOR overrides as an absolute scale), at most one
    /// step per 5s. A step that would land within 0.01 of the floor snaps to the floor,
    /// so the controller never creates a pointless 0.9025x intermediate value.
    /// After ~8s of clean windows with an unloaded GPU thread it climbs back towards
    /// the starting scale, one step per 8s, but only while the process has at least
    /// 384 MB available (or the platform has not reported a value yet). Slow windows
    /// with an idle GPU thread are logged as CPU-bound and deliberately left alone;
    /// they do not reset the climb.
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
        private const double MedianTolerance = 1.05;     // the sustained rate may sit this far above the target before acting
        private const int FailWindowsToStepDown = 3;     // ~5s of sustained GPU-bound slowness
        private const int PassWindowsToStepUp = 5;       // ~8s of clean windows at the current scale
        private const int DownCooldownMs = 5000;         // max one down-step per 5s
        private const int UpCooldownMs = 8000;           // max one up-step per 8s
        private const float DownStep = 0.95f;            // each down-step, cumulative
        private const float FloorFraction = 0.90f;       // floor relative to the starting scale
        private const float AbsoluteFloor = 0.5f;
        private const float UpStep = 1.06f;
        private const double GpuBusyForDownStep = 0.75;  // GPU thread busy fraction that means GPU-bound
        private const double GpuBusyForUpStep = 0.50;    // don't climb while the GPU thread is busier than this
        private const double GpuIdleForCpuBoundLog = 0.35;
        private const long UpStepMinAvailableBytes = 384L * 1024 * 1024; // don't climb into memory pressure
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

        // Optional absolute floor override (MELIORA_FLOOR, e.g. 0.75); 0 = use FloorFraction.
        private static readonly float _floorOverride = ParseFloorOverride();

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
        /// Meliora: the smallest scale the controller may step down to for a game that
        /// started at <paramref name="startScale"/>. MELIORA_FLOOR overrides the default
        /// fraction with an absolute scale (clamped into [AbsoluteFloor, startScale]).
        /// </summary>
        private static float GetFloorScale(float startScale)
        {
            if (_floorOverride > 0f)
            {
                return Math.Max(AbsoluteFloor, Math.Min(_floorOverride, startScale));
            }

            return Math.Max(AbsoluteFloor, startScale * FloorFraction);
        }

        private static float ParseFloorOverride()
        {
            string raw = Environment.GetEnvironmentVariable("MELIORA_FLOOR");

            if (float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) &&
                value >= AbsoluteFloor && value <= 1f)
            {
                return value;
            }

            return 0f;
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

                Console.WriteLine($"[Meliora] adaptive: start {_startScale:0.00}x, floor {GetFloorScale(_startScale):0.00}x, target {MelioraFramePacing.TargetHz} fps");

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

            // Budget: the pacing target with a small tolerance. The gate is the median,
            // not the 95th percentile: single slow frames (shader compiles, allocations)
            // are not fixable by lowering the resolution. A game that steadily runs
            // slower than the target while the GPU thread is saturated still steps down;
            // a game that steadily runs at its own lower rate with a free GPU thread
            // does not.
            double budgetMs = 1000.0 / MelioraFramePacing.TargetHz * MedianTolerance;

            bool slow = median > budgetMs;
            bool gpuBound = busyFraction >= GpuBusyForDownStep;
            bool periodicLog = _windowCount % LogEveryWindows == 0;

            if (slow && gpuBound)
            {
                // Three consecutive windows like this are the proof that the current
                // scale is insufficient: the GPU thread is saturated and even the median
                // frame is out of budget. Reset the climb streak.
                _failStreak++;
                _passStreak = 0;
            }
            else
            {
                // A slow but CPU-bound window says nothing about the scale, and it must
                // not pre-charge the down-step counter either: otherwise one later
                // GPU-bound window would step down immediately instead of after three
                // consecutive GPU-bound windows.
                _failStreak = 0;

                if (slow)
                {
                    if (busyFraction < GpuIdleForCpuBoundLog && nowMs - _lastCpuBoundLogMs >= 10000)
                    {
                        _lastCpuBoundLogMs = nowMs;

                        Console.WriteLine($"[Meliora] adaptive: slow frames but GPU idle (gpuBusy={(int)(busyFraction * 100)}%), not touching scale (CPU-bound?)");
                    }
                }
                else if (busyFraction < GpuBusyForUpStep)
                {
                    // Count only clean windows with an unloaded GPU thread: a window that
                    // is at the target rate but keeps the GPU thread busy is not proof
                    // that the scale can climb, yet it must not reset the streak either.
                    _passStreak++;
                }
            }

            float floorScale = GetFloorScale(_startScale);
            bool stepped = false;

            if (slow &&
                gpuBound &&
                _failStreak >= FailWindowsToStepDown &&
                nowMs - _lastStepMs >= DownCooldownMs)
            {
                if (_scale > floorScale + 0.01f)
                {
                    float next = Math.Max(floorScale, _scale * DownStep);

                    // Snap to the floor instead of creating a separate 0.9025x scale
                    // whose next step to 0.9000x lives only in the third decimal and
                    // recreates the host textures for a 0.25% linear change.
                    if (next - floorScale < 0.01f)
                    {
                        next = floorScale;
                    }

                    Console.WriteLine($"[Meliora] adaptive: step down median={median:0.0}ms budget={budgetMs:0.0}ms gpuBusy={(int)(busyFraction * 100)}% scale={_scale:0.000}x -> {next:0.000}x");

                    _scale = next;
                    GraphicsConfig.ResScale = _scale;
                    _lastStepMs = nowMs;
                    _failStreak = 0;
                    _passStreak = 0;
                    stepped = true;
                }
                else if (periodicLog)
                {
                    Console.WriteLine($"[Meliora] adaptive: at floor ({floorScale:0.00}x) median={median:0.0}ms budget={budgetMs:0.0}ms gpuBusy={(int)(busyFraction * 100)}%");
                }
            }
            else if (!slow &&
                busyFraction < GpuBusyForUpStep &&
                _passStreak >= PassWindowsToStepUp &&
                nowMs - _lastStepMs >= UpCooldownMs &&
                _scale < _startScale - 0.001f)
            {
                long available = MemoryGovernor.AvailableBytes;

                if (available >= 0 && available < UpStepMinAvailableBytes)
                {
                    // The load has passed, but the process is still close to the
                    // memory limit. Climbing now would add texture memory exactly
                    // when the next allocation is most likely to fail. Stay at the
                    // current scale and try again when memory recovers.
                    if (periodicLog)
                    {
                        Console.WriteLine($"[Meliora] adaptive: step up blocked, available={available / (1024 * 1024)}MB < {UpStepMinAvailableBytes / (1024 * 1024)}MB, scale={_scale:0.00}x");
                    }
                }
                else
                {
                    float next = _scale * UpStep;

                    if (next > _startScale - 0.01f)
                    {
                        // Snap home instead of asymptotically creeping towards it.
                        next = _startScale;
                    }

                    Console.WriteLine($"[Meliora] adaptive: step up median={median:0.0}ms budget={budgetMs:0.0}ms gpuBusy={(int)(busyFraction * 100)}% scale={_scale:0.000}x -> {next:0.000}x");

                    _scale = next;
                    GraphicsConfig.ResScale = _scale;
                    _lastStepMs = nowMs;
                    _passStreak = 0;
                    stepped = true;
                }
            }

            if (periodicLog && !stepped)
            {
                Console.WriteLine($"[Meliora] adaptive: p95={p95:0.0}ms median={median:0.0}ms budget={budgetMs:0.0}ms gpuBusy={(int)(busyFraction * 100)}% scale={_scale:0.00}x");
            }
        }
    }
}
