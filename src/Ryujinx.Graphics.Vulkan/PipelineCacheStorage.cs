using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// Keeps the Vulkan pipeline cache on disk, so shaders already compiled by the driver
    /// (on iOS: by MoltenVK) do not have to be compiled again on the next launch.
    /// The file is wrapped in a small header with a checksum, and it is written atomically,
    /// so a damaged file is detected and ignored instead of being handed to the driver.
    /// </summary>
    public static class PipelineCacheStorage
    {
        private const uint Magic = 0x504C454D; // "MELP"
        private const uint FormatVersion = 1;
        private const int HeaderSize = 24;
        private const long MaxPayloadBytes = 512L * 1024 * 1024;
        private const long MaxFolderBytes = 1536L * 1024 * 1024;
        private const long MinSaveIntervalMs = 60_000;

        private static readonly object _gate = new();
        private static Action _saveHook;
        private static long _lastSaveTick = long.MinValue / 2;
        private static int _pendingSave; // 0 = none, 1 = normal, 2 = forced

        /// <summary>
        /// Asks for the cache to be saved on the next render thread tick.
        /// This must be used by callers that run on other threads: vkGetPipelineCacheData
        /// is externally synchronized with pipeline creation, so the save itself is done
        /// on the thread that creates pipelines.
        /// </summary>
        /// <param name="force">True to ignore the minimum interval between saves</param>
        public static void RequestSave(bool force)
        {
            int request = force ? 2 : 1;
            int current = Volatile.Read(ref _pendingSave);

            while (current < request)
            {
                int previous = Interlocked.CompareExchange(ref _pendingSave, request, current);

                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }

        /// <summary>
        /// Executes a save requested with <see cref="RequestSave"/>.
        /// Must only be called from the render thread.
        /// </summary>
        public static void ProcessPendingSave()
        {
            int request = Interlocked.Exchange(ref _pendingSave, 0);

            if (request != 0)
            {
                SaveNow(force: request == 2);
            }
        }

        /// <summary>
        /// Saves the registered cache and unregisters it atomically, so no other thread
        /// can start a save between the last save and the cache destruction.
        /// </summary>
        public static void SaveNowAndUnregister(bool force)
        {
            lock (_gate)
            {
                if (_saveHook == null)
                {
                    return;
                }

                _lastSaveTick = Environment.TickCount64;

                try
                {
                    _saveHook();
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Pipeline cache: save failed. {ex.Message}");
                }
                finally
                {
                    _saveHook = null;
                    Volatile.Write(ref _pendingSave, 0);
                }
            }
        }

        /// <summary>
        /// Full path of the cache file for the game being run. Null or empty disables persistence.
        /// </summary>
        public static string FilePath { get; set; }

        /// <summary>
        /// Saves the registered pipeline cache now. Does nothing if no cache is registered.
        /// </summary>
        /// <param name="force">True to ignore the minimum interval between saves</param>
        public static void SaveNow(bool force)
        {
            lock (_gate)
            {
                if (_saveHook == null)
                {
                    return;
                }

                long now = Environment.TickCount64;

                if (!force && now - _lastSaveTick < MinSaveIntervalMs)
                {
                    return;
                }

                _lastSaveTick = now;

                try
                {
                    _saveHook();
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Pipeline cache: save failed. {ex.Message}");
                }
            }
        }

        internal static void Register(Action saveHook)
        {
            lock (_gate)
            {
                _saveHook = saveHook;
            }
        }

        internal static void Unregister()
        {
            lock (_gate)
            {
                _saveHook = null;
            }
        }

        /// <summary>
        /// Reads the stored cache data, if there is a valid file.
        /// </summary>
        /// <returns>The raw pipeline cache data, or null</returns>
        internal static byte[] TryLoad()
        {
            string path = FilePath;

            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                FileInfo info = new(path);

                if (!info.Exists || info.Length <= HeaderSize || info.Length > MaxPayloadBytes + HeaderSize)
                {
                    return null;
                }

                byte[] file = File.ReadAllBytes(path);

                uint magic = BitConverter.ToUInt32(file, 0);
                uint version = BitConverter.ToUInt32(file, 4);
                long payloadLength = BitConverter.ToInt64(file, 8);
                ulong checksum = BitConverter.ToUInt64(file, 16);

                if (magic != Magic || version != FormatVersion || payloadLength != file.Length - HeaderSize)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Pipeline cache: unknown file format, ignoring it");
                    return null;
                }

                byte[] payload = new byte[payloadLength];
                System.Buffer.BlockCopy(file, HeaderSize, payload, 0, payload.Length);

                if (Checksum(payload) != checksum)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Pipeline cache: checksum mismatch, ignoring the file");
                    return null;
                }

                Logger.Info?.Print(LogClass.Gpu, $"Pipeline cache: loaded {payload.Length / 1024} KiB from disk");

                return payload;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Pipeline cache: could not read the file. {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Writes the data of a Vulkan pipeline cache to disk.
        /// </summary>
        internal static unsafe void Save(Vk api, Device device, PipelineCache cache)
        {
            string path = FilePath;

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            nuint size = 0;

            if (api.GetPipelineCacheData(device, cache, &size, null) != Result.Success || size == 0 || size > (nuint)MaxPayloadBytes)
            {
                return;
            }

            byte[] payload = new byte[(int)size];

            fixed (byte* pPayload = payload)
            {
                if (api.GetPipelineCacheData(device, cache, &size, pPayload) != Result.Success)
                {
                    return;
                }
            }

            if ((int)size != payload.Length)
            {
                Array.Resize(ref payload, (int)size);
            }

            byte[] file = new byte[HeaderSize + payload.Length];
            BitConverter.TryWriteBytes(new Span<byte>(file, 0, 4), Magic);
            BitConverter.TryWriteBytes(new Span<byte>(file, 4, 4), FormatVersion);
            BitConverter.TryWriteBytes(new Span<byte>(file, 8, 8), (long)payload.Length);
            BitConverter.TryWriteBytes(new Span<byte>(file, 16, 8), Checksum(payload));
            System.Buffer.BlockCopy(payload, 0, file, HeaderSize, payload.Length);

            string directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + ".tmp";

            File.WriteAllBytes(temp, file);
            File.Move(temp, path, true);

            Logger.Info?.Print(LogClass.Gpu, $"Pipeline cache: saved {payload.Length / 1024} KiB");

            Prune(directory, path);
        }

        /// <summary>
        /// Builds the cache file path for a game, based on the path of the game file.
        /// </summary>
        /// <param name="baseDirectory">Directory where the cache files are stored</param>
        /// <param name="gamePath">Path of the game file</param>
        /// <returns>Full path of the cache file</returns>
        public static string BuildPath(string baseDirectory, string gamePath)
        {
            // FNV-1a, because string.GetHashCode() is randomized for every process.
            ulong hash = 14695981039346656037UL;

            // Hash the path relative to the app data directory when possible: on iOS the
            // application container UUID changes on every reinstall, which would otherwise
            // invalidate every stored cache.
            string hashedPath = NormalizeGamePath(gamePath);

            foreach (char c in hashedPath)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }

            return Path.Combine(baseDirectory, $"{hash:x16}.vkcache");
        }

        private static string NormalizeGamePath(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath))
            {
                return string.Empty;
            }

            try
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                if (!string.IsNullOrEmpty(documents))
                {
                    string fullPath = Path.GetFullPath(gamePath);
                    string root = Path.GetFullPath(documents).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                    if (fullPath.StartsWith(root, StringComparison.Ordinal))
                    {
                        return fullPath[root.Length..];
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to the unmodified path below.
            }

            return gamePath;
        }

        private static ulong Checksum(byte[] data)
        {
            ulong hash = 14695981039346656037UL;

            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= 1099511628211UL;
            }

            return hash;
        }

        private static void Prune(string directory, string keepPath)
        {
            try
            {
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                DateTime staleTempThreshold = DateTime.UtcNow - TimeSpan.FromHours(1);

                // Remove temporary files left behind by an interrupted save. They never
                // match the "*.vkcache" pattern and would otherwise accumulate forever.
                foreach (FileInfo temp in new DirectoryInfo(directory).GetFiles("*.vkcache.tmp"))
                {
                    try
                    {
                        if (temp.LastWriteTimeUtc < staleTempThreshold)
                        {
                            temp.Delete();
                        }
                    }
                    catch (Exception)
                    {
                        // Best effort only.
                    }
                }

                FileInfo[] files = new DirectoryInfo(directory)
                    .GetFiles("*.vkcache")
                    .Where(f => !string.Equals(f.FullName, Path.GetFullPath(keepPath), StringComparison.Ordinal))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToArray();

                long total = files.Sum(f => f.Length) + new FileInfo(keepPath).Length;

                foreach (FileInfo file in files)
                {
                    if (total <= MaxFolderBytes)
                    {
                        break;
                    }

                    total -= file.Length;
                    file.Delete();
                }
            }
            catch (Exception)
            {
                // Pruning is best effort only.
            }
        }
    }
}
