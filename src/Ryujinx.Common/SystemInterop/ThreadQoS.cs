using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Common.SystemInterop
{
    /// <summary>
    /// Roles a thread can play in the emulator, mapped to a Darwin QoS class by <see cref="ThreadQoS"/>.
    /// </summary>
    public enum QoSRole
    {
        /// <summary>GPU command processing thread ("GPU.MainThread").</summary>
        GpuMain,
        /// <summary>MoltenVK/backend render loop thread (drives the swapchain).</summary>
        RenderLoop,
        /// <summary>First guest thread of a process (the game's main thread).</summary>
        GameMainThread,
        /// <summary>Any other guest or HLE kernel thread.</summary>
        GuestThread,
        /// <summary>SurfaceFlinger composition thread.</summary>
        Composer,
        /// <summary>Audio manager update thread.</summary>
        Audio,
        /// <summary>GPU counter/query consumer thread.</summary>
        CounterQueue,
        /// <summary>Generic background work queue (disk cache writer and friends).</summary>
        AsyncWork,
        /// <summary>Background JIT translation threads.</summary>
        BackgroundTranslate,
        /// <summary>Parallel disk shader cache loader threads.</summary>
        DiskCacheLoader,
    }

    /// <summary>
    /// Assigns Darwin QoS classes to emulator threads on iOS/macOS.
    ///
    /// Apple CPUs only have a handful of performance cores (2 on base iPhones), and every
    /// .NET thread starts at QOS_CLASS_DEFAULT, which the scheduler treats like any other
    /// work. Without explicit QoS the scheduler regularly runs background work (counter
    /// queries, disk cache writes, JIT translation) on P-cores while the render loop or the
    /// game's main thread gets bounced to E-cores, which shows up as hard framerate drops.
    ///
    /// Controlled by the MELIORA_QOS environment variable: enabled unless it is set to "0".
    /// </summary>
    public static class ThreadQoS
    {
        private const uint QosClassUserInteractive = 0x21;
        private const uint QosClassUserInitiated = 0x19;
        private const uint QosClassUtility = 0x11;
        private const uint QosClassBackground = 0x09;

        private static readonly bool _isDarwin = OperatingSystem.IsIOS() || OperatingSystem.IsMacOS();

        private static int _perfCores = -1;
        private static int _interactiveUsed;
        private static int _gameMainClaimed;
        private static readonly Dictionary<QoSRole, string> _appliedRoles = new();
        private static readonly object _logLock = new();

        /// <summary>
        /// Whether QoS assignment is active. Reads the environment variable every time, so
        /// the settings toggle can change it between game launches without an app restart.
        /// </summary>
        public static bool Enabled => _isDarwin && Environment.GetEnvironmentVariable("MELIORA_QOS") != "0";

        /// <summary>
        /// Number of performance cores ("hw.perflevel0.physicalcpu"), 2 when unknown.
        /// </summary>
        public static int PerfCores
        {
            get
            {
                int value = Volatile.Read(ref _perfCores);

                if (value > 0)
                {
                    return value;
                }

                value = QueryPerfCores();

                if (value <= 0)
                {
                    value = 2;
                }

                Volatile.Write(ref _perfCores, value);

                return value;
            }
        }

        /// <summary>
        /// Sets the QoS class of the calling thread for the given role. Must be called
        /// inside the thread itself (as the first line of its entrypoint), never from the
        /// creating thread. No-op on non-Darwin platforms and when MELIORA_QOS=0.
        /// </summary>
        public static void SetCurrentThreadQoS(QoSRole role)
        {
            if (!Enabled)
            {
                return;
            }

            uint qosClass = ClassForRole(role);
            string qosName = NameForClass(qosClass);

            // Never put more threads on UserInteractive than there are performance
            // cores; excess requests are downgraded to UserInitiated.
            if (qosClass == QosClassUserInteractive && Interlocked.Increment(ref _interactiveUsed) > PerfCores)
            {
                qosClass = QosClassUserInitiated;
                qosName = "Initiated";
            }

            int rc = pthread_set_qos_class_self_np(qosClass, 0);

            LogOnce(role, qosName, rc);
        }

        /// <summary>
        /// Whether the calling thread may take the game's main thread slot. Only the first
        /// guest thread of a session gets the UserInteractive slot next to GPU.MainThread.
        /// </summary>
        public static bool ClaimGameMainThread()
        {
            return Interlocked.Exchange(ref _gameMainClaimed, 1) == 0;
        }

        /// <summary>
        /// Resets per-session state (interactive slot usage, game main thread claim and the
        /// applied-role log). Call when a new game starts, before guest threads spawn.
        /// </summary>
        public static void ResetSession()
        {
            Interlocked.Exchange(ref _interactiveUsed, 0);
            Interlocked.Exchange(ref _gameMainClaimed, 0);

            lock (_logLock)
            {
                if (_appliedRoles.Count > 0)
                {
                    List<string> parts = new(_appliedRoles.Count);

                    foreach (KeyValuePair<QoSRole, string> pair in _appliedRoles)
                    {
                        parts.Add($"{pair.Key}:{pair.Value}");
                    }

                    Console.WriteLine($"[Meliora] qos: perfCores={PerfCores} applied={string.Join(",", parts)}");

                    _appliedRoles.Clear();
                }
            }
        }

        private static uint ClassForRole(QoSRole role)
        {
            return role switch
            {
                QoSRole.GpuMain => QosClassUserInteractive,
                QoSRole.GameMainThread => QosClassUserInteractive,
                // With only 2 P-cores both slots go to GPU.MainThread and the game's main
                // thread; the backend render loop runs at UserInitiated instead.
                QoSRole.RenderLoop => PerfCores > 2 ? QosClassUserInteractive : QosClassUserInitiated,
                QoSRole.GuestThread => QosClassUserInitiated,
                QoSRole.Composer => QosClassUserInitiated,
                QoSRole.Audio => QosClassUserInitiated,
                QoSRole.DiskCacheLoader => QosClassUserInitiated,
                _ => QosClassUtility, // CounterQueue, AsyncWork, BackgroundTranslate
            };
        }

        private static string NameForClass(uint qosClass)
        {
            return qosClass switch
            {
                QosClassUserInteractive => "Interactive",
                QosClassUserInitiated => "Initiated",
                QosClassUtility => "Utility",
                QosClassBackground => "Background",
                _ => $"0x{qosClass:x2}",
            };
        }

        private static void LogOnce(QoSRole role, string qosName, int rc)
        {
            bool first;

            lock (_logLock)
            {
                first = _appliedRoles.TryAdd(role, qosName);
            }

            // One line per role (first application in the session only), so logs stay
            // readable while dozens of guest threads spawn.
            if (first)
            {
                Console.WriteLine($"[Meliora] qos: enabled={Enabled} perfCores={PerfCores} applied={role}:{qosName}(rc={rc})");
            }
        }

        private static int QueryPerfCores()
        {
            if (!_isDarwin)
            {
                return 0;
            }

            try
            {
                int old = 0;
                nuint len = (nuint)sizeof(int);

                if (sysctlbyname("hw.perflevel0.physicalcpu", ref old, ref len, 0, 0) == 0 && old > 0)
                {
                    return old;
                }
            }
            catch
            {
            }

            return 0;
        }

        [DllImport("libSystem.dylib", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pthread_set_qos_class_self_np(uint qos_class, int relative_priority);

        [DllImport("libSystem.dylib", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sysctlbyname(string name, ref int oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);
    }
}
