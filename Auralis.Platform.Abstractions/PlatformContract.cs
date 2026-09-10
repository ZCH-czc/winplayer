namespace Auralis.Platform.Abstractions;

/// <summary>
/// Defines the binary contract shared by Auralis and independently deployed platform plugins.
/// </summary>
public static class PlatformContract
{
    /// <summary>
    /// Gets the current host API version. Version <c>1</c> is intentionally stable; additive
    /// changes should not change it, while breaking contract changes require a new value.
    /// </summary>
    public const int HostApiVersion = 1;

    /// <summary>
    /// Determines whether a plugin API range can run in this host.
    /// </summary>
    /// <param name="minimumHostApiVersion">The oldest host API supported by the plugin.</param>
    /// <param name="maximumHostApiVersion">The newest host API supported by the plugin.</param>
    /// <returns><see langword="true"/> when the current host API falls inside the range.</returns>
    public static bool IsCompatible(int minimumHostApiVersion, int maximumHostApiVersion) =>
        minimumHostApiVersion > 0 &&
        maximumHostApiVersion >= minimumHostApiVersion &&
        HostApiVersion >= minimumHostApiVersion &&
        HostApiVersion <= maximumHostApiVersion;
}

internal static class PlatformIdRules
{
    internal static string EnsureScopedId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length > 64 || !char.IsAsciiLetterOrDigit(value[0]))
        {
            throw new ArgumentException(
                "An identifier must start with an ASCII letter or digit and contain at most 64 characters.",
                parameterName);
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_')
            {
                throw new ArgumentException(
                    "An identifier may contain only ASCII letters, digits, '.', '-' and '_'.",
                    parameterName);
            }
        }

        return value;
    }
}
