using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.PreciseSleep;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger
{
    class SurfaceFlinger : IConsumerListener, IDisposable
    {
        private readonly Switch _device;

        private readonly Dictionary<long, Layer> _layers;

        private bool _isRunning;

        private readonly Thread _composerThread;

        private readonly AutoResetEvent _event = new(false);
        private readonly AutoResetEvent _nextFrameEvent = new(true);
        private long _ticks;
        private long _ticksPerFrame;

        // Meliora: the spin tail is adaptive. It starts at the old fixed 2ms budget and
        // then follows the composer's measured wake lateness, clamped between 0.2ms and
        // 2ms, or is pinned by MELIORA_SPIN_US when that is set.
        private long _spinTicks;
        private readonly long _spinFloorTicks;
        private readonly long _spinCapTicks;
        private long _latenessEma;
        private bool _spinAdaptive;
        private readonly bool _smoothRuntime;
        private int _appliedTargetHz;

        private readonly long _1msTicks;

        private int _swapInterval;
        private int _swapIntervalDelay;

        private readonly object _lock = new();

        public long RenderLayerId { get; private set; }

        private class Layer
        {
            public int ProducerBinderId;
            public IGraphicBufferProducer Producer;
            public BufferItemConsumer Consumer;
            public BufferQueueCore Core;
            public ulong Owner;
            public LayerState State;
        }

        private class TextureCallbackInformation
        {
            public Layer Layer;
            public BufferItem Item;
        }

        public SurfaceFlinger(Switch device)
        {
            _device = device;
            _layers = new Dictionary<long, Layer>();
            RenderLayerId = 0;

            _composerThread = new Thread(HandleComposition, 4096 * 2)
            {
                Name = "SurfaceFlinger.Composer",
            };

            // .NET's Thread.Priority calls pthread_setschedparam on Darwin, which locks
            // the thread's scheduler policy and makes pthread_set_qos_class_self_np fail
            // with EINVAL later. While QoS is active, skip the managed priority and tag
            // the thread with a QoS class in HandleComposition instead.
            if (!ThreadQoS.Enabled)
            {
                _composerThread.Priority = ThreadPriority.AboveNormal;
            }

            _ticks = 0;
            _1msTicks = Stopwatch.Frequency / 1000;
            _spinFloorTicks = Stopwatch.Frequency / 5000; // 0.2ms
            _spinCapTicks = Stopwatch.Frequency / 500;    // 2ms
            // Meliora: re-read the pacing target for this game. The app re-applies the
            // environment for every launch, and the static field would otherwise keep
            // the value of the very first game in the process. The smooth flag is
            // sampled once here too, instead of a per-frame environment lookup.
            MelioraFramePacing.RefreshTargetHz();
            _smoothRuntime = Environment.GetEnvironmentVariable("MELIORA_SMOOTH") == "1";

            _appliedTargetHz = MelioraFramePacing.TargetHz;

            // Meliora: a fixed spin budget can be forced with MELIORA_SPIN_US (in
            // microseconds); otherwise it adapts to the measured wake lateness.
            if (long.TryParse(Environment.GetEnvironmentVariable("MELIORA_SPIN_US"), out long spinUs) && spinUs > 0)
            {
                _spinTicks = Math.Clamp(spinUs * Stopwatch.Frequency / 1_000_000, 1, Stopwatch.Frequency / 100);
                _spinAdaptive = false;
            }
            else
            {
                _spinTicks = _spinCapTicks;
                _spinAdaptive = true;
            }

            UpdateSwapInterval(1);

            _composerThread.Start();
        }

        private void UpdateSwapInterval(int swapInterval)
        {
            _swapInterval = swapInterval;

            // If the swap interval is 0, Game VSync is disabled.
            if (_swapInterval == 0)
            {
                _nextFrameEvent.Set();
                _ticksPerFrame = 1;
            }
            else
            {
                _ticksPerFrame = Stopwatch.Frequency / MelioraFramePacing.TargetHz;
            }
        }

        public IGraphicBufferProducer CreateLayer(out long layerId, ulong pid, LayerState initialState = LayerState.ManagedClosed)
        {
            layerId = 1;

            lock (_lock)
            {
                foreach (KeyValuePair<long, Layer> pair in _layers)
                {
                    if (pair.Key >= layerId)
                    {
                        layerId = pair.Key + 1;
                    }
                }
            }

            CreateLayerFromId(pid, layerId, initialState);

            return GetProducerByLayerId(layerId);
        }

        private void CreateLayerFromId(ulong pid, long layerId, LayerState initialState)
        {
            lock (_lock)
            {
                Logger.Info?.Print(LogClass.SurfaceFlinger, $"Creating layer {layerId}");

                BufferQueueCore core = BufferQueue.CreateBufferQueue(_device, pid, out BufferQueueProducer producer, out BufferQueueConsumer consumer);

                core.BufferQueued += () =>
                {
                    _nextFrameEvent.Set();
                };

                _layers.Add(layerId, new Layer
                {
                    ProducerBinderId = HOSBinderDriverServer.RegisterBinderObject(producer),
                    Producer = producer,
                    Consumer = new BufferItemConsumer(_device, consumer, 0, -1, false, this),
                    Core = core,
                    Owner = pid,
                    State = initialState,
                });
            }
        }

        public Vi.ResultCode OpenLayer(ulong pid, long layerId, out IBinder producer)
        {
            Layer layer = GetLayerByIdLocked(layerId);

            if (layer == null || layer.State != LayerState.ManagedClosed)
            {
                producer = null;

                return Vi.ResultCode.InvalidArguments;
            }

            layer.State = LayerState.ManagedOpened;
            producer = layer.Producer;

            return Vi.ResultCode.Success;
        }

        public Vi.ResultCode CloseLayer(long layerId)
        {
            lock (_lock)
            {
                Layer layer = GetLayerByIdLocked(layerId);

                if (layer == null)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to close layer {layerId}");

                    return Vi.ResultCode.InvalidValue;
                }

                CloseLayer(layerId, layer);

                return Vi.ResultCode.Success;
            }
        }

        public Vi.ResultCode DestroyManagedLayer(long layerId)
        {
            lock (_lock)
            {
                Layer layer = GetLayerByIdLocked(layerId);

                if (layer == null)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy managed layer {layerId} (not found)");

                    return Vi.ResultCode.InvalidValue;
                }

                if (layer.State != LayerState.ManagedClosed && layer.State != LayerState.ManagedOpened)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy managed layer {layerId} (permission denied)");

                    return Vi.ResultCode.PermissionDenied;
                }

                HOSBinderDriverServer.UnregisterBinderObject(layer.ProducerBinderId);

                if (_layers.Remove(layerId) && layer.State == LayerState.ManagedOpened)
                {
                    CloseLayer(layerId, layer);
                }

                return Vi.ResultCode.Success;
            }
        }

        public Vi.ResultCode DestroyStrayLayer(long layerId)
        {
            lock (_lock)
            {
                Layer layer = GetLayerByIdLocked(layerId);

                if (layer == null)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy stray layer {layerId} (not found)");

                    return Vi.ResultCode.InvalidValue;
                }

                if (layer.State != LayerState.Stray)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy stray layer {layerId} (permission denied)");

                    return Vi.ResultCode.PermissionDenied;
                }

                HOSBinderDriverServer.UnregisterBinderObject(layer.ProducerBinderId);

                if (_layers.Remove(layerId))
                {
                    CloseLayer(layerId, layer);
                }

                return Vi.ResultCode.Success;
            }
        }

        private void CloseLayer(long layerId, Layer layer)
        {
            // If the layer was removed and the current in use, we need to change the current layer in use.
            if (RenderLayerId == layerId)
            {
                // If no layer is availaible, reset to default value.
                if (_layers.Count == 0)
                {
                    SetRenderLayer(0);
                }
                else
                {
                    SetRenderLayer(_layers.Last().Key);
                }
            }

            if (layer.State == LayerState.ManagedOpened)
            {
                layer.State = LayerState.ManagedClosed;
            }
        }

        public void SetRenderLayer(long layerId)
        {
            lock (_lock)
            {
                RenderLayerId = layerId;
            }
        }

        private Layer GetLayerByIdLocked(long layerId)
        {
            foreach (KeyValuePair<long, Layer> pair in _layers)
            {
                if (pair.Key == layerId)
                {
                    return pair.Value;
                }
            }

            return null;
        }

        public IGraphicBufferProducer GetProducerByLayerId(long layerId)
        {
            lock (_lock)
            {
                Layer layer = GetLayerByIdLocked(layerId);

                if (layer != null)
                {
                    return layer.Producer;
                }
            }

            return null;
        }

        private void HandleComposition()
        {
            ThreadQoS.SetCurrentThreadQoS(QoSRole.Composer);

            _isRunning = true;

            long lastTicks = PerformanceCounter.ElapsedTicks;

            while (_isRunning)
            {
                long ticks = PerformanceCounter.ElapsedTicks;

                if (_swapInterval == 0)
                {
                    Compose();

                    // Meliora: the wait bounds how long the unbuffered path idles when
                    // no new buffer arrives; follow the pacing target instead of a
                    // hard-coded 60 Hz assumption.
                    _device.System?.SignalVsync();

                    _nextFrameEvent.WaitOne((int)Math.Ceiling(1000.0 / MelioraFramePacing.TargetHz));
                    lastTicks = ticks;
                }
                else
                {
                    _ticks += ticks - lastTicks;
                    lastTicks = ticks;

                    // Meliora: with the "Smooth" toggle on, pick up a changed pacing
                    // target without a game restart.
                    if (_smoothRuntime)
                    {
                        int targetHz = MelioraFramePacing.TargetHz;

                        if (targetHz != _appliedTargetHz)
                        {
                            _appliedTargetHz = targetHz;
                            _ticksPerFrame = Stopwatch.Frequency / Math.Max(targetHz, 1);
                        }
                    }

                    if (_ticks >= _ticksPerFrame)
                    {
                        if (_swapIntervalDelay-- == 0)
                        {
                            Compose();

                            // When a frame is presented, delay the next one by its swap interval value.
                            _swapIntervalDelay = Math.Max(0, _swapInterval - 1);
                        }

                        _device.System?.SignalVsync();

                        // Apply a maximum bound of 3 frames to the tick remainder, in case some event causes Ryujinx to pause for a long time or messes with the timer.
                        _ticks = Math.Min(_ticks - _ticksPerFrame, _ticksPerFrame * 3);
                    }

                    // Sleep if possible. If the time til the next frame is too low, spin wait instead.
                    long diff = _ticksPerFrame - (_ticks + PerformanceCounter.ElapsedTicks - ticks);
                    if (diff > 0)
                    {
                        long target = PerformanceCounter.ElapsedTicks + diff;

                        PreciseSleepHelper.SleepUntilTimePoint(_event, target);

                        if (_spinAdaptive)
                        {
                            UpdateSpinTicks(target);
                        }

                        diff = _ticksPerFrame - (_ticks + PerformanceCounter.ElapsedTicks - ticks);

                        if (diff <= 0)
                        {
                            // The sleep already overran the time point; nothing to wait for.
                        }
                        else if (diff < _spinTicks)
                        {
                            PreciseSleepHelper.SpinWaitUntilTimePoint(PerformanceCounter.ElapsedTicks + diff);
                        }
                        else
                        {
                            _event.WaitOne((int)(diff / _1msTicks));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Meliora: tunes the spin threshold from the measured wake lateness of the
        /// previous sleep.
        ///
        /// The nanosleep path is biased to wake slightly before the time point, so what
        /// is left of the frame is covered by a precise spin. When the wake-earliness
        /// varies between devices (or disappears entirely, waking late instead), a fixed
        /// threshold either burns CPU spinning uselessly or leaves a coarse wait in
        /// charge of the last milliseconds. The exponential moving average of the
        /// observed lateness keeps the spin just big enough to cover it, within a
        /// 0.2ms floor and the old 2ms cap.
        /// </summary>
        /// <param name="targetTicks">Time point the sleep was aiming for</param>
        private void UpdateSpinTicks(long targetTicks)
        {
            long wake = PerformanceCounter.ElapsedTicks;

            if (wake <= targetTicks)
            {
                // Woke on time or early: nothing was lost to lateness.
                return;
            }

            long lateness = wake - targetTicks;

            // 25% weight per frame, so a single scheduling outlier cannot pin the spin.
            _latenessEma = _latenessEma == 0 ? lateness : _latenessEma + ((lateness - _latenessEma) >> 2);

            _spinTicks = Math.Clamp(_latenessEma + _1msTicks / 5, _spinFloorTicks, _spinCapTicks);
        }

        public void Compose()
        {
            lock (_lock)
            {
                // TODO: support multilayers (& multidisplay ?)
                if (RenderLayerId == 0)
                {
                    return;
                }

                Layer layer = GetLayerByIdLocked(RenderLayerId);

                Status acquireStatus = layer.Consumer.AcquireBuffer(out BufferItem item, 0);

                if (acquireStatus == Status.Success)
                {
                    // If device vsync is disabled, reflect the change.
                    if (!_device.EnableDeviceVsync)
                    {
                        if (_swapInterval != 0)
                        {
                            UpdateSwapInterval(0);
                        }
                    }
                    else if (item.SwapInterval != _swapInterval)
                    {
                        UpdateSwapInterval(item.SwapInterval);
                    }

                    PostFrameBuffer(layer, item);
                }
                else if (acquireStatus != Status.NoBufferAvailaible && acquireStatus != Status.InvalidOperation)
                {
                    throw new InvalidOperationException();
                }
            }
        }

        private void PostFrameBuffer(Layer layer, BufferItem item)
        {
            int frameBufferWidth = item.GraphicBuffer.Object.Width;
            int frameBufferHeight = item.GraphicBuffer.Object.Height;

            int nvMapHandle = item.GraphicBuffer.Object.Buffer.Surfaces[0].NvMapHandle;

            if (nvMapHandle == 0)
            {
                nvMapHandle = item.GraphicBuffer.Object.Buffer.NvMapId;
            }

            ulong bufferOffset = (ulong)item.GraphicBuffer.Object.Buffer.Surfaces[0].Offset;

            NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(layer.Owner, nvMapHandle);

            ulong frameBufferAddress = map.Address + bufferOffset;

            Format format = ConvertColorFormat(item.GraphicBuffer.Object.Buffer.Surfaces[0].ColorFormat);

            byte bytesPerPixel =
                format == Format.B5G6R5Unorm ||
                format == Format.R4G4B4A4Unorm ? (byte)2 : (byte)4;

            int gobBlocksInY = 1 << item.GraphicBuffer.Object.Buffer.Surfaces[0].BlockHeightLog2;

            // Note: Rotation is being ignored.
            Rect cropRect = item.Crop;

            bool flipX = item.Transform.HasFlag(NativeWindowTransform.FlipX);
            bool flipY = item.Transform.HasFlag(NativeWindowTransform.FlipY);

            AspectRatio aspectRatio = _device.Configuration.AspectRatio;
            bool isStretched = aspectRatio == AspectRatio.Stretched;

            ImageCrop crop = new(
                cropRect.Left,
                cropRect.Right,
                cropRect.Top,
                cropRect.Bottom,
                flipX,
                flipY,
                isStretched,
                aspectRatio.ToFloatX(),
                aspectRatio.ToFloatY());

            TextureCallbackInformation textureCallbackInformation = new()
            {
                Layer = layer,
                Item = item,
            };

            if (_device.Gpu.Window.EnqueueFrameThreadSafe(
                layer.Owner,
                frameBufferAddress,
                frameBufferWidth,
                frameBufferHeight,
                0,
                false,
                gobBlocksInY,
                format,
                bytesPerPixel,
                crop,
                AcquireBuffer,
                ReleaseBuffer,
                textureCallbackInformation))
            {
                if (item.Fence.FenceCount == 0)
                {
                    _device.Gpu.Window.SignalFrameReady();
                    _device.Gpu.GPFifo.Interrupt();
                }
                else
                {
                    item.Fence.RegisterCallback(_device.Gpu, (x) =>
                    {
                        _device.Gpu.Window.SignalFrameReady();
                        _device.Gpu.GPFifo.Interrupt();
                    });
                }
            }
            else
            {
                ReleaseBuffer(textureCallbackInformation);
            }
        }

        private void ReleaseBuffer(object obj)
        {
            ReleaseBuffer((TextureCallbackInformation)obj);
        }

        private void ReleaseBuffer(TextureCallbackInformation information)
        {
            AndroidFence fence = AndroidFence.NoFence;

            information.Layer.Consumer.ReleaseBuffer(information.Item, ref fence);
        }

        private void AcquireBuffer(GpuContext ignored, object obj)
        {
            AcquireBuffer((TextureCallbackInformation)obj);
        }

        private void AcquireBuffer(TextureCallbackInformation information)
        {
            information.Item.Fence.WaitForever(_device.Gpu);
        }

        public static Format ConvertColorFormat(ColorFormat colorFormat)
        {
            return colorFormat switch
            {
                ColorFormat.A8B8G8R8 => Format.R8G8B8A8Unorm,
                ColorFormat.X8B8G8R8 => Format.R8G8B8A8Unorm,
                ColorFormat.R5G6B5 => Format.B5G6R5Unorm,
                ColorFormat.A8R8G8B8 => Format.B8G8R8A8Unorm,
                ColorFormat.A4B4G4R4 => Format.R4G4B4A4Unorm,
                _ => throw new NotImplementedException($"Color Format \"{colorFormat}\" not implemented!"),
            };
        }

        public void Dispose()
        {
            _isRunning = false;

            foreach (Layer layer in _layers.Values)
            {
                layer.Core.PrepareForExit();
            }
        }

        public void OnFrameAvailable(ref BufferItem item)
        {
            _device.Statistics.RecordGameFrameTime();
        }

        public void OnFrameReplaced(ref BufferItem item)
        {
            _device.Statistics.RecordGameFrameTime();
        }

        public void OnBuffersReleased() { }
    }
}
