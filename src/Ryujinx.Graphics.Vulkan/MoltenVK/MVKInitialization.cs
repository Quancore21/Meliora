using Silk.NET.Core.Loader;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Vulkan.MoltenVK
{
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("ios")]
    public static partial class MVKInitialization
    {
        private const string VulkanLib = "libvulkan.dylib";

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkGetMoltenVKConfigurationMVK(IntPtr unusedInstance, out MVKConfiguration config, in IntPtr configSize);

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkSetMoltenVKConfigurationMVK(IntPtr unusedInstance, in MVKConfiguration config, in IntPtr configSize);

        public static void Initialize()
        {
            var configSize = (IntPtr)Marshal.SizeOf<MVKConfiguration>();

            vkGetMoltenVKConfigurationMVK(IntPtr.Zero, out MVKConfiguration config, configSize);

            // config.UseMetalArgumentBuffers = true;

            //config.SemaphoreSupportStyle = MVKVkSemaphoreSupportStyle.MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_SINGLE_QUEUE;
            // config.SynchronousQueueSubmits = false;

            config.ResumeLostDevice = true;

            // Mirror MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS (set by the app's
            // "MVK: Prefill Metal Command Buffers" settings toggle). MoltenVK only reads
            // the environment the first time its dylib loads, so applying the value here
            // as well keeps the toggle effective between game launches even after
            // MoltenVK has already initialized.
            string prefill = Environment.GetEnvironmentVariable("MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS");
            config.PrefillMetalCommandBuffers = prefill == "1";

            vkSetMoltenVKConfigurationMVK(IntPtr.Zero, config, configSize);
        }

        /// <summary>
        /// Meliora: reads back the MoltenVK configuration as it is now in effect and
        /// formats the parts that matter for a game log. Called once per session by the
        /// "[Meliora] gpu:" log in VulkanRenderer.
        /// </summary>
        public static string DescribeEffectiveOptions()
        {
            try
            {
                var configSize = (IntPtr)Marshal.SizeOf<MVKConfiguration>();

                vkGetMoltenVKConfigurationMVK(IntPtr.Zero, out MVKConfiguration config, configSize);

                return $", mvk: argBuffers={(config.UseMetalArgumentBuffers ? 1 : 0)} prefill={(config.PrefillMetalCommandBuffers ? 1 : 0)}";
            }
            catch
            {
                // Logging must never break renderer initialization.
                return string.Empty;
            }
        }

        private static string[] Resolver(string path)
        {
            if (path.EndsWith(VulkanLib))
            {
                path = path[..^VulkanLib.Length] + "libMoltenVK.dylib";
                return [path];
            }
            return Array.Empty<string>();
        }

        public static void InitializeResolver()
        {
            ((DefaultPathResolver)PathResolver.Default).Resolvers.Insert(0, Resolver);
        }
    }
}
