using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    /// <summary>
    /// Shares the memory situation of the host process with the GPU caches.
    /// The platform layer (the iOS app) reports how much memory the OS still lets the process use,
    /// and the caches shrink early instead of using a fixed budget until the OS terminates the process.
    /// </summary>
    public static class MemoryGovernor
    {
        private const long DefaultHeadroomBytes = 768L * 1024 * 1024;
        private const long MinHeadroomBytes = 256L * 1024 * 1024;
        private const long MaxHeadroomBytes = 2048L * 1024 * 1024;
        private const long LogIntervalMs = 5000;

        private static int _enabled;
        private static int _trimRequested;
        private static long _availableBytes = -1;
        private static long _headroomBytes = DefaultHeadroomBytes;
        private static long _sizeAtSample = -1;
        private static long _lastLogTick;
        private static long _lastTextureCacheSize;

        /// <summary>
        /// Enables or disables the governor and sets how much memory must stay free.
        /// </summary>
        /// <param name="enabled">True to enable the governor</param>
        /// <param name="headroomBytes">Amount of memory, in bytes, that should stay available to the process</param>
        public static void Configure(bool enabled, long headroomBytes)
        {
            if (headroomBytes <= 0)
            {
                headroomBytes = DefaultHeadroomBytes;
            }

            headroomBytes = Math.Clamp(headroomBytes, MinHeadroomBytes, MaxHeadroomBytes);

            Volatile.Write(ref _headroomBytes, headroomBytes);
            Volatile.Write(ref _sizeAtSample, -1);
            Volatile.Write(ref _availableBytes, -1);
            Volatile.Write(ref _trimRequested, 0);
            Volatile.Write(ref _enabled, enabled ? 1 : 0);

            Logger.Notice.Print(LogClass.Gpu, $"Memory governor: {(enabled ? "enabled" : "disabled")}, headroom {headroomBytes / (1024 * 1024)} MiB");
        }

        /// <summary>
        /// Reports how much memory the OS still allows the process to allocate.
        /// </summary>
        /// <param name="availableBytes">Available memory in bytes, or a value below zero if unknown</param>
        public static void ReportAvailableMemory(long availableBytes)
        {
            // The size of the cache at the time of this sample is captured by the next cache call.
            Volatile.Write(ref _sizeAtSample, -1);
            Volatile.Write(ref _availableBytes, availableBytes);
        }

        /// <summary>
        /// Asks the caches to shrink as soon as possible (for example after a low memory warning)
        /// and suggests a managed heap collection in the background.
        /// </summary>
        public static void RequestEmergencyTrim()
        {
            if (Volatile.Read(ref _enabled) == 0)
            {
                return;
            }

            Volatile.Write(ref _trimRequested, 1);

            ThreadPool.QueueUserWorkItem(static _ =>
            {
                try
                {
                    // A blocking, compacting collection (especially of the large object
                    // heap) freezes emulation for hundreds of milliseconds. A background
                    // optimized collection is enough: the texture cache shrink requested
                    // above is what actually returns memory to the OS.
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
                }
                catch (Exception)
                {
                    // A failed optional collection must never affect emulation.
                }
            });

            Logger.Warning?.Print(LogClass.Gpu, "Memory governor: emergency trim requested");
        }

        /// <summary>
        /// Meliora: last known total size of the texture cache, for memory telemetry
        /// (the iOS app reports it in its periodic memory log).
        /// </summary>
        public static long LastTextureCacheSize => Volatile.Read(ref _lastTextureCacheSize);

        /// <summary>
        /// Meliora: last value reported by the platform, in bytes, or a negative value
        /// when the platform has not reported memory yet or the governor is disabled.
        /// </summary>
        public static long AvailableBytes => Volatile.Read(ref _availableBytes);

        /// <summary>
        /// Gets the maximum size the texture cache may currently have.
        /// This is called very often, so it only does a few memory reads and some arithmetic.
        /// </summary>
        /// <param name="constrainedMax">Conservative fixed budget, used while the governor has no data</param>
        /// <param name="unconstrainedMax">Budget used when the governor reports that enough memory is available</param>
        /// <param name="currentSize">Current size of the cache in bytes</param>
        /// <param name="floor">Smallest size the cache is allowed to be shrunk to</param>
        /// <returns>The maximum cache size in bytes</returns>
        internal static ulong GetTextureCacheLimit(ulong constrainedMax, ulong unconstrainedMax, ulong currentSize, ulong floor)
        {
            // The cache reports its size here on every check; keep it for telemetry.
            Volatile.Write(ref _lastTextureCacheSize, (long)currentSize);

            if (Volatile.Read(ref _enabled) == 0)
            {
                return constrainedMax;
            }

            long available = Volatile.Read(ref _availableBytes);

            if (available < 0)
            {
                // No data from the platform yet, stay conservative.
                return constrainedMax;
            }

            if (Volatile.Read(ref _trimRequested) != 0 && Interlocked.Exchange(ref _trimRequested, 0) != 0)
            {
                // A real emergency trim may go below the normal floor. The normal
                // floor keeps the cache useful; the emergency floor (half of it)
                // returns memory when iOS is already warning about pressure.
                ulong emergencyFloor = floor / 2;

                return Math.Min(unconstrainedMax, Math.Max(currentSize / 2, emergencyFloor));
            }

            long headroom = Volatile.Read(ref _headroomBytes);
            long size = (long)currentSize;
            long sizeAtSample = Volatile.Read(ref _sizeAtSample);

            if (sizeAtSample < 0)
            {
                // First look at the cache since the last sample: this is the size the sample belongs to.
                sizeAtSample = size;
                Volatile.Write(ref _sizeAtSample, size);
            }

            // How much memory still has to be given back to restore the headroom.
            // Memory used by textures added since the sample counts against the headroom,
            // memory freed by evictions since the sample counts in its favour.
            long deficit = headroom - available + (size - sizeAtSample);

            if (deficit <= 0)
            {
                return unconstrainedMax;
            }

            ulong target = currentSize > (ulong)deficit ? currentSize - (ulong)deficit : 0UL;

            return Math.Min(unconstrainedMax, Math.Max(target, floor));
        }

        /// <summary>
        /// Logs a cache shrink, at most once every few seconds.
        /// </summary>
        /// <param name="before">Cache size before shrinking</param>
        /// <param name="after">Cache size after shrinking</param>
        internal static void LogShrink(ulong before, ulong after)
        {
            if (Volatile.Read(ref _enabled) == 0)
            {
                return;
            }

            long now = Environment.TickCount64;

            if (now - Volatile.Read(ref _lastLogTick) < LogIntervalMs)
            {
                return;
            }

            Volatile.Write(ref _lastLogTick, now);

            Logger.Info?.Print(LogClass.Gpu, $"Memory governor: texture cache {before / (1024 * 1024)} MiB -> {after / (1024 * 1024)} MiB (available {Volatile.Read(ref _availableBytes) / (1024 * 1024)} MiB)");
        }
    }
}
