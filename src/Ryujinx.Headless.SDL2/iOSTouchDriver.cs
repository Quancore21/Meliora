using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Linq;

namespace Ryujinx.Headless.SDL2
{
    class iOSTouchDriver : IGamepadDriver
    {
        private const int CursorHideIdleTime = 5;

        private bool _isDisposed;
        private readonly HideCursorMode _hideCursorMode;
        private bool _isHidden;
        private long _lastCursorMoveTime;

        public bool[] PressedButtons { get; }
        public Vector2[] CurrentPosition
        {
            get
            {
                lock (TouchLock)
                {
                    return _activeTouches.Values.ToArray();
                }
            }
        }
        public Vector2 Scroll { get; private set; }
        public Size ClientSize;

        private static Dictionary<int, Vector2> _activeTouches = new();
        private static readonly object TouchLock = new();

        public iOSTouchDriver(HideCursorMode hideCursorMode)
        {
            PressedButtons = new bool[(int)MouseButton.Count];
            _hideCursorMode = hideCursorMode;
        }

        [UnmanagedCallersOnly(EntryPoint = "touch_began")]
        public static void TouchBeganAtPoint(float x, float y, int index)
        {
            Vector2 position = new Vector2(x, y);

            lock (TouchLock)
            {
                _activeTouches[index] = position;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "touch_moved")]
        public static void TouchMovedAtPoint(float x, float y, int index)
        {
            lock (TouchLock)
            {
                if (_activeTouches.ContainsKey(index))
                {
                    _activeTouches[index] = new Vector2(x, y);
                }
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "touch_ended")]
        public static void TouchEndedForIndex(int index)
        {
            lock (TouchLock)
            {
                _activeTouches.Remove(index);
            }
        }

        public static void ResetTouches()
        {
            lock (TouchLock)
            {
                _activeTouches.Clear();
            }
        }

        public void UpdatePosition()
        {
            lock (TouchLock)
            {
                if (_activeTouches.Count > 0)
                {
                    var touch = _activeTouches.Values.GetEnumerator();
                    touch.MoveNext();
                    Vector2 position = touch.Current;
                }
            }

            CheckIdle();
        }

        private void CheckIdle()
        {
            if (_hideCursorMode != HideCursorMode.OnIdle)
            {
                return;
            }

            long cursorMoveDelta = Stopwatch.GetTimestamp() - _lastCursorMoveTime;

            if (cursorMoveDelta >= CursorHideIdleTime * Stopwatch.Frequency)
            {
                if (!_isHidden)
                {
                    Logger.Debug?.Print(LogClass.Application, "Hiding cursor due to inactivity.");
                    _isHidden = true;
                }
            }
            else
            {
                if (_isHidden)
                {
                    Logger.Debug?.Print(LogClass.Application, "Showing cursor after activity.");
                    _isHidden = false;
                }
            }
        }


        public void SetClientSize(int width, int height)
        {
            ClientSize = new Size(width, height);
        }

        public bool IsAnyPressed(MouseButton button)
        {
            lock (TouchLock)
            {
                return _activeTouches.Count > 0;
            }
        }

        public Size GetClientSize()
        {
            return ClientSize;
        }

        public string DriverName => "iOSTouchDriver";

        public event Action<string> OnGamepadConnected
        {
            add { }
            remove { }
        }

        public event Action<string> OnGamepadDisconnected
        {
            add { }
            remove { }
        }

        public ReadOnlySpan<string> GamepadsIds => new[] { "0" };

        public IGamepad GetGamepad(string id)
        {
            return new iOSMouse(this);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;

            // Do not leak touch points into the next emulation session.
            ResetTouches();
        }
    }
}
