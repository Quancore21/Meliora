using Ryujinx.Common;
using Silk.NET.Vulkan;
using System;
using System.Diagnostics;

namespace Ryujinx.Graphics.Vulkan
{
    static class FenceHelper
    {
        private const ulong DefaultTimeout = 100000000; // 100ms

        /// <summary>
        /// Anything other than a normal (not signaled) poll outcome. Result.Timeout with
        /// a zero timeout is the normal "not signaled yet" result, not an error.
        /// </summary>
        private static bool IsFenceError(Result result)
        {
            return result != Result.Success && result != Result.Timeout;
        }

        public static bool AnySignaled(Vk api, Device device, ReadOnlySpan<Fence> fences, ulong timeout = 0)
        {
            if (timeout == 0)
            {
                Result result = api.WaitForFences(device, (uint)fences.Length, fences, false, timeout);

                MelioraTelemetry.AddFencePoll(result == Result.Success, IsFenceError(result));

                return result == Result.Success;
            }

            long melioraStart = Stopwatch.GetTimestamp();
            Result timedResult = api.WaitForFences(device, (uint)fences.Length, fences, false, timeout);

            MelioraTelemetry.AddFenceTimed(
                Stopwatch.GetTimestamp() - melioraStart,
                timedResult == Result.Success,
                timedResult == Result.Timeout,
                IsFenceError(timedResult));

            return timedResult == Result.Success;
        }

        public static bool AllSignaled(Vk api, Device device, ReadOnlySpan<Fence> fences, ulong timeout = 0)
        {
            if (timeout == 0)
            {
                Result result = api.WaitForFences(device, (uint)fences.Length, fences, true, timeout);

                MelioraTelemetry.AddFencePoll(result == Result.Success, IsFenceError(result));

                return result == Result.Success;
            }

            long melioraStart = Stopwatch.GetTimestamp();
            Result timedResult = api.WaitForFences(device, (uint)fences.Length, fences, true, timeout);

            MelioraTelemetry.AddFenceTimed(
                Stopwatch.GetTimestamp() - melioraStart,
                timedResult == Result.Success,
                timedResult == Result.Timeout,
                IsFenceError(timedResult));

            return timedResult == Result.Success;
        }

        public static void WaitAllIndefinitely(Vk api, Device device, ReadOnlySpan<Fence> fences)
        {
            long melioraStart = Stopwatch.GetTimestamp();

            Result result;

            while ((result = api.WaitForFences(device, (uint)fences.Length, fences, true, DefaultTimeout)) == Result.Timeout)
            {
                // Keep waiting while the fence is not signaled.
                //
                // Meliora: each Timeout here is one internal 100 ms iteration, not a
                // final failure. The loop only leaves on Success or a real error.
                MelioraTelemetry.AddFenceBlockTimeout();
            }

            MelioraTelemetry.AddFenceBlock(Stopwatch.GetTimestamp() - melioraStart);

            result.ThrowOnError();
        }
    }
}
