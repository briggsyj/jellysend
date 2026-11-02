using System.Runtime.InteropServices;
using System.Text;

namespace Slopsoil.Discord.Dave;

/// <summary>Buffer marshalling for libdave's C API (kept outside <c>Native/</c> so it is unit-tested).</summary>
internal static unsafe class DaveMarshal
{
    /// <summary>
    /// Copy a library-allocated <c>uint8_t*</c>/<c>size_t</c> output pair into a managed array and release
    /// the native buffer with <paramref name="free"/> (<c>daveFree</c>). A null pointer yields an empty array.
    /// </summary>
    public static byte[] TakeBytes(IntPtr data, nuint length, Action<IntPtr> free)
    {
        if (data == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            return new ReadOnlySpan<byte>((void*)data, checked((int)length)).ToArray();
        }
        finally
        {
            free(data);
        }
    }

    /// <summary>A NUL-terminated UTF-8 <c>const char**</c> array owned by managed code.</summary>
    public sealed class Utf8StringArray : IDisposable
    {
        private readonly int _count;
        private byte** _pointer;

        public Utf8StringArray(IReadOnlyList<string> values)
        {
            _count = values.Count;
            _pointer = (byte**)NativeMemory.AllocZeroed((nuint)Math.Max(_count, 1), (nuint)sizeof(byte*));
            for (var i = 0; i < _count; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(values[i]);
                var str = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
                bytes.CopyTo(new Span<byte>(str, bytes.Length));
                str[bytes.Length] = 0;
                _pointer[i] = str;
            }
        }

        public byte** Pointer
        {
            get
            {
                ObjectDisposedException.ThrowIf(_pointer == null, this);
                return _pointer;
            }
        }

        public nuint Count => (nuint)_count;

        public void Dispose()
        {
            if (_pointer == null)
            {
                return;
            }

            for (var i = 0; i < _count; i++)
            {
                NativeMemory.Free(_pointer[i]);
            }

            NativeMemory.Free(_pointer);
            _pointer = null;
        }
    }
}
