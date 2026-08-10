using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cysharp.Net.Http
{
    internal sealed unsafe class YahaRuntimeSafeHandle : SafeHandle
    {
        public override bool IsInvalid => handle == IntPtr.Zero;

        public YahaRuntimeSafeHandle(YahaNativeRuntimeContext* runtime)
            : base((IntPtr)runtime, true)
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public YahaNativeRuntimeContext* DangerousGet() => (YahaNativeRuntimeContext*)DangerousGetHandle();

        protected override bool ReleaseHandle()
        {
            if (YahaEventSource.Log.IsEnabled()) YahaEventSource.Log.Info($"yaha_dispose_runtime");
            NativeMethods.yaha_dispose_runtime((YahaNativeRuntimeContext*)handle);
            return true;
        }
    }

    internal sealed unsafe class YahaContextSafeHandle : SafeHandle
    {
        private YahaRuntimeSafeHandle? _parent;
        private readonly int _instanceId;
        private List<GCHandle>? _callbackStates;

        public override bool IsInvalid => handle == IntPtr.Zero;

        public YahaContextSafeHandle(YahaNativeContext* context, int instanceId)
            : base((IntPtr)context, true)
        {
            _instanceId = instanceId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public YahaNativeContext* DangerousGet() => (YahaNativeContext*)DangerousGetHandle();

        public void SetParent(YahaRuntimeSafeHandle parent)
        {
            var addRef = false;
            parent.DangerousAddRef(ref addRef);
            if (addRef)
            {
                _parent = parent;
            }
        }

        /// <summary>
        /// Transfers ownership of a callback-state <see cref="GCHandle"/> to this handle, which frees
        /// it once the native context has been disposed.
        /// </summary>
        /// <remarks>
        /// Callback states (the DNS resolver, the server certificate verifier) are handed to the
        /// native side as raw <see cref="IntPtr"/>s and dereferenced with
        /// <see cref="GCHandle.FromIntPtr"/> whenever the native side invokes the callback. They must
        /// therefore outlive every native user of the context.
        /// <para>
        /// They used to be freed in <c>NativeHttpHandlerCore.Dispose</c>, which is too early:
        /// disposing the handler while a request is still connecting left the native side holding a
        /// freed handle, and dereferencing one is undefined - it can hand back an unrelated object
        /// from a recycled slot. This handle's ref-count already tracks exactly the right lifetime,
        /// because every in-flight request holds a reference to it.
        /// </para>
        /// <para>
        /// Only called during construction, before the handle is shared, so no synchronisation is
        /// needed here.
        /// </para>
        /// </remarks>
        public void AddCallbackState(GCHandle callbackState)
        {
            (_callbackStates ??= new List<GCHandle>()).Add(callbackState);
        }

        protected override bool ReleaseHandle()
        {
            if (YahaEventSource.Log.IsEnabled()) YahaEventSource.Log.Info($"[Id:{_instanceId}] yaha_dispose_context");
            NativeMethods.yaha_dispose_context((YahaNativeContext*)handle);

            // After the native context is gone: nothing can look these up any more.
            if (_callbackStates is { } callbackStates)
            {
                foreach (var callbackState in callbackStates)
                {
                    if (callbackState.IsAllocated)
                    {
                        callbackState.Free();
                    }
                }
                callbackStates.Clear();
            }

            _parent?.DangerousRelease();

            return true;
        }
    }
    
    internal sealed unsafe class YahaRequestContextSafeHandle : SafeHandle
    {
        private YahaContextSafeHandle? _parent;
        private readonly int _requestSequence;

        public override bool IsInvalid => handle == IntPtr.Zero;

        public YahaRequestContextSafeHandle(YahaNativeRequestContext* context, int requestSequence)
            : base((IntPtr)context, true)
        {
            _requestSequence = requestSequence;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public YahaNativeRequestContext* DangerousGet() => (YahaNativeRequestContext*)DangerousGetHandle();

        public void SetParent(YahaContextSafeHandle parent)
        {
            var addRef = false;
            parent.DangerousAddRef(ref addRef);
            if (addRef)
            {
                _parent = parent;
            }
        }

        protected override bool ReleaseHandle()
        {
            if (_parent is null)
            {
                return false;
            }

            if (YahaEventSource.Log.IsEnabled()) YahaEventSource.Log.Info($"[ReqSeq:{_requestSequence}] yaha_request_destroy");
            NativeMethods.yaha_request_destroy((YahaNativeContext*)_parent.DangerousGetHandle(), (YahaNativeRequestContext*)handle);
            _parent.DangerousRelease();
            return true;
        }
    }
}
