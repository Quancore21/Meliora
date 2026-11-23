using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using System;

namespace Ryujinx.Tests.Graphics
{
    /// <summary>
    /// Tests for the reference-counting semantics of <see cref="Auto{T}"/> that protect
    /// texture readbacks. A readback that is queued while its source texture is being
    /// disposed must keep the VkImage alive until the copy command completes, and a
    /// readback on an already destroyed storage must fail cleanly instead of recording
    /// a copy with a null Vulkan image handle (which crashed the driver on iOS).
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class AutoLifetimeTests
    {
        private sealed class TrackedDisposable : IDisposable
        {
            public bool Disposed;

            public void Dispose()
            {
                Disposed = true;
            }
        }

        [Test]
        public void TryIncrementOnAliveObjectSucceeds()
        {
            var value = new TrackedDisposable();
            var auto = new Auto<TrackedDisposable>(value);

            Assert.That(auto.TryIncrementReferenceCount(), Is.True);

            // Disposing while the extra reference is held must not destroy the value yet.
            auto.Dispose();

            Assert.That(value.Disposed, Is.False);

            auto.DecrementReferenceCount();

            Assert.That(value.Disposed, Is.True, "Value must be destroyed after the last reference is released");
        }

        [Test]
        public void HeldReferenceDefersDestructionUntilReleased()
        {
            var value = new TrackedDisposable();
            var auto = new Auto<TrackedDisposable>(value);

            Assert.That(auto.TryIncrementReferenceCount(), Is.True);

            auto.Dispose();

            // While the readback holds its reference, the value must stay usable.
            Assert.That(value.Disposed, Is.False, "Value must not be destroyed while a readback holds a reference");
            Assert.That(auto.GetUnsafe(), Is.SameAs(value), "Value must stay accessible while a reference is held");

            auto.DecrementReferenceCount();

            Assert.That(value.Disposed, Is.True);
        }

        [Test]
        public void TryIncrementOnDestroyedObjectFails()
        {
            var value = new TrackedDisposable();
            var auto = new Auto<TrackedDisposable>(value);

            auto.Dispose();

            // After destruction the value is gone: a readback on it must fail cleanly
            // instead of recording a copy with a null handle.
            Assert.That(auto.TryIncrementReferenceCount(), Is.False, "TryIncrementReferenceCount must fail on a destroyed object");
            Assert.That(value.Disposed, Is.True);
        }

        [Test]
        public void GetOnDestroyedObjectReturnsDefault()
        {
            var value = new TrackedDisposable();
            var auto = new Auto<TrackedDisposable>(value);

            auto.Dispose();

            // This is the state that produced the null VkImage in the field crash.
            Assert.That(auto.Get(default), Is.Null);
        }

        [Test]
        public void GetUnsafeOnAliveObjectReturnsValue()
        {
            var value = new TrackedDisposable();
            var auto = new Auto<TrackedDisposable>(value);

            Assert.That(auto.GetUnsafe(), Is.SameAs(value));

            auto.Dispose();

            Assert.That(value.Disposed, Is.True);
        }

        [Test]
        public void LogBrokenStateIsSafeToCall()
        {
            // The guard must never throw, even when called from multiple threads at once.
            Assert.DoesNotThrow(() => MelioraReadbackGuard.LogBrokenState("test", "details"));
        }
    }
}
