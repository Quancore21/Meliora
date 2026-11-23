using System;

namespace Ryujinx.Graphics.GAL
{
    /// <summary>
    /// Implemented by programs that finish compilation asynchronously and can notify a listener
    /// when their compilation reaches a terminal state.
    /// </summary>
    public interface IProgramCompilationNotifier
    {
        /// <summary>
        /// Raised once when background compilation finishes, successfully or not.
        /// </summary>
        event Action CompilationCompleted;
    }
}
