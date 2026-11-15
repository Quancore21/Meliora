using System;
using System.Runtime.InteropServices;
using Ryujinx.Common.Logging;
using Ryujinx.UI.Common.Helper;
using System.Threading;

namespace Ryujinx.Headless.SDL2
{
    public static class AlertHelper
    {
        private const int KeyboardPollIntervalMs = 100;
        private const int KeyboardTimeoutMs = 120_000;

        private static readonly object _keyboardLock = new();
        private static int _keyboardGeneration;

        [DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        public static extern void showKeyboardAlert(string title, string message, string placeholder);

        [DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        public static extern void showAlert(string title, string message, bool showCancel);

        [DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr getKeyboardInput();

        [DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        private static extern void clearKeyboardInput();

        public static void ShowAlertWithTextInput(string title, string message, string placeholder, Action<string> onTextEntered)
        {
            int generation;

            lock (_keyboardLock)
            {
                generation = ++_keyboardGeneration;
            }

            clearKeyboardInput();
            showKeyboardAlert(title ?? string.Empty, message ?? string.Empty, placeholder ?? string.Empty);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                long deadline = Environment.TickCount64 + KeyboardTimeoutMs;

                while (Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(KeyboardPollIntervalMs);

                    lock (_keyboardLock)
                    {
                        if (generation != _keyboardGeneration)
                        {
                            // Another prompt replaced this one (or the app is shutting
                            // down), stop polling instead of living forever.
                            return;
                        }
                    }

                    IntPtr inputPtr = getKeyboardInput();

                    if (inputPtr != IntPtr.Zero)
                    {
                        string result = Marshal.PtrToStringAnsi(inputPtr) ?? string.Empty;

                        clearKeyboardInput();

                        onTextEntered?.Invoke(result);

                        return;
                    }
                }

                Logger.Warning?.Print(LogClass.Application, "Timed out waiting for the iOS keyboard input.");

                // Complete the dialog so the guest does not wait forever for an answer.
                onTextEntered?.Invoke(string.Empty);
            });
        }

        public static void CancelPendingTextInput()
        {
            lock (_keyboardLock)
            {
                _keyboardGeneration++;
            }

            clearKeyboardInput();
        }

        public static void ShowAlert(string title, string message, bool cancel) {
            showAlert(title, message, cancel);
        }
    }
}
