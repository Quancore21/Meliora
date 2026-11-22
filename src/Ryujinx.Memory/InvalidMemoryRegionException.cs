using System;
using System.Globalization;

namespace Ryujinx.Memory
{
    public class InvalidMemoryRegionException : Exception
    {
        private readonly bool _hasDetails;
        private readonly ulong _offset;
        private readonly ulong _vaEstimate;
        private readonly ulong _size;
        private readonly bool _write;
        private readonly ulong _tableSize;
        private readonly ulong _hostPageSize;

        public InvalidMemoryRegionException() : base("Attempted to access an invalid memory region.")
        {
        }

        public InvalidMemoryRegionException(string message) : base(message)
        {
        }

        public InvalidMemoryRegionException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Meliora: captures the NativePageTable fault context without formatting, allocating
        /// or logging. The text is built only when <see cref="Message"/> is read, which happens
        /// after the signal handler has returned.
        /// </summary>
        public InvalidMemoryRegionException(
            ulong offset,
            ulong vaEstimate,
            ulong size,
            bool write,
            ulong tableSize,
            ulong hostPageSize)
        {
            _hasDetails = true;
            _offset = offset;
            _vaEstimate = vaEstimate;
            _size = size;
            _write = write;
            _tableSize = tableSize;
            _hostPageSize = hostPageSize;
        }

        public override string Message
        {
            get
            {
                if (!_hasDetails)
                {
                    return base.Message;
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Attempted to access an invalid memory region. offset=0x{0:X} va=0x{1:X} size=0x{2:X} write={3} tableSize=0x{4:X} hostPageSize=0x{5:X}",
                    _offset,
                    _vaEstimate,
                    _size,
                    _write,
                    _tableSize,
                    _hostPageSize);
            }
        }
    }
}
