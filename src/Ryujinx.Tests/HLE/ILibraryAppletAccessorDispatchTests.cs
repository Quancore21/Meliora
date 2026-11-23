using NUnit.Framework;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Services;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// Verifies the IPC dispatch wiring of ILibraryAppletAccessor command 90
    /// (SetLibraryAppletGpuTimeSliceBoost, 20.0.0+) without a running HLE context:
    /// the command must be registered, must read exactly four u64s from the request,
    /// must accept the all-zero request the Software Keyboard applet sends, and must
    /// fail loudly on any non-zero value instead of silently ignoring it.
    /// </summary>
    [TestFixture]
    public class ILibraryAppletAccessorDispatchTests
    {
        private static object CreateUninitializedService()
        {
            // The generated dispatch methods only inspect their arguments, so the service
            // constructor (which requires a live Switch device) is not needed for dispatch.
            return RuntimeHelpers.GetUninitializedObject(
                typeof(Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.LibraryAppletCreator.ILibraryAppletAccessor));
        }

        private static ServiceCtx BuildContext(MemoryStream requestStream, MemoryStream responseStream)
        {
            IpcMessage request = new()
            {
                HandleDesc = new IpcHandleDesc(Array.Empty<int>(), Array.Empty<int>(), 0),
            };
            IpcMessage response = new();

            return new ServiceCtx(
                null,
                null,
                null,
                null,
                request,
                response,
                new BinaryReader(requestStream),
                new BinaryWriter(responseStream));
        }

        [Test]
        public void Command90IsSetLibraryAppletGpuTimeSliceBoost()
        {
            object service = CreateUninitializedService();

            int id = ((IpcService)service).CmifCommandIdByMethodName("SetLibraryAppletGpuTimeSliceBoost");

            Assert.That(id, Is.EqualTo(90));
        }

        [Test]
        public void Command90AllZeroInputReturnsSuccess()
        {
            // The exact request shape observed in the field: SFCI header, command id 0x5A,
            // then four all-zero u64s from the Software Keyboard applet.
            MemoryStream requestStream = new();
            MemoryStream responseStream = new();

            using (BinaryWriter writer = new(requestStream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(IpcMagic.Sfci);
                writer.Write(90L); // Command id
                writer.Write(0UL);
                writer.Write(0UL);
                writer.Write(0UL);
                writer.Write(0UL);
            }

            requestStream.Seek(0, SeekOrigin.Begin);

            ServiceCtx context = BuildContext(requestStream, responseStream);

            ((IpcService)CreateUninitializedService()).CallCmifMethod(context);

            // The response is only the SFCO header plus the result code: no output payload.
            byte[] responseData = responseStream.ToArray();

            Assert.That(responseData.Length, Is.EqualTo(0x10));
            Assert.That(BitConverter.ToInt64(responseData, 0), Is.EqualTo(IpcMagic.Sfco));
            Assert.That(BitConverter.ToInt64(responseData, 8), Is.EqualTo(0L)); // ResultCode.Success

            // Exactly the SFCI header (0x10) plus 32 input bytes were consumed.
            Assert.That(requestStream.Position, Is.EqualTo(0x30));
        }

        [Test]
        public void Command90NonZeroInputFails()
        {
            MemoryStream requestStream = new();
            MemoryStream responseStream = new();

            using (BinaryWriter writer = new(requestStream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(IpcMagic.Sfci);
                writer.Write(90L); // Command id
                writer.Write(0UL);
                writer.Write(0x1234UL); // Any non-zero value must not be silently ignored
                writer.Write(0UL);
                writer.Write(0UL);
            }

            requestStream.Seek(0, SeekOrigin.Begin);

            ServiceCtx context = BuildContext(requestStream, responseStream);

            Assert.Throws<Ryujinx.HLE.Exceptions.ServiceNotImplementedException>(
                () => ((IpcService)CreateUninitializedService()).CallCmifMethod(context));
        }

        [Test]
        public void Command90IsNotClaimedByAnotherMethod()
        {
            object service = CreateUninitializedService();

            foreach (var method in typeof(Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.LibraryAppletCreator.ILibraryAppletAccessor).GetMethods())
            {
                if (method.Name == "SetLibraryAppletGpuTimeSliceBoost" || method.Name.StartsWith("get_") || method.Name.StartsWith("set_"))
                {
                    continue;
                }

                int id = ((IpcService)service).CmifCommandIdByMethodName(method.Name);

                Assert.That(id, Is.Not.EqualTo(90), $"Method {method.Name} is also mapped to command 90");
            }
        }
    }
}
