using NUnit.Framework;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Ipc;
using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// Verifies the IPC dispatch wiring of IHidServer command 92 (SetGestureOutputRanges,
    /// 18.0.0+) without a running HLE context: the command must be registered, must read
    /// exactly pid + ushort from the request, and must return Success with no output data.
    /// </summary>
    [TestFixture]
    public class IHidServerDispatchTests
    {
        private static object CreateUninitializedService()
        {
            // The generated dispatch methods only inspect their arguments, so the service
            // constructor (which requires a live Switch device) is not needed for dispatch.
            return RuntimeHelpers.GetUninitializedObject(typeof(Ryujinx.HLE.HOS.Services.Hid.IHidServer));
        }

        private static ServiceCtx BuildContext(MemoryStream requestStream, MemoryStream responseStream, ulong pid)
        {
            IpcMessage request = new()
            {
                HandleDesc = new IpcHandleDesc(Array.Empty<int>(), Array.Empty<int>(), pid),
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
        public void Command92IsSetGestureOutputRanges()
        {
            object service = CreateUninitializedService();

            int id = ((Ryujinx.HLE.HOS.Services.IpcService)service).CmifCommandIdByMethodName("SetGestureOutputRanges");

            Assert.That(id, Is.EqualTo(92));
        }

        [Test]
        public void Command92ReturnsSuccessWithoutOutput()
        {
            const ulong Pid = 1234;

            MemoryStream requestStream = new();
            MemoryStream responseStream = new();

            using (BinaryWriter writer = new(requestStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(IpcMagic.Sfci);
                writer.Write(92L); // Command id
                writer.Write((ushort)0x1234); // SetGestureOutputRanges input
            }

            requestStream.Seek(0, SeekOrigin.Begin);

            ServiceCtx context = BuildContext(requestStream, responseStream, Pid);

            ((Ryujinx.HLE.HOS.Services.IpcService)CreateUninitializedService()).CallCmifMethod(context);

            // The response is only the SFCO header plus the result code: no output payload.
            byte[] responseData = responseStream.ToArray();

            Assert.That(responseData.Length, Is.EqualTo(0x10));
            Assert.That(BitConverter.ToInt64(responseData, 0), Is.EqualTo(IpcMagic.Sfco));
            Assert.That(BitConverter.ToInt64(responseData, 8), Is.EqualTo(0L)); // ResultCode.Success

            // Exactly pid + ushort were consumed: SFCI header (0x10) + 2 input bytes.
            Assert.That(requestStream.Position, Is.EqualTo(0x12));
        }

        [Test]
        public void Command92IsNotClaimedByAnotherMethod()
        {
            object service = CreateUninitializedService();

            foreach (var method in typeof(Ryujinx.HLE.HOS.Services.Hid.IHidServer).GetMethods())
            {
                if (method.Name == "SetGestureOutputRanges" || method.Name.StartsWith("get_") || method.Name.StartsWith("set_"))
                {
                    continue;
                }

                int id = ((Ryujinx.HLE.HOS.Services.IpcService)service).CmifCommandIdByMethodName(method.Name);

                Assert.That(id, Is.Not.EqualTo(92), $"Method {method.Name} is also mapped to command 92");
            }
        }
    }
}
