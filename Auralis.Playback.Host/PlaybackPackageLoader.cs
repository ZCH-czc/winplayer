using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Auralis.Services;

namespace Auralis.Playback.Host;

/// <summary>A caller-owned exact grant made AFTER explicit trust. Not a signature or an install receipt.
/// Never construct from discovery alone. Persistent approval/import policy belongs to the installer.</summary>
public sealed record PlaybackPackageApproval(string ComponentId, string ManifestSha256);

public enum PlaybackPackageLoadIssue { ApprovalMismatch, IntegrityFailed, DependencyInvalid, FactoryInvalid, ActivationFailed }
public sealed class PlaybackPackageLoadException(PlaybackPackageLoadIssue issue) : InvalidOperationException("Playback component activation failed.")
{
    public PlaybackPackageLoadIssue Issue { get; } = issue;
}

/// <summary>Lazy activation of explicitly trusted packages. In-process, not a sandbox or a timeout
/// boundary. No automatic user discovery/approval. Package replacement requires stopping its sessions.</summary>
public static class PlaybackPackageLoader
{
    public static PlaybackComponentRegistration CreateRegistration(PlaybackPackageSnapshot snapshot,
        PlaybackPackageApproval approval, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.ComponentId != snapshot.Manifest.Descriptor.Id ||
            !string.Equals(approval.ManifestSha256, snapshot.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new PlaybackPackageLoadException(PlaybackPackageLoadIssue.ApprovalMismatch);
        return new(snapshot.Manifest.Descriptor, enabled, () => Activate(snapshot));
    }

    private static IPlaybackComponentFactory Activate(PlaybackPackageSnapshot snapshot)
    {
        var locks = new List<FileStream>();
        PackageContext? context = null;
        IPlaybackComponentFactory? factory = null;
        var issue = PlaybackPackageLoadIssue.IntegrityFailed;
        try
        {
            if (!OperatingSystem.IsWindows() || snapshot.Manifest.RuntimeIdentifier != "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant())
                throw new InvalidOperationException();
            // First reject bad paths before opening handles; then keep each approved file read-only
            // and reverify. On Windows this prevents ordinary writes/deletes while the factory lives.
            Verify(snapshot);
            foreach (var file in snapshot.Manifest.Files.Select(f => f.Path).Prepend(PlaybackComponentManifest.FileName))
                locks.Add(new FileStream(Path.Combine(snapshot.DirectoryPath, file), FileMode.Open, FileAccess.Read, FileShare.Read));
            Verify(snapshot);
            issue = PlaybackPackageLoadIssue.DependencyInvalid;
            context = new PackageContext(snapshot);
            var assembly = context.LoadFromAssemblyPath(Path.Combine(snapshot.DirectoryPath, snapshot.Manifest.EntryAssembly));
            issue = PlaybackPackageLoadIssue.FactoryInvalid;
            var type = assembly.GetType(snapshot.Manifest.EntryType, throwOnError: true)!;
            if (!type.IsPublic || type.IsAbstract || type.ContainsGenericParameters ||
                !typeof(IPlaybackComponentFactory).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException();
            issue = PlaybackPackageLoadIssue.ActivationFailed;
            factory = (IPlaybackComponentFactory)Activator.CreateInstance(type)!;
            var result = new PackageFactory(factory, context, locks);
            factory = null; context = null; locks = [];
            return result;
        }
        catch
        {
            try { factory?.Dispose(); } catch { }
            try { context?.Unload(); } catch { }
            foreach (var handle in locks) handle.Dispose();
            throw new PlaybackPackageLoadException(issue);
        }
    }

    private static void Verify(PlaybackPackageSnapshot snapshot)
    {
        // No captured UI context and no plugin code in the asynchronous hashing path.
        if (PlaybackComponentCatalog.VerifyPayloadAsync(snapshot).GetAwaiter().GetResult() != PlaybackPackageIssue.None)
            throw new InvalidOperationException();
    }

    private sealed class PackageFactory(IPlaybackComponentFactory inner, PackageContext context, List<FileStream> locks) : IPlaybackComponentFactory
    {
        private bool _disposed;
        public PlaybackComponentDescriptor Descriptor => inner.Descriptor;
        public void VerifyRuntime() { ObjectDisposedException.ThrowIf(_disposed, this); inner.VerifyRuntime(); }
        public IPlaybackSession Create(SynchronizationContext? eventContext)
        { ObjectDisposedException.ThrowIf(_disposed, this); return inner.Create(eventContext); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { inner.Dispose(); }
            finally
            {
                try { context.Unload(); }
                finally { foreach (var handle in locks) handle.Dispose(); }
            }
        }
    }

    private sealed class PackageContext : AssemblyLoadContext
    {
        private readonly Dictionary<string, (AssemblyName Identity, string Path)> _managed = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _native = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Assembly Contract = typeof(IPlaybackSession).Assembly;
        private static readonly string ContractName = Contract.GetName().Name!;

        public PackageContext(PlaybackPackageSnapshot snapshot) : base("playback-" + Guid.NewGuid().ToString("N"), isCollectible: true)
        {
            foreach (var file in snapshot.Manifest.Files.Where(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var path = Path.Combine(snapshot.DirectoryPath, file.Path);
                AssemblyName identity;
                try { identity = AssemblyName.GetAssemblyName(path); }
                catch (BadImageFormatException)
                {
                    var nativeName = Path.GetFileNameWithoutExtension(path);
                    // Native plugin trees may contain same-named non-imported modules; do not choose one.
                    if (!_native.TryAdd(nativeName, path)) _native[nativeName] = "";
                    continue;
                }
                if (identity.Name == ContractName || FrameworkName(identity.Name) || HostAssembly(identity.Name) ||
                    !_managed.TryAdd(identity.Name!, (identity, path))) throw new InvalidOperationException();
            }
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == ContractName)
            {
                var actual = Contract.GetName();
                if (name.Version > actual.Version || !SameToken(name, actual)) throw new FileLoadException();
                return Contract;
            }
            if (HostAssembly(name.Name)) throw new FileLoadException("Host implementation is not a playback dependency.");
            if (FrameworkName(name.Name)) return null; // framework only; never arbitrary host/private assemblies
            if (name.Name is not null && _managed.TryGetValue(name.Name, out var dependency) &&
                name.Version <= dependency.Identity.Version && SameToken(name, dependency.Identity))
                return LoadFromAssemblyPath(dependency.Path);
            throw new FileLoadException("Undeclared playback dependency.");
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            if (name != Path.GetFileName(name)) throw new DllNotFoundException();
            var key = Path.GetFileNameWithoutExtension(name);
            if (_native.TryGetValue(key, out var path) && path.Length > 0) return LoadUnmanagedDllFromPath(path);
            // Only explicit Windows OS imports may use the OS resolver. This is dependency hygiene,
            // not containment: trusted component code can call NativeLibrary.Load by itself.
            if (SystemImports.Contains(key)) return IntPtr.Zero;
            throw new DllNotFoundException("Undeclared playback native dependency.");
        }
        private static bool SameToken(AssemblyName a, AssemblyName b) =>
            (a.GetPublicKeyToken() ?? []).SequenceEqual(b.GetPublicKeyToken() ?? []);
        private static bool HostAssembly(string? name) => name is "Auralis" or "Auralis.Playback.Host" or
            "Auralis.Platform.Host" or "Auralis.Platform.Abstractions" or "Auralis.Platform";
        private static bool FrameworkName(string? name) => name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp" or
            "WindowsBase" or "PresentationCore" or "PresentationFramework" or "Accessibility" or
            "UIAutomationClient" or "UIAutomationTypes" or "UIAutomationProvider" ||
            name?.StartsWith("System.", StringComparison.Ordinal) == true || name?.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) == true;
        private static readonly HashSet<string> SystemImports = new(new[]
        {
            "kernel32", "kernelbase", "user32", "advapi32", "ole32", "oleaut32", "shell32", "gdi32", "gdiplus",
            "comdlg32", "comctl32", "winmm", "ws2_32", "bcrypt", "ntdll", "shlwapi", "dwmapi"
        }, StringComparer.OrdinalIgnoreCase);
    }
}
