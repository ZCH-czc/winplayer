using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Auralis.Services;

internal static class RuntimeBuildIdentity
{
    public static string Label { get; } = Create();
    private static string Create()
    {
        var assembly = typeof(RuntimeBuildIdentity).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "unknown";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(assembly.ManifestModule.ModuleVersionId.ToString()));
        // Web-only changes also distinguish two builds carrying the same product version.
        foreach (var name in new[] { "app.js", "styles.css", "media-hub.js", "media-hub.css", "window-surfaces.css", "index.html" })
        {
            try { hash.AppendData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "wwwroot", name))); }
            catch (IOException) { hash.AppendData(Encoding.UTF8.GetBytes(name)); }
        }
        return $"{version} · {Convert.ToHexString(hash.GetHashAndReset())[..10]}";
    }
}
