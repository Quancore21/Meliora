using Ryujinx.Common.Logging;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// Meliora: reports broken readback states (a readback that would use a dead or null
    /// Vulkan resource). Normal readbacks never reach this, so the log stays quiet even
    /// with the thousands of readbacks per second a game can produce.
    /// Lines are throttled because a disposed texture can trigger many failing flushes in
    /// a row when the guest keeps writing to its tracked memory range.
    /// </summary>
    internal static class MelioraReadbackGuard
    {
        private static readonly long _logIntervalTicks = 5L * Stopwatch.Frequency;

        private static long _lastLogTicks;

        /// <summary>
        /// Logs one broken readback state, at most once every five seconds.
        /// </summary>
        /// <param name="caller">Call site, used to identify the failing path in the log</param>
        /// <param name="details">Handles, sizes and state of the broken readback</param>
        public static void LogBrokenState(string caller, string details)
        {
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastLogTicks);

            if (now - last < _logIntervalTicks)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastLogTicks, now, last) != last)
            {
                return;
            }

            Logger.Error?.Print(LogClass.Gpu, $"Broken readback state ({caller}): {details}");
        }
    }
}
