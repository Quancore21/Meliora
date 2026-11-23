using System;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger
{
    /// <summary>
    /// Meliora: composition cadence for the Surfaceless composer.
    ///
    /// The target FPS setting already limits how often the composer wakes up. A game that
    /// asks for swap interval 2 is asking to present every second native (60 Hz) vsync,
    /// not every second target tick. Applying both limits made Target 30 + native 30
    /// compose at 15 FPS.
    /// </summary>
    internal static class MelioraCompositionPacing
    {
        internal const int NativeHz = 60;

        /// <summary>
        /// Number of target-paced ticks between two compositions.
        /// </summary>
        /// <param name="targetHz">Pacing target in frames per second</param>
        /// <param name="swapInterval">Swap interval requested by the game</param>
        /// <returns>At least one tick</returns>
        internal static int GetComposeTickInterval(int targetHz, int swapInterval)
        {
            if (targetHz <= 0 || swapInterval <= 1)
            {
                return 1;
            }

            // Effective rate is min(targetHz, NativeHz / swapInterval), expressed as a
            // number of target ticks. For the four supported combinations this is exact:
            // 60/1 -> 1, 60/2 -> 2, 30/1 -> 1, 30/2 -> 1.
            int interval = (int)Math.Round(
                swapInterval * (double)targetHz / NativeHz,
                MidpointRounding.AwayFromZero);

            return Math.Max(1, interval);
        }
    }
}
