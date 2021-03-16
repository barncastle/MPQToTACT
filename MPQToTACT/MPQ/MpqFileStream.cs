using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using MPQToTACT.Helpers;
using MPQToTACT.MPQ.Native;

namespace MPQToTACT.MPQ
{
    public class MpqFileStream : Stream
    {
        private MpqFileSafeHandle _handle;
        private readonly FileAccess _accessType;
        private MpqArchive _owner;

        public readonly string FileName;
        public readonly uint Flags;

        internal unsafe MpqFileStream(MpqFileSafeHandle handle, FileAccess accessType, MpqArchive owner)
        {
            var header = (_TMPQFileHeader*)handle.DangerousGetHandle().ToPointer();

            Flags = header->pFileEntry->dwFlags;
            FileName = Marshal.PtrToStringAnsi(header->pFileEntry->szFileName).WoWNormalise();

            _handle = handle;
            _accessType = accessType;
            _owner = owner;
        }

        private void VerifyHandle()
        {
            if (!IsVerifiedHandle())
                throw new ObjectDisposedException("MpqFileStream");
        }

        private bool IsVerifiedHandle()
        {
            return !(_handle == null || _handle.IsInvalid || _handle.IsClosed);
        }

        public override bool CanRead => IsVerifiedHandle();

        public override bool CanSeek => IsVerifiedHandle();

        public override bool CanWrite => IsVerifiedHandle() && _accessType != FileAccess.Read;

        public override void Flush()
        {
            VerifyHandle();

            _owner.Flush();
        }

        public override long Length
        {
            get
            {
                if (IsVerifiedHandle())
                {
                    uint high = 0;
                    var low = NativeMethods.SFileGetFileSize(_handle, ref high);

                    ulong val = (high << 32) | low;
                    return unchecked((long)val);
                }

                return 0;
            }
        }

        public override long Position
        {
            get
            {
                VerifyHandle();
                return NativeMethods.SFileGetFilePointer(_handle);
            }
            set => Seek(value, SeekOrigin.Begin);
        }

        public DateTime? CreatedDate
        {
            get => IsVerifiedHandle() ? NativeMethods.SFileGetFileTime(_handle) : null;
        }

        public override unsafe int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset > buffer.Length || (offset + count) > buffer.Length)
                throw new ArgumentException("offset > buffer.Length");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            VerifyHandle();

            bool success;
            uint read;
            fixed (byte* pb = &buffer[offset])
            {
                NativeOverlapped overlapped = default;
                success = NativeMethods.SFileReadFile(_handle, new IntPtr(pb), unchecked((uint)count), out read, ref overlapped);
            }

            // StormLib fails bounds checks
            if (!success && Position != Length)
                throw new Exception("Unable to read file");

            return unchecked((int)read);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            VerifyHandle();

            var low = unchecked((uint)(offset & 0xffffffffu));
            var high = unchecked((uint)(offset >> 32));
            return NativeMethods.SFileSetFilePointer(_handle, low, ref high, (uint)origin);
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override unsafe void Write(byte[] buffer, int offset, int count)
        {
            VerifyHandle();

            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset > buffer.Length || (offset + count) > buffer.Length)
                throw new ArgumentException("offset > buffer.Length");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            VerifyHandle();

            bool success;
            fixed (byte* pb = &buffer[offset])
            {
                success = NativeMethods.SFileWriteFile(_handle, new IntPtr(pb), unchecked((uint)count), 0u);
            }

            if (!success)
                throw new Win32Exception();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                if (_handle != null && !_handle.IsInvalid)
                {
                    _handle.Close();
                    _handle = null;
                }

                if (_owner != null)
                {
                    _owner.RemoveOwnedFile(this);
                    _owner = null;
                }
            }
        }

        public string GetMD5Hash()
        {
            if (IsVerifiedHandle())
            {
                return NativeMethods.SFileGetFileHash(_handle);
            }
            else
            {
                return null;
            }
        }
    }
}
