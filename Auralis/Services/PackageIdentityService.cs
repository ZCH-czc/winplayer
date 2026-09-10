using System.Runtime.InteropServices;
using System.Text;

namespace Auralis.Services;

/// <summary>
/// Detects whether the current desktop process was activated from an MSIX package. Packaged
/// installations let Windows own file associations, startup registration, upgrade and uninstall;
/// portable builds keep the existing per-user registry integration.
/// </summary>
internal static class PackageIdentityService
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private static readonly Lazy<bool> HasIdentity = new(DetectPackageIdentity);

    internal static bool IsPackaged => HasIdentity.Value;

    private static bool DetectPackageIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result switch
        {
            0 or ErrorInsufficientBuffer => true,
            AppModelErrorNoPackage => false,
            _ => false
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        StringBuilder? packageFullName);
}
