using Ryujinx.Common.SystemInterop;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Ryujinx.Common
{
    /// <summary>
    /// Snapshot of one emulated memory region, as used by the Meliora detail log.
    /// </summary>
    public readonly struct MelioraGuestMemoryRegion
    {
        /// <summary>Total size of the region in bytes.</summary>
        public ulong TotalBytes { get; }

        /// <summary>Unallocated size of the region in bytes.</summary>
        public ulong FreeBytes { get; }

        /// <summary>Allocated size of the region in bytes.</summary>
        public ulong UsedBytes => TotalBytes - FreeBytes;

        public MelioraGuestMemoryRegion(ulong totalBytes, ulong freeBytes)
        {
            TotalBytes = totalBytes;
            FreeBytes = freeBytes;
        }
    }

    /// <summary>
    /// Physical Vulkan allocator state, gathered on the render thread by the Vulkan backend.
    /// </summary>
    public struct MelioraVulkanSnapshot
    {
        public int BlockCount;
        public long BlockBytes;
        public long UsedBytes;
        public int HostImportCount;
        public long HostImportBytes;
        public int LiveBufferCount;
        public long LiveBufferBytes;
        public int BufferCreatesTotal;
        public int FlushBufferBytes;
    }

    /// <summary>
    /// Meliora: read-only performance and memory telemetry.
    ///
    /// The app requests a report every ten seconds. Emulation threads only bump counters;
    /// the report itself is assembled, sorted and printed on a dedicated background thread.
    /// Every counter is updated with Interlocked, so parallel producers cannot lose an
    /// increment. A frozen slot is copied only after all writers that were active during
    /// the swap have left it.
    /// </summary>
    public static class MelioraTelemetry
    {
        private const int FrameCapacity = 2048;
        private const int PacketCount = 4;
        private const int RegionCount = 4;

        // Request lifecycle. Only one report can be produced at a time.
        private const int StateIdle = 0;
        private const int StateRequested = 1;
        private const int StateCapturingHost = 2;
        private const int StateHostReady = 3;
        private const int StateCapturingVulkan = 4;

        private sealed class Counters
        {
            public readonly float[] FrameIntervals = new float[FrameCapacity];

            public int FrameCount;
            public int FrameDropped;

            public int CpuFrames;
            public long CpuTicksSum;
            public long CpuTicksMax;

            public int PresentFrames;
            public long PresentTicksSum;
            public long PresentTicksMax;

            public int ShaderTranslations;
            public long ShaderTranslateTicksSum;
            public long ShaderTranslateTicksMax;

            public int ProgramCreates;
            public long ProgramCreateTicksSum;
            public long ProgramCreateTicksMax;

            public int ProgramLinkPolls;
            public int ProgramLinkBlocks;
            public int ProgramLinkIncomplete;
            public long ProgramLinkTicksSum;
            public long ProgramLinkTicksMax;

            public int PipelineCacheNull;
            public int PipelineLookupMiss;

            public int FencePolls;
            public int FencePollHits;
            public int FenceBlocks;
            public int FenceBlockTimeouts;
            public long FenceBlockTicksSum;
            public long FenceBlockTicksMax;

            public int CpuWaits;
            public int CpuWaitTimeouts;
            public long CpuWaitTicksSum;
            public long CpuWaitTicksMax;

            public int BufferMigrations;
            public long BufferMigrationBytes;
            public long BufferMigrationTicksSum;
            public long BufferMigrationTicksMax;

            public int BufferFlushes;
            public long BufferFlushBytes;
            public long BufferFlushTicksSum;
            public long BufferFlushTicksMax;

            public int Readbacks;
            public long ReadbackBytes;

            public int VkBlockAllocs;
            public long VkBlockAllocBytes;
            public int VkBlockFrees;
            public long VkBlockFreeBytes;

            /// <summary>
            /// Clears the counters of a slot that is about to become active. The frame array
            /// is left allocated; only its used length matters. Called when no writer is
            /// active on the slot.
            /// </summary>
            public void Reset()
            {
                FrameCount = 0;
                FrameDropped = 0;

                CpuFrames = 0;
                CpuTicksSum = 0;
                CpuTicksMax = 0;

                PresentFrames = 0;
                PresentTicksSum = 0;
                PresentTicksMax = 0;

                ShaderTranslations = 0;
                ShaderTranslateTicksSum = 0;
                ShaderTranslateTicksMax = 0;

                ProgramCreates = 0;
                ProgramCreateTicksSum = 0;
                ProgramCreateTicksMax = 0;

                ProgramLinkPolls = 0;
                ProgramLinkBlocks = 0;
                ProgramLinkIncomplete = 0;
                ProgramLinkTicksSum = 0;
                ProgramLinkTicksMax = 0;

                PipelineCacheNull = 0;
                PipelineLookupMiss = 0;

                FencePolls = 0;
                FencePollHits = 0;
                FenceBlocks = 0;
                FenceBlockTimeouts = 0;
                FenceBlockTicksSum = 0;
                FenceBlockTicksMax = 0;

                CpuWaits = 0;
                CpuWaitTimeouts = 0;
                CpuWaitTicksSum = 0;
                CpuWaitTicksMax = 0;

                BufferMigrations = 0;
                BufferMigrationBytes = 0;
                BufferMigrationTicksSum = 0;
                BufferMigrationTicksMax = 0;

                BufferFlushes = 0;
                BufferFlushBytes = 0;
                BufferFlushTicksSum = 0;
                BufferFlushTicksMax = 0;

                Readbacks = 0;
                ReadbackBytes = 0;

                VkBlockAllocs = 0;
                VkBlockAllocBytes = 0;
                VkBlockFrees = 0;
                VkBlockFreeBytes = 0;
            }

            /// <summary>
            /// Copies a frozen slot. Called only when no writer is active on it, so plain
            /// reads are safe here.
            /// </summary>
            public void CopyFrom(Counters other)
            {
                int count = Math.Min(other.FrameCount, FrameIntervals.Length);

                if (count != 0)
                {
                    Array.Copy(other.FrameIntervals, FrameIntervals, count);
                }

                FrameCount = count;
                FrameDropped = other.FrameDropped;

                CpuFrames = other.CpuFrames;
                CpuTicksSum = other.CpuTicksSum;
                CpuTicksMax = other.CpuTicksMax;

                PresentFrames = other.PresentFrames;
                PresentTicksSum = other.PresentTicksSum;
                PresentTicksMax = other.PresentTicksMax;

                ShaderTranslations = other.ShaderTranslations;
                ShaderTranslateTicksSum = other.ShaderTranslateTicksSum;
                ShaderTranslateTicksMax = other.ShaderTranslateTicksMax;

                ProgramCreates = other.ProgramCreates;
                ProgramCreateTicksSum = other.ProgramCreateTicksSum;
                ProgramCreateTicksMax = other.ProgramCreateTicksMax;

                ProgramLinkPolls = other.ProgramLinkPolls;
                ProgramLinkBlocks = other.ProgramLinkBlocks;
                ProgramLinkIncomplete = other.ProgramLinkIncomplete;
                ProgramLinkTicksSum = other.ProgramLinkTicksSum;
                ProgramLinkTicksMax = other.ProgramLinkTicksMax;

                PipelineCacheNull = other.PipelineCacheNull;
                PipelineLookupMiss = other.PipelineLookupMiss;

                FencePolls = other.FencePolls;
                FencePollHits = other.FencePollHits;
                FenceBlocks = other.FenceBlocks;
                FenceBlockTimeouts = other.FenceBlockTimeouts;
                FenceBlockTicksSum = other.FenceBlockTicksSum;
                FenceBlockTicksMax = other.FenceBlockTicksMax;

                CpuWaits = other.CpuWaits;
                CpuWaitTimeouts = other.CpuWaitTimeouts;
                CpuWaitTicksSum = other.CpuWaitTicksSum;
                CpuWaitTicksMax = other.CpuWaitTicksMax;

                BufferMigrations = other.BufferMigrations;
                BufferMigrationBytes = other.BufferMigrationBytes;
                BufferMigrationTicksSum = other.BufferMigrationTicksSum;
                BufferMigrationTicksMax = other.BufferMigrationTicksMax;

                BufferFlushes = other.BufferFlushes;
                BufferFlushBytes = other.BufferFlushBytes;
                BufferFlushTicksSum = other.BufferFlushTicksSum;
                BufferFlushTicksMax = other.BufferFlushTicksMax;

                Readbacks = other.Readbacks;
                ReadbackBytes = other.ReadbackBytes;

                VkBlockAllocs = other.VkBlockAllocs;
                VkBlockAllocBytes = other.VkBlockAllocBytes;
                VkBlockFrees = other.VkBlockFrees;
                VkBlockFreeBytes = other.VkBlockFreeBytes;
            }
        }

        private sealed class FlushPacket
        {
            public readonly Counters Counters = new();
            public readonly MelioraGuestMemoryRegion[] GuestRegions = new MelioraGuestMemoryRegion[RegionCount];

            public long TexCacheBytes;
            public long AvailableBytes;
            public int BufferEntries;
            public long BufferBytes;
            public int MultiRangeEntries;
            public int GraphicsPrograms;
            public int ComputePrograms;
            public MelioraVulkanSnapshot Vulkan;
        }

        private static readonly Counters[] _slots = { new Counters(), new Counters() };
        private static readonly int[] _slotWriters = new int[2];
        private static int _activeSlot;

        private static int _requestState = StateIdle;
        private static long _pendingTexCacheBytes;

        private static readonly MelioraGuestMemoryRegion[] _hostGuestRegions = new MelioraGuestMemoryRegion[RegionCount];
        private static int _hostBufferEntries;
        private static long _hostBufferBytes;
        private static int _hostMultiRangeEntries;
        private static int _hostGraphicsPrograms;
        private static int _hostComputePrograms;
        private static long _hostAvailableBytes;

        private static MelioraVulkanSnapshot _vulkanSnapshot;
        private static int _frozenSlot = -1;

        private static readonly object _packetLock = new();
        private static readonly FlushPacket[] _packets = CreatePackets();
        private static readonly int[] _freePackets = { 0, 1, 2, 3 };
        private static readonly int[] _readyPackets = new int[PacketCount];
        private static int _freePacketCount = PacketCount;
        private static int _readyHead;
        private static int _readyTail;
        private static int _readyCount;
        private static readonly AutoResetEvent _packetReady = new(false);

        private static Thread _formatterThread;
        private static readonly object _formatterLock = new();

        // Accessed only by the formatter thread.
        private static readonly StringBuilder _lineBuilder = new(1024);
        private static float[] _sortScratch;

        /// <summary>
        /// True when the app asked for a report and the render loop still has to sample
        /// guest memory, the buffer cache and the shader caches.
        /// </summary>
        public static bool IsHostSnapshotRequested => Volatile.Read(ref _requestState) == StateRequested;

        /// <summary>
        /// True when host data is ready and the Vulkan backend still has to sample the
        /// physical allocators on its own thread.
        /// </summary>
        public static bool IsVulkanSnapshotRequested => Volatile.Read(ref _requestState) == StateHostReady;

        /// <summary>
        /// Meliora: asks for a new detail report. Called from the app every ten seconds,
        /// right after the existing "[Meliora] mem footprint=..." line.
        /// </summary>
        /// <param name="texCacheBytes">Current texture cache size, as measured by the app</param>
        public static void RequestFlush(long texCacheBytes)
        {
            EnsureFormatter();

            Volatile.Write(ref _pendingTexCacheBytes, texCacheBytes);

            Interlocked.CompareExchange(ref _requestState, StateRequested, StateIdle);
        }

        /// <summary>
        /// Clears the counters for a new emulation session. Called from the render loop
        /// before the first frame, when no counter writer is active.
        /// </summary>
        public static void ResetSession()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i].Reset();
            }

            Volatile.Write(ref _activeSlot, 0);
            Volatile.Write(ref _frozenSlot, -1);
            Volatile.Write(ref _requestState, StateIdle);
        }

        /// <summary>
        /// Samples guest memory and the GPU caches. Must run on the GPU main thread,
        /// which owns the buffer and shader caches.
        /// </summary>
        public static void CaptureHostSnapshot(
            ReadOnlySpan<MelioraGuestMemoryRegion> guestRegions,
            int bufferEntries,
            long bufferBytes,
            int multiRangeEntries,
            int graphicsPrograms,
            int computePrograms,
            long availableBytes)
        {
            if (Interlocked.CompareExchange(ref _requestState, StateCapturingHost, StateRequested) != StateRequested)
            {
                return;
            }

            int regionCount = Math.Min(guestRegions.Length, RegionCount);

            for (int i = 0; i < regionCount; i++)
            {
                _hostGuestRegions[i] = guestRegions[i];
            }

            for (int i = regionCount; i < RegionCount; i++)
            {
                _hostGuestRegions[i] = default;
            }

            _hostBufferEntries = bufferEntries;
            _hostBufferBytes = bufferBytes;
            _hostMultiRangeEntries = multiRangeEntries;
            _hostGraphicsPrograms = graphicsPrograms;
            _hostComputePrograms = computePrograms;
            _hostAvailableBytes = availableBytes;

            Volatile.Write(ref _requestState, StateHostReady);
        }

        /// <summary>
        /// Samples the physical Vulkan allocators and closes the report. Must run on the
        /// backend render thread, immediately after the per-frame sync cleanup.
        /// </summary>
        public static void CaptureVulkanSnapshot(in MelioraVulkanSnapshot snapshot)
        {
            if (Interlocked.CompareExchange(ref _requestState, StateCapturingVulkan, StateHostReady) != StateHostReady)
            {
                return;
            }

            _vulkanSnapshot = snapshot;

            FreezeSlots();
            TryPublishFrozenSlot();
        }

        /// <summary>
        /// Completes a deferred publication once the writers of the frozen slot are gone.
        /// Called every frame from the backend thread; costs one volatile read in the
        /// common case.
        /// </summary>
        public static void TryPublishFrozenSlot()
        {
            int slot = Volatile.Read(ref _frozenSlot);

            if (slot < 0 || Volatile.Read(ref _slotWriters[slot]) != 0)
            {
                return;
            }

            FlushPacket packet;

            lock (_packetLock)
            {
                if (_freePacketCount == 0)
                {
                    // The formatter is far behind (should not happen at a ten second
                    // cadence). Drop this report rather than overwrite a packet in use.
                    Volatile.Write(ref _frozenSlot, -1);
                    Volatile.Write(ref _requestState, StateIdle);
                    return;
                }

                int packetIndex = _freePackets[--_freePacketCount];
                packet = _packets[packetIndex];

                packet.Counters.CopyFrom(_slots[slot]);
                Array.Copy(_hostGuestRegions, packet.GuestRegions, RegionCount);

                packet.TexCacheBytes = Volatile.Read(ref _pendingTexCacheBytes);
                packet.AvailableBytes = _hostAvailableBytes;
                packet.BufferEntries = _hostBufferEntries;
                packet.BufferBytes = _hostBufferBytes;
                packet.MultiRangeEntries = _hostMultiRangeEntries;
                packet.GraphicsPrograms = _hostGraphicsPrograms;
                packet.ComputePrograms = _hostComputePrograms;
                packet.Vulkan = _vulkanSnapshot;

                _readyPackets[_readyTail] = packetIndex;
                _readyTail = (_readyTail + 1) % PacketCount;
                _readyCount++;
            }

            Volatile.Write(ref _frozenSlot, -1);
            Volatile.Write(ref _requestState, StateIdle);

            _packetReady.Set();
        }

        private static void FreezeSlots()
        {
            int oldSlot = Volatile.Read(ref _activeSlot);
            int newSlot = oldSlot ^ 1;

            // The inactive slot has no writers: every writer that targeted it left before
            // the previous report was published. Reset it before publishing the swap.
            if (Volatile.Read(ref _slotWriters[newSlot]) != 0)
            {
                // Extremely unlikely; retry on the next PreFrame instead of spinning on
                // the render thread.
                Volatile.Write(ref _requestState, StateHostReady);
                return;
            }

            _slots[newSlot].Reset();

            Volatile.Write(ref _activeSlot, newSlot);

            // Writers that read the old slot before the swap are still counted here. The
            // copy is deferred to TryPublishFrozenSlot once they all leave.
            Volatile.Write(ref _frozenSlot, oldSlot);
        }

        private static FlushPacket[] CreatePackets()
        {
            FlushPacket[] packets = new FlushPacket[PacketCount];

            for (int i = 0; i < packets.Length; i++)
            {
                packets[i] = new FlushPacket();
            }

            return packets;
        }

        private static void EnsureFormatter()
        {
            if (Volatile.Read(ref _formatterThread) != null)
            {
                return;
            }

            lock (_formatterLock)
            {
                if (_formatterThread != null)
                {
                    return;
                }

                _formatterThread = new Thread(FormatterLoop)
                {
                    Name = "Meliora.Telemetry",
                    IsBackground = true,
                };

                _formatterThread.Start();
            }
        }

        private static void FormatterLoop()
        {
            ThreadQoS.SetCurrentThreadQoS(QoSRole.AsyncWork);

            while (true)
            {
                int packetIndex = -1;

                lock (_packetLock)
                {
                    if (_readyCount != 0)
                    {
                        packetIndex = _readyPackets[_readyHead];
                        _readyHead = (_readyHead + 1) % PacketCount;
                        _readyCount--;
                    }
                }

                if (packetIndex < 0)
                {
                    _packetReady.WaitOne();
                    continue;
                }

                Format(_packets[packetIndex]);

                lock (_packetLock)
                {
                    _freePackets[_freePacketCount++] = packetIndex;
                }
            }
        }

        private static void Format(FlushPacket packet)
        {
            _sortScratch ??= new float[FrameCapacity];

            Counters counters = packet.Counters;
            int frameCount = counters.FrameCount;

            if (frameCount != 0)
            {
                Array.Copy(counters.FrameIntervals, _sortScratch, frameCount);
                Array.Sort(_sortScratch, 0, frameCount);
            }

            float p50 = Percentile(_sortScratch, frameCount, 0.50);
            float p95 = Percentile(_sortScratch, frameCount, 0.95);
            float p99 = Percentile(_sortScratch, frameCount, 0.99);
            float max = frameCount != 0 ? _sortScratch[frameCount - 1] : 0;

            double cpuAverage = counters.CpuFrames != 0
                ? TicksToMs(counters.CpuTicksSum) / counters.CpuFrames
                : 0;
            double presentAverage = counters.PresentFrames != 0
                ? TicksToMs(counters.PresentTicksSum) / counters.PresentFrames
                : 0;

            _lineBuilder.Clear();

            _lineBuilder.Append("[Meliora] mem-detail guest ");
            AppendGuestRegion("app", packet.GuestRegions[0]);
            AppendGuestRegion("applet", packet.GuestRegions[1]);
            AppendGuestRegion("svc", packet.GuestRegions[2]);
            AppendGuestRegion("nv", packet.GuestRegions[3]);

            _lineBuilder.Append("| owners tex=");
            AppendMiB(packet.TexCacheBytes);
            _lineBuilder.Append(" bufEntries=").Append(packet.BufferEntries);
            _lineBuilder.Append(" bufMem=");
            AppendMiB(packet.BufferBytes);
            _lineBuilder.Append(" multiEntries=").Append(packet.MultiRangeEntries);
            _lineBuilder.Append(" shaderG=").Append(packet.GraphicsPrograms);
            _lineBuilder.Append(" shaderC=").Append(packet.ComputePrograms);
            _lineBuilder.Append(" avail=");

            if (packet.AvailableBytes < 0)
            {
                _lineBuilder.Append("n/a");
            }
            else
            {
                AppendMiB(packet.AvailableBytes, "MB");
            }

            _lineBuilder.Append(" | physical vkBlocks=").Append(packet.Vulkan.BlockCount);
            _lineBuilder.Append(" vkMem=");
            AppendMiB(packet.Vulkan.BlockBytes);
            _lineBuilder.Append(" vkUsed=");
            AppendMiB(packet.Vulkan.UsedBytes);
            _lineBuilder.Append(" liveBufs=").Append(packet.Vulkan.LiveBufferCount);
            _lineBuilder.Append(" liveBufMem=");
            AppendMiB(packet.Vulkan.LiveBufferBytes);
            _lineBuilder.Append(" bufCreatesTotal=").Append(packet.Vulkan.BufferCreatesTotal);
            _lineBuilder.Append(" hostImports=").Append(packet.Vulkan.HostImportCount);
            _lineBuilder.Append(" hostImportMem=");
            AppendMiB(packet.Vulkan.HostImportBytes);
            _lineBuilder.Append(" flushBuf=");
            AppendMiB(packet.Vulkan.FlushBufferBytes);

            Console.WriteLine(_lineBuilder.ToString());

            _lineBuilder.Clear();

            _lineBuilder.Append("[Meliora] perf-detail frames=").Append(frameCount);
            _lineBuilder.Append(" drop=").Append(counters.FrameDropped);
            AppendMs(" p50", p50);
            AppendMs(" p95", p95);
            AppendMs(" p99", p99);
            AppendMs(" max", max);
            AppendMs(" cpuAvg", cpuAverage);
            AppendMs(" cpuMax", TicksToMs(counters.CpuTicksMax));
            AppendMs(" presentAvg", presentAverage);
            AppendMs(" presentMax", TicksToMs(counters.PresentTicksMax));

            _lineBuilder.Append(" | sync fencePoll=").Append(counters.FencePolls);
            _lineBuilder.Append(" fenceHit=").Append(counters.FencePollHits);
            _lineBuilder.Append(" fenceBlock=").Append(counters.FenceBlocks);
            AppendMs(" fenceBlockAvg", counters.FenceBlocks != 0
                ? TicksToMs(counters.FenceBlockTicksSum) / counters.FenceBlocks
                : 0);
            AppendMs(" fenceBlockMax", TicksToMs(counters.FenceBlockTicksMax));
            _lineBuilder.Append(" fenceTimeouts=").Append(counters.FenceBlockTimeouts);
            _lineBuilder.Append(" cpuWait=").Append(counters.CpuWaits);
            AppendMs(" cpuWaitAvg", counters.CpuWaits != 0
                ? TicksToMs(counters.CpuWaitTicksSum) / counters.CpuWaits
                : 0);
            AppendMs(" cpuWaitMax", TicksToMs(counters.CpuWaitTicksMax));
            _lineBuilder.Append(" cpuWaitTimeouts=").Append(counters.CpuWaitTimeouts);

            _lineBuilder.Append(" | buf migrate=").Append(counters.BufferMigrations);
            AppendMs(" migrateAvg", counters.BufferMigrations != 0
                ? TicksToMs(counters.BufferMigrationTicksSum) / counters.BufferMigrations
                : 0);
            AppendMs(" migrateMax", TicksToMs(counters.BufferMigrationTicksMax));
            _lineBuilder.Append(" migrateMem=");
            AppendMiB(counters.BufferMigrationBytes);
            _lineBuilder.Append(" flush=").Append(counters.BufferFlushes);
            AppendMs(" flushAvg", counters.BufferFlushes != 0
                ? TicksToMs(counters.BufferFlushTicksSum) / counters.BufferFlushes
                : 0);
            AppendMs(" flushMax", TicksToMs(counters.BufferFlushTicksMax));
            _lineBuilder.Append(" flushMem=");
            AppendMiB(counters.BufferFlushBytes);
            _lineBuilder.Append(" readback=").Append(counters.Readbacks);
            _lineBuilder.Append(" readbackMem=");
            AppendMiB(counters.ReadbackBytes);

            _lineBuilder.Append(" | vk blockAlloc=").Append(counters.VkBlockAllocs);
            _lineBuilder.Append(" blockAllocMem=");
            AppendMiB(counters.VkBlockAllocBytes);
            _lineBuilder.Append(" blockFree=").Append(counters.VkBlockFrees);
            _lineBuilder.Append(" blockFreeMem=");
            AppendMiB(counters.VkBlockFreeBytes);

            _lineBuilder.Append(" | shader translate=").Append(counters.ShaderTranslations);
            AppendMs(" translateAvg", counters.ShaderTranslations != 0
                ? TicksToMs(counters.ShaderTranslateTicksSum) / counters.ShaderTranslations
                : 0);
            AppendMs(" translateMax", TicksToMs(counters.ShaderTranslateTicksMax));
            _lineBuilder.Append(" create=").Append(counters.ProgramCreates);
            AppendMs(" createAvg", counters.ProgramCreates != 0
                ? TicksToMs(counters.ProgramCreateTicksSum) / counters.ProgramCreates
                : 0);
            AppendMs(" createMax", TicksToMs(counters.ProgramCreateTicksMax));
            _lineBuilder.Append(" linkPoll=").Append(counters.ProgramLinkPolls);
            _lineBuilder.Append(" linkBlock=").Append(counters.ProgramLinkBlocks);
            AppendMs(" linkAvg", counters.ProgramLinkBlocks != 0
                ? TicksToMs(counters.ProgramLinkTicksSum) / counters.ProgramLinkBlocks
                : 0);
            AppendMs(" linkMax", TicksToMs(counters.ProgramLinkTicksMax));
            _lineBuilder.Append(" linkIncomplete=").Append(counters.ProgramLinkIncomplete);
            _lineBuilder.Append(" pipelineCacheNull=").Append(counters.PipelineCacheNull);
            _lineBuilder.Append(" pipelineLookupMiss=").Append(counters.PipelineLookupMiss);

            Console.WriteLine(_lineBuilder.ToString());
        }

        private static void AppendGuestRegion(string name, in MelioraGuestMemoryRegion region)
        {
            _lineBuilder.Append(name).Append('=');
            AppendRawMiB(region.UsedBytes);
            _lineBuilder.Append('/');
            AppendRawMiB(region.FreeBytes);
            _lineBuilder.Append("MiB ");
        }

        private static void AppendMiB(long bytes, string unit = "MiB")
        {
            AppendRawMiB(bytes);
            _lineBuilder.Append(unit);
        }

        private static void AppendRawMiB(ulong bytes)
        {
            _lineBuilder.Append(bytes / (1024UL * 1024UL));
        }

        private static void AppendRawMiB(long bytes)
        {
            _lineBuilder.Append(bytes / (1024L * 1024L));
        }

        private static void AppendMs(string name, double milliseconds)
        {
            _lineBuilder.Append(name).Append('=');
            _lineBuilder.Append(milliseconds.ToString("0.0", CultureInfo.InvariantCulture));
            _lineBuilder.Append("ms");
        }

        private static double TicksToMs(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static float Percentile(float[] sorted, int count, double percentile)
        {
            if (count == 0)
            {
                return 0;
            }

            int index = (int)((count - 1) * percentile + 0.5);

            if (index >= count)
            {
                index = count - 1;
            }

            return sorted[index];
        }

        private struct SlotWriter : IDisposable
        {
            private int _slot;

            public Counters Counters;

            public SlotWriter()
            {
                _slot = 0;
                Counters = null;

                while (true)
                {
                    int slot = Volatile.Read(ref _activeSlot);

                    Interlocked.Increment(ref _slotWriters[slot]);

                    if (Volatile.Read(ref _activeSlot) == slot)
                    {
                        _slot = slot;
                        Counters = _slots[slot];
                        return;
                    }

                    Interlocked.Decrement(ref _slotWriters[slot]);
                }
            }

            public void Dispose()
            {
                Interlocked.Decrement(ref _slotWriters[_slot]);
            }
        }

        private static void UpdateMax(ref long location, long value)
        {
            long current = Volatile.Read(ref location);

            while (value > current)
            {
                long previous = Interlocked.CompareExchange(ref location, value, current);

                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }

        /// <summary>
        /// Records one ProcessFrame call and how long the CPU spent on it. Only the render
        /// loop calls this, so the frame interval ring itself needs no extra locking.
        /// </summary>
        public static void AddCpuFrame(long ticks)
        {
            if (ticks <= 0)
            {
                return;
            }

            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.CpuFrames);
            Interlocked.Add(ref counters.CpuTicksSum, ticks);
            UpdateMax(ref counters.CpuTicksMax, ticks);
        }

        /// <summary>
        /// Records one presented frame and the interval from the previous present.
        /// </summary>
        public static void AddPresent(long ticks, long intervalTicks)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.PresentFrames);
            Interlocked.Add(ref counters.PresentTicksSum, ticks);
            UpdateMax(ref counters.PresentTicksMax, ticks);

            if (intervalTicks > 0)
            {
                if (counters.FrameCount < FrameCapacity)
                {
                    counters.FrameIntervals[counters.FrameCount++] = (float)TicksToMs(intervalTicks);
                }
                else
                {
                    Interlocked.Increment(ref counters.FrameDropped);
                }
            }
        }

        public static void AddShaderTranslate(long ticks)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.ShaderTranslations);
            Interlocked.Add(ref counters.ShaderTranslateTicksSum, ticks);
            UpdateMax(ref counters.ShaderTranslateTicksMax, ticks);
        }

        public static void AddProgramCreate(long ticks)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.ProgramCreates);
            Interlocked.Add(ref counters.ProgramCreateTicksSum, ticks);
            UpdateMax(ref counters.ProgramCreateTicksMax, ticks);
        }

        public static void AddProgramLinkPoll()
        {
            using SlotWriter writer = new();
            Interlocked.Increment(ref writer.Counters.ProgramLinkPolls);
        }

        public static void AddProgramLink(long ticks, bool incomplete)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.ProgramLinkBlocks);

            if (incomplete)
            {
                Interlocked.Increment(ref counters.ProgramLinkIncomplete);
            }

            Interlocked.Add(ref counters.ProgramLinkTicksSum, ticks);
            UpdateMax(ref counters.ProgramLinkTicksMax, ticks);
        }

        public static void AddPipelineCacheNull()
        {
            using SlotWriter writer = new();
            Interlocked.Increment(ref writer.Counters.PipelineCacheNull);
        }

        public static void AddPipelineLookupMiss()
        {
            using SlotWriter writer = new();
            Interlocked.Increment(ref writer.Counters.PipelineLookupMiss);
        }

        public static void AddFencePoll(bool signaled)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.FencePolls);

            if (signaled)
            {
                Interlocked.Increment(ref counters.FencePollHits);
            }
        }

        public static void AddFenceBlock(long ticks)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.FenceBlocks);
            Interlocked.Add(ref counters.FenceBlockTicksSum, ticks);
            UpdateMax(ref counters.FenceBlockTicksMax, ticks);
        }

        public static void AddFenceBlockTimeout()
        {
            using SlotWriter writer = new();
            Interlocked.Increment(ref writer.Counters.FenceBlockTimeouts);
        }

        public static void AddCpuWait(long ticks, bool timedOut)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.CpuWaits);

            if (timedOut)
            {
                Interlocked.Increment(ref counters.CpuWaitTimeouts);
            }

            Interlocked.Add(ref counters.CpuWaitTicksSum, ticks);
            UpdateMax(ref counters.CpuWaitTicksMax, ticks);
        }

        public static void AddBufferMigration(long ticks, long bytes)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.BufferMigrations);
            Interlocked.Add(ref counters.BufferMigrationBytes, bytes);
            Interlocked.Add(ref counters.BufferMigrationTicksSum, ticks);
            UpdateMax(ref counters.BufferMigrationTicksMax, ticks);
        }

        public static void AddBufferFlush(long ticks, long bytes)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.BufferFlushes);
            Interlocked.Add(ref counters.BufferFlushBytes, bytes);
            Interlocked.Add(ref counters.BufferFlushTicksSum, ticks);
            UpdateMax(ref counters.BufferFlushTicksMax, ticks);
        }

        public static void AddReadback(long bytes)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.Readbacks);
            Interlocked.Add(ref counters.ReadbackBytes, bytes);
        }

        public static void AddVkBlockAlloc(long bytes)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.VkBlockAllocs);
            Interlocked.Add(ref counters.VkBlockAllocBytes, bytes);
        }

        public static void AddVkBlockFree(long bytes)
        {
            using SlotWriter writer = new();
            Counters counters = writer.Counters;

            Interlocked.Increment(ref counters.VkBlockFrees);
            Interlocked.Add(ref counters.VkBlockFreeBytes, bytes);
        }
    }
}
