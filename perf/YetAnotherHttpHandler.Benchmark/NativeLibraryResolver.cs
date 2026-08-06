using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace YahaBenchmark;

/// <summary>
/// Resolves the native library out of the <c>runtimes/&lt;rid&gt;/native/</c> layout that
/// YetAnotherHttpHandler.csproj copies into the build output. Mirrors the resolver the test project
/// installs; without it the P/Invokes would look for the library next to the executable.
/// </summary>
internal static class NativeLibraryResolver
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(Cysharp.Net.Http.YetAnotherHttpHandler).Assembly,
            static (name, assembly, path) =>
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

                return NativeLibrary.Load(Path.Combine(
                    AppContext.BaseDirectory, "runtimes", $"{platform}-{arch}", "native", $"{prefix}{name}{ext}"));
            });
    }
}
