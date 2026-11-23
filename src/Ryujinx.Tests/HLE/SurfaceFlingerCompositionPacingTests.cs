using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.SurfaceFlinger;

namespace Ryujinx.Tests.HLE
{
    [TestFixture]
    public class SurfaceFlingerCompositionPacingTests
    {
        [TestCase(60, 1, 60)]
        [TestCase(60, 2, 30)]
        [TestCase(30, 1, 30)]
        [TestCase(30, 2, 30)] // native 30 FPS + Target 30 must not become 15
        public void CompositionsPerSecondMatchTheExpectedRate(int targetHz, int swapInterval, int expectedFps)
        {
            int composeInterval = MelioraCompositionPacing.GetComposeTickInterval(targetHz, swapInterval);

            int composes = 0;
            int delay = 0;

            // One virtual second: the composer wakes up targetHz times.
            for (int tick = 0; tick < targetHz; tick++)
            {
                if (delay-- == 0)
                {
                    composes++;
                    delay = composeInterval - 1;
                }
            }

            Assert.That(composes, Is.EqualTo(expectedFps));
        }

        [TestCase(60)]
        [TestCase(30)]
        [TestCase(15)]
        [TestCase(120)]
        public void SwapIntervalOneNeverSkipsTargetTicks(int targetHz)
        {
            Assert.That(MelioraCompositionPacing.GetComposeTickInterval(targetHz, 1), Is.EqualTo(1));
        }

        [TestCase(1, 2)]
        [TestCase(60, 2)]
        [TestCase(120, 2)]
        public void CompositionIntervalIsAtLeastOneTick(int targetHz, int swapInterval)
        {
            Assert.That(MelioraCompositionPacing.GetComposeTickInterval(targetHz, swapInterval), Is.GreaterThanOrEqualTo(1));
        }
    }
}
