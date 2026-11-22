using Ryujinx.Common;
using Silk.NET.Vulkan;
using System;
using System.Diagnostics;

namespace Ryujinx.Graphics.Vulkan
{
    static class FenceHelper
    {
        private const ulong DefaultTimeout = 100000000; // 100ms

        public static bool AnySignaled(Vk api, Device device, ReadOnlySpan<Fence> fences, ulong timeout = 0)
        {
            bool signaled = api.WaitForFences(device, (uint)fences.Length, fences, false, timeout) == Result.Success;

            MelioraTelemetry.AddFencePoll(signaled);

            return signaled;
        }

        public static bool AllSignaled(Vk api, Device device, ReadOnlySpan<Fence> fences, ulong timeout = 0)
        {
            bool signaled = api.WaitForFences(device, (uint)fences.Length, fences, true, timeout) == Result.Success;

            MelioraTelemetry.AddFencePoll(signaled);

            return signaled;
        }

        public static void WaitAllIndefinitely(Vk api, Device device, ReadOnlySpan<Fence> fences)
        {
            long melioraStart = Stopwatch.GetTimestamp();

            Result result;

            while ((result = api.WaitForFences(device, (uint)fences.Length, fences, true, DefaultTimeout)) == Result.Timeout)
            {
                // Keep waiting while the fence is not signaled.
                MelioraTelemetry.AddFenceBlockTimeout();
            }

            MelioraTelemetry.AddFenceBlock(Stopwatch.GetTimestamp() - melioraStart);

            result.ThrowOnError();
        }
    }
}
