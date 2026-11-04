using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Slopsoil.Discord.Native;

/// <summary>
/// Maps the logical native library names used by the P/Invoke shims to the sonames that exist on
/// the platforms we run on (Ubuntu CI: libsodium.so.23; Fedora image: libsodium.so.26; libdave is
/// built as liblibdave.so by its CMake project).
/// </summary>
internal static class NativeResolver
{
    private static readonly Dictionary<string, string[]> Candidates = new()
    {
        ["sodium"] = ["libsodium.so.26", "libsodium.so.23", "libsodium.so"],
        ["opus"] = ["libopus.so.0", "libopus.so"],
        ["dave"] = ["libdave.so", "liblibdave.so", "liblibdave.so.1"],
    };

    static NativeResolver()
    {
        // Accept the literal file names too, so a shim may say "libdave.so".
        Candidates["libdave.so"] = Candidates["dave"];
        Candidates["libsodium.so.23"] = Candidates["sodium"];
        Candidates["libopus.so.0"] = Candidates["opus"];
    }

#pragma warning disable CA2255 // module initializer is the documented way to register a resolver
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => NativeLibrary.SetDllImportResolver(typeof(NativeResolver).Assembly, Resolve);

    internal static IReadOnlyList<string> CandidatesFor(string name) =>
        Candidates.TryGetValue(name, out var names) ? names : [name];

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        foreach (var candidate in CandidatesFor(name))
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero; // fall back to the runtime's default probing
    }
}
