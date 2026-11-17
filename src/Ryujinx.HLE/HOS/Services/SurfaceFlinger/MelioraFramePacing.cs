using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger
{
    /// <summary>
    /// Meliora: central frame pacing state.
    ///
    /// Replaces the hard-coded 60 FPS composition target with one source of truth read
    /// by both the composer (SurfaceFlinger) and the render loop's status reporting.
    /// The target comes from the MELIORA_TARGET_FPS environment variable (default 60),
    /// clamped to 15-240. It is re-read at every game launch (<see cref="RefreshTargetHz"/>),
    /// so the Target 30 FPS toggle applies to the next game without restarting the app.
    /// </summary>
    public static class MelioraFramePacing
    {
        private const int DefaultTargetHz = 60;
        private const int MinTargetHz = 15;
        private const int MaxTargetHz = 240;

        private static int _targetHz = ReadTargetHzFromEnvironment();

        /// <summary>Composition target in frames per second.</summary>
        public static int TargetHz => Volatile.Read(ref _targetHz);

        /// <summary>Duration of one frame at the current target, in host ticks.</summary>
        public static long TicksPerFrame => Stopwatch.Frequency / Math.Max(TargetHz, 1);

        /// <summary>
        /// Re-reads the target from the environment. Must be called once per game
        /// launch (SurfaceFlinger's constructor): the static initializer above only
        /// runs once per process, so without this a settings change between games
        /// would be picked up only after an app restart.
        /// </summary>
        public static void RefreshTargetHz()
        {
            Volatile.Write(ref _targetHz, ReadTargetHzFromEnvironment());
        }

        private static int ReadTargetHzFromEnvironment()
        {
            string value = Environment.GetEnvironmentVariable("MELIORA_TARGET_FPS");

            if (int.TryParse(value, out int targetHz))
            {
                return Math.Clamp(targetHz, MinTargetHz, MaxTargetHz);
            }

            return DefaultTargetHz;
        }
    }
}
