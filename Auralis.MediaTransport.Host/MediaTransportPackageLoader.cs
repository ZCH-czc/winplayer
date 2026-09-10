using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Auralis.MediaTransport.Host;

/// <summary>Caller-owned exact grant made after explicit trust, never from discovery alone.
/// Not a signature or persistent installation receipt.</summary>
public sealed record MediaTransportPackageApproval(string ComponentId, string ManifestSha256);
public enum MediaTransportLoadIssue { ApprovalMismatch, IntegrityFailed, DependencyInvalid, FactoryInvalid, ActivationFailed }
public sealed class MediaTransportLoadException(MediaTransportLoadIssue issue)
    : InvalidOperationException("Transport component activation failed.")
{ public MediaTransportLoadIssue Issue { get; } = issue; }

/// <summary>Trusted in-process activation. No installation, automatic approval, network, user
/// discovery or sandbox. Existing sessions must close before component replacement.</summary>
public static class MediaTransportPackageLoader
{
    public static MediaTransportRegistration CreateRegistration(MediaTransportPackageSnapshot snapshot,
        MediaTransportPackageApproval approval, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(approval);
        if (approval.ComponentId != snapshot.Manifest.Descriptor.Id ||
            !string.Equals(approval.ManifestSha256, snapshot.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new MediaTransportLoadException(MediaTransportLoadIssue.ApprovalMismatch);
        return new(snapshot.Manifest.Descriptor, enabled, () => Activate(snapshot));
    }

    private static IMediaTransportFactory Activate(MediaTransportPackageSnapshot snapshot)
    {
        var leases = new List<FileStream>();
        PackageContext? context = null;
        var issue = MediaTransportLoadIssue.IntegrityFailed;
        try
        {
            if (!OperatingSystem.IsWindows() || snapshot.Manifest.RuntimeIdentifier !=
                "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()) throw new InvalidOperationException();
            Verify(snapshot);
            foreach (var file in snapshot.Manifest.Files.Select(f => f.Path).Prepend(MediaTransportComponentManifest.FileName))
                leases.Add(new FileStream(Path.Combine(snapshot.DirectoryPath, file), FileMode.Open, FileAccess.Read, FileShare.Read));
            Verify(snapshot); // Keep Windows read leases through factory/session lifetime.
            issue = MediaTransportLoadIssue.DependencyInvalid;
            context = new PackageContext(snapshot);
            var assembly = context.LoadFromAssemblyPath(Path.Combine(snapshot.DirectoryPath, snapshot.Manifest.EntryAssembly));
            issue = MediaTransportLoadIssue.FactoryInvalid;
            var type = assembly.GetType(snapshot.Manifest.EntryType, throwOnError: true)!;
            if (!type.IsPublic || type.IsAbstract || type.ContainsGenericParameters ||
                !typeof(IMediaTransportFactory).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException();
            issue = MediaTransportLoadIssue.ActivationFailed;
            var factory = (IMediaTransportFactory)Activator.CreateInstance(type)!;
            return new PackageFactory(factory, context, leases);
        }
        catch
        {
            try { context?.Unload(); } catch { }
            foreach (var lease in leases) lease.Dispose();
            throw new MediaTransportLoadException(issue);
        }
    }
    private static void Verify(MediaTransportPackageSnapshot snapshot)
    {
        if (snapshot.Manifest.Descriptor.ApiVersion != MediaTransportRegistry.ApiVersion ||
            snapshot.Manifest.Descriptor.MinimumHostVersion > MediaTransportRegistry.HostVersion ||
            MediaTransportComponentCatalog.VerifyPayloadAsync(snapshot).GetAwaiter().GetResult() != MediaTransportPackageIssue.None)
            throw new InvalidOperationException();
    }

    private sealed class PackageFactory(IMediaTransportFactory inner, PackageContext context, List<FileStream> leases) : IMediaTransportFactory
    {
        private readonly object _gate = new();
        private Task? _close;
        public MediaTransportDescriptor Descriptor => inner.Descriptor;
        public IMediaTransportSession Create(MediaTransportContext configuration)
        { lock (_gate) { ObjectDisposedException.ThrowIf(_close is not null, this); return inner.Create(configuration); } }
        public ValueTask DisposeAsync()
        { lock (_gate) return new(_close ??= Task.Run(CloseAsync)); }
        private async Task CloseAsync()
        {
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { context.Unload(); }
                finally { foreach (var lease in leases) lease.Dispose(); }
            }
        }
    }

    private sealed class PackageContext : AssemblyLoadContext
    {
        private readonly Dictionary<string, (AssemblyName Identity, string Path)> _managed = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _native = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Assembly Contract = typeof(IMediaTransportSession).Assembly;
        private static readonly string ContractName = Contract.GetName().Name!;
        // Only assemblies from the actual core framework directory may fall through to Default.
        // A name beginning System. alone is not sufficient to access an application's dependency.
        private static readonly HashSet<string> FrameworkNames = new(((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => string.Equals(Path.GetDirectoryName(p), Path.GetDirectoryName(typeof(object).Assembly.Location), StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
        internal PackageContext(MediaTransportPackageSnapshot snapshot) : base("transport-" + Guid.NewGuid().ToString("N"), isCollectible: true)
        {
            foreach (var file in snapshot.Manifest.Files.Where(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var path = Path.Combine(snapshot.DirectoryPath, file.Path);
                AssemblyName identity;
                try { identity = AssemblyName.GetAssemblyName(path); }
                catch (BadImageFormatException)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (!_native.TryAdd(name, path)) throw new InvalidOperationException();
                    continue;
                }
                if (string.Equals(identity.Name, ContractName, StringComparison.OrdinalIgnoreCase) || HostAssembly(identity.Name) || ReservedFrameworkName(identity.Name) ||
                    !_managed.TryAdd(identity.Name!, (identity, path))) throw new InvalidOperationException();
            }
        }
        protected override Assembly? Load(AssemblyName name)
        {
            if (string.Equals(name.Name, ContractName, StringComparison.OrdinalIgnoreCase))
            {
                var actual = Contract.GetName();
                if (name.Version > actual.Version || !SameToken(name, actual)) throw new FileLoadException();
                return Contract;
            }
            if (HostAssembly(name.Name)) throw new FileLoadException("Host implementations are not transport dependencies.");
            if (name.Name is not null && FrameworkNames.Contains(name.Name)) return null;
            if (name.Name is not null && _managed.TryGetValue(name.Name, out var dependency) &&
                name.Version <= dependency.Identity.Version && SameToken(name, dependency.Identity))
                return LoadFromAssemblyPath(dependency.Path);
            throw new FileLoadException("Undeclared transport dependency.");
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            if (name != Path.GetFileName(name)) throw new DllNotFoundException();
            var key = Path.GetFileNameWithoutExtension(name);
            if (_native.TryGetValue(key, out var path)) return LoadUnmanagedDllFromPath(path);
            if (SystemImports.Contains(key)) return IntPtr.Zero;
            throw new DllNotFoundException("Undeclared transport native dependency.");
        }
        private static bool SameToken(AssemblyName a, AssemblyName b) =>
            (a.GetPublicKeyToken() ?? []).SequenceEqual(b.GetPublicKeyToken() ?? []);
        private static bool HostAssembly(string? name) => new[] { "Auralis", "Auralis.MediaTransport.Host", "Auralis.Platform" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            name?.StartsWith("Auralis.Platform.", StringComparison.OrdinalIgnoreCase) == true || name?.StartsWith("Auralis.Playback.", StringComparison.OrdinalIgnoreCase) == true;
        private static bool ReservedFrameworkName(string? name) => new[] { "System", "mscorlib", "netstandard", "Microsoft.CSharp" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            name?.StartsWith("System.", StringComparison.OrdinalIgnoreCase) == true || name?.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase) == true;
        private static readonly HashSet<string> SystemImports = new(["kernel32", "kernelbase", "ntdll", "bcrypt", "crypt32", "secur32", "ws2_32", "advapi32"], StringComparer.OrdinalIgnoreCase);
    }
}
