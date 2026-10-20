using System;
using System.Runtime.InteropServices;

namespace PstBrowser.Reader.MacOS
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct CGRect
    {
        public double X, Y, Width, Height;
        public CGRect(double x, double y, double w, double h) { X = x; Y = y; Width = w; Height = h; }
    }

    /// <summary>
    /// Minimal Objective-C runtime bridge (no Xamarin / .NET for macOS workload): classes, selectors, messages,
    /// classes defined at run time for the delegates, and blocks (closures of Apple's C extension).
    /// </summary>
    internal static unsafe class ObjC
    {
        private const string Lib = "/usr/lib/libobjc.A.dylib";

        [DllImport(Lib)] public static extern IntPtr objc_getClass(string name);
        [DllImport(Lib)] public static extern IntPtr objc_getProtocol(string name);
        [DllImport(Lib)] public static extern IntPtr sel_registerName(string name);
        [DllImport(Lib)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
        [DllImport(Lib)] public static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
        [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
        [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

        // objc_msgSend must be called with the exact prototype of each method (arm64): one import per shape
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, long b);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, long b, IntPtr c);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, long c, IntPtr d);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, CGRect r, IntPtr a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, CGRect r, ulong a, ulong b, [MarshalAs(UnmanagedType.I1)] bool c);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, long a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern long SendLong(IntPtr self, IntPtr sel);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SendBool(IntPtr self, IntPtr sel, IntPtr a);

        public static IntPtr Class(string name) => objc_getClass(name);
        public static IntPtr Sel(string name) => sel_registerName(name);
        public static IntPtr Msg(IntPtr self, string sel) => Send(self, Sel(sel));
        public static IntPtr Msg(IntPtr self, string sel, IntPtr a) => Send(self, Sel(sel), a);
        public static IntPtr New(string cls) => Msg(Msg(Class(cls), "alloc"), "init");
        public static bool Responds(IntPtr obj, string sel) => obj != IntPtr.Zero && SendBool(obj, Sel("respondsToSelector:"), Sel(sel));

        public static IntPtr NSString(string s)
        {
            var utf8 = Marshal.StringToCoTaskMemUTF8(s ?? "");
            try { return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8); }
            finally { Marshal.FreeCoTaskMem(utf8); }
        }

        public static string FromNSString(IntPtr s) => s == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Msg(s, "UTF8String"));

        public static IntPtr NSUrl(string url) => Send(Class("NSURL"), Sel("URLWithString:"), NSString(url));

        /// <summary>Absolute string of the URL of an NSURLRequest.</summary>
        public static string RequestUrl(IntPtr request) => FromNSString(Msg(Msg(request, "URL"), "absoluteString"));

        public static IntPtr NSData(byte[] data)
        {
            fixed (byte* p = data) return Send(Class("NSData"), Sel("dataWithBytes:length:"), (IntPtr)p, (long)data.Length);
        }

        /// <summary>A class defined at run time (delegates), with its methods and protocols.</summary>
        public static IntPtr DefineClass(string name, string superclass, string[] protocols, params (string sel, IntPtr imp, string types)[] methods)
        {
            var existing = objc_getClass(name);
            if (existing != IntPtr.Zero) return existing;
            var cls = objc_allocateClassPair(objc_getClass(superclass), name, 0);
            foreach (var p in protocols)
            {
                var proto = objc_getProtocol(p);
                if (proto != IntPtr.Zero) class_addProtocol(cls, proto);
            }
            foreach (var (sel, imp, types) in methods) class_addMethod(cls, Sel(sel), imp, types);
            objc_registerClassPair(cls);
            return cls;
        }

        // ------------------------------------------------------------------ blocks

        [StructLayout(LayoutKind.Sequential)]
        private struct BlockDescriptor { public ulong Reserved, Size; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlockLiteral
        {
            public IntPtr Isa;
            public int Flags, Reserved;
            public IntPtr Invoke;
            public BlockDescriptor* Descriptor;
            public IntPtr Context; // ours: read back by the invoke function
        }

        private const int BlockIsGlobal = 1 << 28;
        private static IntPtr _globalBlockIsa;

        /// <summary>
        /// A block without captured variables (global block, never copied nor released) calling <paramref name="invoke"/>
        /// with the block pointer as first argument; <see cref="BlockContext"/> gives back <paramref name="context"/>.
        /// The memory is kept for the life of the process: create such blocks once.
        /// </summary>
        public static IntPtr Block(IntPtr invoke, IntPtr context)
        {
            if (_globalBlockIsa == IntPtr.Zero)
                _globalBlockIsa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
            var desc = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
            desc->Size = (ulong)sizeof(BlockLiteral);
            var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
            block->Isa = _globalBlockIsa;
            block->Flags = BlockIsGlobal;
            block->Invoke = invoke;
            block->Descriptor = desc;
            block->Context = context;
            return (IntPtr)block;
        }

        public static IntPtr BlockContext(IntPtr block) => ((BlockLiteral*)block)->Context;

        /// <summary>Calls a block received from Objective-C taking one integer (e.g. a WKNavigationActionPolicy decision handler).</summary>
        public static void InvokeBlock(IntPtr block, long value)
            => ((delegate* unmanaged<IntPtr, long, void>)((BlockLiteral*)block)->Invoke)(block, value);
    }
}
