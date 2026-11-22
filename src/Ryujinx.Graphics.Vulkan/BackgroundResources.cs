using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    class BackgroundResource : IDisposable
    {
        private readonly VulkanRenderer _gd;
        private Device _device;

        private CommandBufferPool _pool;
        private PersistentFlushBuffer _flushBuffer;

        public BackgroundResource(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
        }

        public CommandBufferPool GetPool()
        {
            if (_pool == null)
            {
                bool useBackground = _gd.BackgroundQueue.Handle != 0 && _gd.Vendor != Vendor.Amd;
                Queue queue = useBackground ? _gd.BackgroundQueue : _gd.Queue;
                object queueLock = useBackground ? _gd.BackgroundQueueLock : _gd.QueueLock;

                lock (queueLock)
                {
                    _pool = new CommandBufferPool(
                        _gd.Api,
                        _device,
                        queue,
                        queueLock,
                        _gd.QueueFamilyIndex,
                        _gd.IsQualcommProprietary,
                        isLight: true);
                }
            }

            return _pool;
        }

        public PersistentFlushBuffer GetFlushBuffer()
        {
            PersistentFlushBuffer flushBuffer = Volatile.Read(ref _flushBuffer);

            if (flushBuffer == null)
            {
                flushBuffer = new PersistentFlushBuffer(_gd);

                Volatile.Write(ref _flushBuffer, flushBuffer);
            }

            return flushBuffer;
        }

        /// <summary>
        /// Meliora: current size of this thread's readback buffer, zero before first use.
        /// The reference and its size are read with Volatile, so the telemetry thread can
        /// call this while the owner thread creates, resizes or disposes the buffer.
        /// </summary>
        public int GetFlushBufferSize()
        {
            return Volatile.Read(ref _flushBuffer)?.BufferSize ?? 0;
        }

        public void Dispose()
        {
            _pool?.Dispose();

            PersistentFlushBuffer flushBuffer = Volatile.Read(ref _flushBuffer);

            flushBuffer?.Dispose();

            Volatile.Write(ref _flushBuffer, null);
        }
    }

    class BackgroundResources : IDisposable
    {
        private readonly VulkanRenderer _gd;
        private Device _device;

        private readonly Dictionary<Thread, BackgroundResource> _resources;

        public BackgroundResources(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;

            _resources = new Dictionary<Thread, BackgroundResource>();
        }

        private void Cleanup()
        {
            lock (_resources)
            {
                foreach (KeyValuePair<Thread, BackgroundResource> tuple in _resources)
                {
                    if (!tuple.Key.IsAlive)
                    {
                        tuple.Value.Dispose();
                        _resources.Remove(tuple.Key);
                    }
                }
            }
        }

        public BackgroundResource Get()
        {
            Thread thread = Thread.CurrentThread;

            lock (_resources)
            {
                if (!_resources.TryGetValue(thread, out BackgroundResource resource))
                {
                    Cleanup();

                    resource = new BackgroundResource(_gd, _device);

                    _resources[thread] = resource;
                }

                return resource;
            }
        }

        /// <summary>
        /// Meliora: counts and sums the sizes of all live per-thread readback buffers.
        /// Each thread owns its own buffer, so sizes are read with Volatile; the sum and
        /// count are a snapshot, not a maximum.
        /// </summary>
        public void GetFlushBufferStats(out int count, out long totalBytes)
        {
            count = 0;
            totalBytes = 0;

            lock (_resources)
            {
                foreach (KeyValuePair<Thread, BackgroundResource> tuple in _resources)
                {
                    int resourceSize = tuple.Value.GetFlushBufferSize();

                    if (resourceSize > 0)
                    {
                        count++;
                        totalBytes += resourceSize;
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_resources)
            {
                foreach (var resource in _resources.Values)
                {
                    resource.Dispose();
                }
            }
        }
    }
}
