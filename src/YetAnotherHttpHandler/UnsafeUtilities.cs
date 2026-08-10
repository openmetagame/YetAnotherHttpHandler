using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Cysharp.Net.Http
{
    internal static class UnsafeUtilities
    {
        /// <summary>
        /// The name the native runtime gives to every tokio worker thread.
        /// </summary>
        /// <remarks>
        /// This must stay in sync with <c>WORKER_THREAD_NAME</c> in
        /// <c>native/yaha_native/src/context.rs</c>. It is pinned on the Rust side rather than taken
        /// from tokio's default, because tokio renamed that default (1.38 "tokio-runtime-worker" ->
        /// "tokio-rt-worker").
        /// <para>
        /// This is only used by tests that count runtime threads. Do NOT use it to decide whether
        /// the current thread belongs to the native runtime - call
        /// <see cref="IsRunningOnNativeRuntimeThread"/> instead, which asks tokio directly and works
        /// on every platform.
        /// </para>
        /// </remarks>
        public const string WorkerThreadName = "yaha-rt-worker";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe static string GetStringFromUtf8Bytes(ReadOnlySpan<byte> bytes)
        {
#if !NETSTANDARD2_0
            return Encoding.UTF8.GetString(bytes);
#else
            fixed (byte* p = bytes)
            {
                return Encoding.UTF8.GetString(p, bytes.Length);
            }
#endif
        }

        // System.Text.Ascii.EqualsIgnoreCase
        public static bool EqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
            => left.Length == right.Length && EqualsIgnoreCase(ref MemoryMarshal.GetReference(left), ref MemoryMarshal.GetReference(right), (uint)left.Length);
        public static bool EqualsIgnoreCase(ref byte left, ref byte right, uint length)
        {
            for (nuint i = 0; i < length; ++i)
            {
                uint valueA = unchecked((uint)(Unsafe.Add(ref left, (nint)i)));
                uint valueB = unchecked((uint)(Unsafe.Add(ref right, (nint)i)));

                if (!IsAsciiCodePoint(valueA | valueB))
                {
                    return false;
                }

                if (valueA == valueB)
                {
                    continue; // exact match
                }

                valueA |= 0x20u;
                if (valueA - 'a' > 'z' - 'a')
                {
                    return false; // not exact match, and first input isn't in [A-Za-z]
                }

                if (valueA != (valueB | 0x20u))
                {
                    return false;
                }
            }

            return true;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool IsAsciiCodePoint(uint value) => value <= 0x7Fu;
        }

        /// <summary>
        /// Returns <see langword="true"/> when the calling thread belongs to the native tokio
        /// runtime (a worker thread or a blocking-pool thread).
        /// </summary>
        /// <remarks>
        /// Releasing native handles from a runtime thread can free the native request context while
        /// the runtime is still using it, which corrupts memory rather than failing cleanly.
        /// <para>
        /// This used to be inferred from the OS thread name via <c>GetThreadDescription</c>, which
        /// only exists on Windows - so on Linux, macOS, iOS and Android the check silently did
        /// nothing, and Android is precisely where the mistake is most expensive. Asking tokio
        /// directly (<c>Handle::try_current()</c>) works everywhere and also catches blocking-pool
        /// threads, which carry a different name.
        /// </para>
        /// <para>
        /// This method is always compiled, unlike <see cref="RequireRunningOnManagedThread"/>.
        /// </para>
        /// </remarks>
        public static bool IsRunningOnNativeRuntimeThread()
        {
            try
            {
                return NativeMethods.yaha_is_runtime_thread();
            }
            catch (Exception)
            {
                // The native library may not be loaded yet, or may already be unloaded during
                // shutdown. Neither is a runtime thread.
                return false;
            }
        }

        /// <summary>
        /// Fail-fasts if the calling thread belongs to the native runtime. Debug builds only; see
        /// <see cref="IsRunningOnNativeRuntimeThread"/> for the always-on check.
        /// </summary>
        [Conditional("DEBUG")]
        public static void RequireRunningOnManagedThread()
        {
            if (IsRunningOnNativeRuntimeThread())
            {
                Environment.FailFast($"The current thread is a native runtime (tokio) thread.");
            }
        }
    }

    internal readonly ref struct TempUtf8String
    {
        private readonly byte[]? _buffer;
        private readonly ReadOnlySpan<byte> _span;

        public ReadOnlySpan<byte> Span => _span;

        public TempUtf8String(string s)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(s.Length));
#if NETSTANDARD2_0
            var written = Encoding.UTF8.GetBytes(s, 0, s.Length, _buffer, 0);
#else
            var written = Encoding.UTF8.GetBytes(s, _buffer.AsSpan());
#endif
            _span = _buffer.AsSpan(0, written);
        }

        public TempUtf8String(ReadOnlySpan<byte> s)
        {
            _buffer = null;
            _span = s;
        }

        public void Dispose()
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
        }
    }
}
