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
            _flushBuffer ??= new PersistentFlushBuffer(_gd);

            return _flushBuffer;
        }

        /// <summary>
        /// Meliora: current size of this thread's readback buffer, zero before first use.
        /// </summary>
        public int GetFlushBufferSize()
        {
            return _flushBuffer?.BufferSize ?? 0;
        }

        public void Dispose()
        {
            _pool?.Dispose();
            _flushBuffer?.Dispose();
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
        /// Meliora: largest readback buffer among the per-thread background resources.
        /// </summary>
        public int GetFlushBufferSize()
        {
            int size = 0;

            lock (_resources)
            {
                foreach (KeyValuePair<Thread, BackgroundResource> tuple in _resources)
                {
                    int resourceSize = tuple.Value.GetFlushBufferSize();

                    if (resourceSize > size)
                    {
                        size = resourceSize;
                    }
                }
            }

            return size;
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
