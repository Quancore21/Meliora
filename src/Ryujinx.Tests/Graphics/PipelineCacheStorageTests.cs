using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using System;
using System.Buffers.Binary;
using System.IO;

namespace Ryujinx.Tests.Graphics
{
    [TestFixture]
    [NonParallelizable]
    public class PipelineCacheStorageTests
    {
        private string _directory;
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "meliora-pipeline-cache-tests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_directory);

            _path = Path.Combine(_directory, "test.vkcache");

            PipelineCacheStorage.FilePath = _path;
        }

        [TearDown]
        public void TearDown()
        {
            PipelineCacheStorage.FilePath = null;

            try
            {
                Directory.Delete(_directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        [Test]
        public void RoundTripWithPartialPayload()
        {
            byte[] payload = CreatePayload(256);
            const int PayloadLength = 100;

            PipelineCacheStorage.WriteCacheFile(_path, payload, PayloadLength);

            byte[] loaded = PipelineCacheStorage.TryLoad();

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.Length, Is.EqualTo(PayloadLength));
            Assert.That(loaded, Is.EqualTo(payload.AsSpan(0, PayloadLength).ToArray()));
            Assert.That(new FileInfo(_path).Length, Is.EqualTo(PipelineCacheStorage.HeaderSize + PayloadLength));
            Assert.That(File.Exists(_path + ".tmp"), Is.False);
        }

        [Test]
        public void FullPayloadRoundTrip()
        {
            byte[] payload = CreatePayload(4096);

            PipelineCacheStorage.WriteCacheFile(_path, payload, payload.Length);

            byte[] loaded = PipelineCacheStorage.TryLoad();

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded, Is.EqualTo(payload));
        }

        [Test]
        public void HeaderContainsExpectedValues()
        {
            byte[] payload = CreatePayload(128);
            const int PayloadLength = 77;

            PipelineCacheStorage.WriteCacheFile(_path, payload, PayloadLength);

            byte[] raw = File.ReadAllBytes(_path);

            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)), Is.EqualTo(PipelineCacheStorage.Magic));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4)), Is.EqualTo(PipelineCacheStorage.FormatVersion));
            Assert.That(BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(8, 8)), Is.EqualTo(PayloadLength));
            Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(16, 8)), Is.EqualTo(Fnv1a(raw.AsSpan(PipelineCacheStorage.HeaderSize, PayloadLength))));
            Assert.That(raw.Length, Is.EqualTo(PipelineCacheStorage.HeaderSize + PayloadLength));
        }

        [Test]
        public void ChecksumMismatchIsACacheMiss()
        {
            WriteValidFile();

            byte[] raw = File.ReadAllBytes(_path);
            raw[PipelineCacheStorage.HeaderSize] ^= 0xFF;
            File.WriteAllBytes(_path, raw);

            Assert.That(PipelineCacheStorage.TryLoad(), Is.Null);
        }

        [Test]
        public void TruncatedPayloadIsACacheMiss()
        {
            WriteValidFile();

            using (FileStream stream = new(_path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(PipelineCacheStorage.HeaderSize + 20);
            }

            Assert.That(PipelineCacheStorage.TryLoad(), Is.Null);
        }

        [Test]
        public void TruncatedHeaderIsACacheMiss()
        {
            WriteValidFile();

            using (FileStream stream = new(_path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(PipelineCacheStorage.HeaderSize - 1);
            }

            Assert.That(PipelineCacheStorage.TryLoad(), Is.Null);
        }

        [TestCase(0u, 1u, 100L)] // bad magic
        [TestCase(0x504C454Du, 2u, 100L)] // bad version
        [TestCase(0x504C454Du, 1u, 99L)] // size does not match the file
        [TestCase(0x504C454Du, 1u, -1L)] // negative size
        public void InvalidHeaderIsACacheMiss(uint magic, uint version, long payloadLength)
        {
            WriteValidFile();

            byte[] raw = File.ReadAllBytes(_path);

            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0, 4), magic);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4, 4), version);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(8, 8), payloadLength);

            File.WriteAllBytes(_path, raw);

            Assert.That(PipelineCacheStorage.TryLoad(), Is.Null);
        }

        [Test]
        public void PayloadOverLimitIsACacheMiss()
        {
            WriteValidFile();

            byte[] raw = File.ReadAllBytes(_path);

            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(8, 8), PipelineCacheStorage.MaxPayloadBytes + 1);

            File.WriteAllBytes(_path, raw);

            Assert.That(PipelineCacheStorage.TryLoad(), Is.Null);
        }

        [Test]
        public void EmptyPayloadFileIsACacheMiss()
        {
            PipelineCacheStorage.WriteCacheFile(_path, Array.Empty<byte>(), 0);

            Assert.That(PipelineCacheStorage.TryLoad(), Is.Null);
        }

        [Test]
        public void MissingFileIsACacheMiss()
        {
            PipelineCacheStorage.FilePath = Path.Combine(_directory, "missing.vkcache");

            Assert.That(PipelineCacheStorage.TryLoad(), Is.Null);
        }

        [Test]
        public void EmptyFilePathIsACacheMiss()
        {
            PipelineCacheStorage.FilePath = string.Empty;

            Assert.That(PipelineCacheStorage.TryLoad(), Is.Null);
        }

        [Test]
        public void WriteRejectsPayloadLengthBeyondBuffer()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PipelineCacheStorage.WriteCacheFile(_path, new byte[8], 9));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipelineCacheStorage.WriteCacheFile(_path, new byte[8], -1));
        }

        [Test]
        public void WriteRejectsNullPayloadAndPath()
        {
            Assert.Throws<ArgumentNullException>(() => PipelineCacheStorage.WriteCacheFile(_path, null, 0));
            Assert.Throws<ArgumentException>(() => PipelineCacheStorage.WriteCacheFile(string.Empty, new byte[1], 1));
        }

        private void WriteValidFile()
        {
            byte[] payload = CreatePayload(256);

            PipelineCacheStorage.WriteCacheFile(_path, payload, 100);
        }

        private static byte[] CreatePayload(int length)
        {
            byte[] payload = new byte[length];

            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i * 31 + 7);
            }

            return payload;
        }

        private static ulong Fnv1a(ReadOnlySpan<byte> data)
        {
            ulong hash = 14695981039346656037UL;

            foreach (byte value in data)
            {
                hash ^= value;
                hash *= 1099511628211UL;
            }

            return hash;
        }
    }
}
