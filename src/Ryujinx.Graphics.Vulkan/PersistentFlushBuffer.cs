using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    internal class PersistentFlushBuffer : IDisposable
    {
        private readonly VulkanRenderer _gd;

        private BufferHolder _flushStorage;

        // The telemetry thread reads the size while the owner thread may be creating,
        // resizing or disposing the storage, so it is kept in a volatile field.
        private int _bufferSize;

        /// <summary>
        /// Current size of the persistent readback buffer, or zero before first use.
        /// </summary>
        public int BufferSize => Volatile.Read(ref _bufferSize);

        public PersistentFlushBuffer(VulkanRenderer gd)
        {
            _gd = gd;
        }

        private BufferHolder ResizeIfNeeded(int size)
        {
            var flushStorage = Volatile.Read(ref _flushStorage);

            if (flushStorage == null || size > flushStorage.Size)
            {
                flushStorage?.Dispose();

                Volatile.Write(ref _bufferSize, 0);

                flushStorage = _gd.BufferManager.Create(_gd, size);

                Volatile.Write(ref _flushStorage, flushStorage);
                Volatile.Write(ref _bufferSize, flushStorage.Size);
            }

            return flushStorage;
        }

        public Span<byte> GetBufferData(CommandBufferPool cbp, BufferHolder buffer, int offset, int size)
        {
            long melioraStart = Stopwatch.GetTimestamp();

            var flushStorage = ResizeIfNeeded(size);
            Auto<DisposableBuffer> srcBuffer;

            using (var cbs = cbp.Rent())
            {
                srcBuffer = buffer.GetBuffer(cbs.CommandBuffer);
                var dstBuffer = flushStorage.GetBuffer(cbs.CommandBuffer);

                if (srcBuffer.TryIncrementReferenceCount())
                {
                    BufferHolder.Copy(_gd, cbs, srcBuffer, dstBuffer, offset, 0, size, registerSrcUsage: false);
                }
                else
                {
                    // Source buffer is no longer alive, don't copy anything to flush storage.
                    srcBuffer = null;
                }
            }

            flushStorage.WaitForFences();
            srcBuffer?.DecrementReferenceCount();

            Span<byte> result = flushStorage.GetDataStorage(0, size);

            // Meliora: CPU-side wall time of the whole readback call, including the fence
            // wait above; not pure GPU copy time.
            MelioraTelemetry.AddReadback(Stopwatch.GetTimestamp() - melioraStart, size);

            return result;
        }

        public Span<byte> GetTextureData(CommandBufferPool cbp, TextureView view, int size)
        {
            long melioraStart = Stopwatch.GetTimestamp();

            TextureCreateInfo info = view.Info;

            var flushStorage = ResizeIfNeeded(size);

            using (var cbs = cbp.Rent())
            {
                var buffer = flushStorage.GetBuffer(cbs.CommandBuffer).Get(cbs).Value;
                var image = view.GetImage().Get(cbs).Value;

                view.CopyFromOrToBuffer(cbs.CommandBuffer, buffer, image, size, true, 0, 0, info.GetLayers(), info.Levels, singleSlice: false);
            }

            flushStorage.WaitForFences();

            Span<byte> result = flushStorage.GetDataStorage(0, size);

            // Meliora: CPU-side wall time of the whole readback call, including the fence
            // wait above; not pure GPU copy time.
            MelioraTelemetry.AddReadback(Stopwatch.GetTimestamp() - melioraStart, size);

            return result;
        }

        public Span<byte> GetTextureData(CommandBufferPool cbp, TextureView view, int size, int layer, int level)
        {
            long melioraStart = Stopwatch.GetTimestamp();

            var flushStorage = ResizeIfNeeded(size);

            using (var cbs = cbp.Rent())
            {
                var buffer = flushStorage.GetBuffer(cbs.CommandBuffer).Get(cbs).Value;
                var image = view.GetImage().Get(cbs).Value;

                view.CopyFromOrToBuffer(cbs.CommandBuffer, buffer, image, size, true, layer, level, 1, 1, singleSlice: true);
            }

            flushStorage.WaitForFences();

            Span<byte> result = flushStorage.GetDataStorage(0, size);

            // Meliora: CPU-side wall time of the whole readback call, including the fence
            // wait above; not pure GPU copy time.
            MelioraTelemetry.AddReadback(Stopwatch.GetTimestamp() - melioraStart, size);

            return result;
        }

        public void Dispose()
        {
            BufferHolder flushStorage = Volatile.Read(ref _flushStorage);

            flushStorage?.Dispose();

            Volatile.Write(ref _flushStorage, null);
            Volatile.Write(ref _bufferSize, 0);
        }
    }
}
