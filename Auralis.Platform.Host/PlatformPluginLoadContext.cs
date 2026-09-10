using System.Reflection;
using System.Runtime.Loader;
using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

internal sealed class PlatformPluginLoadContext : AssemblyLoadContext
{
    private static readonly Assembly SharedContractAssembly = typeof(IAuralisPlatformPlugin).Assembly;
    private static readonly string SharedContractName = SharedContractAssembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;

    internal PlatformPluginLoadContext(string entryAssemblyPath, string pluginId)
        : base($"Auralis.Platform:{pluginId}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, SharedContractName, StringComparison.OrdinalIgnoreCase))
        {
            // Every plugin must see the host's already-loaded contract assembly. Loading a private copy
            // would make IAuralisPlatformPlugin fail type-identity checks even at the same API version.
            return SharedContractAssembly;
        }

        var dependencyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return dependencyPath is null ? null : LoadFromAssemblyPath(dependencyPath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var dependencyPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return dependencyPath is null ? nint.Zero : LoadUnmanagedDllFromPath(dependencyPath);
    }
}
