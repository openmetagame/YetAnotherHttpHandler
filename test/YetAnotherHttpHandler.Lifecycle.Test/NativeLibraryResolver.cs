using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace _YetAnotherHttpHandler.Lifecycle.Test;

internal static class NativeLibraryResolver
{
    [ModuleInitializer]
    public static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(Cysharp.Net.Http.YetAnotherHttpHandler).Assembly,
            static (name, assembly, path) => Resolve(name));
    }

    private static nint Resolve(string name)
    {
        if (!name.Contains("yaha_native") && !name.Contains("Cysharp.Net.Http.YetAnotherHttpHandler.Native"))
        {
            return nint.Zero;
        }

        var (platform, prefix, ext) =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ("win", "", ".dll") :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ("osx", "lib", ".dylib") :
            ("linux", "lib", ".so");

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => throw new NotSupportedException($"Unsupported architecture: {RuntimeInformation.OSArchitecture}"),
        };

        return NativeLibrary.Load(Path.Combine($"runtimes/{platform}-{arch}/native/{prefix}{name}{ext}"));
    }
}
