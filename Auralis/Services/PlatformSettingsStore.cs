using System.IO;
using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;

namespace Auralis.Services;

/// <summary>
/// Persists the small, non-sensitive portion of the online-platform configuration. Provider
/// credentials and signed media URLs deliberately never enter this file.
/// </summary>
internal sealed class PlatformSettingsStore
{
    internal Func<string, CancellationToken, Task<IReadOnlyList<PlatformSettingManifest>>> ResolveDeclarationsAsync { get; set; } =
        (_, _) => Task.FromResult<IReadOnlyList<PlatformSettingManifest>>([]);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private Dictionary<string, string>? _values;

    internal PlatformSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Auralis",
            "platform-settings.json");
    }

    private async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _values!.TryGetValue(key, out var value) ? value : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The same declaration-based read path serves the UI and the provider. Legacy migration
    // never activates plugin code; no platform names or special setting keys live in this store.
    internal async ValueTask<string?> GetScopedAsync(string pluginId, string key, CancellationToken token)
    {
        ValidatePart(pluginId);
        ValidatePart(key);
        var declaration = (await ResolveDeclarationsAsync(pluginId, token).ConfigureAwait(false))
            .FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
        if (declaration is null) return null;
        var value = await GetAsync($"plugin:{pluginId}:{key}", token).ConfigureAwait(false);
        if (value is not null)
            return declaration.TryNormalize(value, out var normalized) ? normalized : declaration.DefaultValue;
        foreach (var legacyKey in declaration.LegacyKeys)
        {
            var legacy = await GetAsync(legacyKey, token).ConfigureAwait(false);
            if (legacy is not null && declaration.TryNormalize(legacy, out var migrated)) return migrated;
        }
        return declaration.DefaultValue;
    }

    internal async Task SetScopedAsync(string pluginId, string key, string value, CancellationToken token)
    {
        ValidatePart(pluginId);
        ValidatePart(key);
        var scoped = $"plugin:{pluginId}:{key}";
        ValidateKey(scoped);
        if (value.Length > 2048 || value.Any(char.IsControl)) throw new ArgumentException("Invalid non-secret setting.");
        await _gate.WaitAsync(token);
        try
        {
            await EnsureLoadedAsync(token);
            var values = _values!;
            var previous = values.GetValueOrDefault(scoped);
            if (previous is null && values.Count >= 128) throw new InvalidOperationException("Setting limit reached.");
            values[scoped] = value;
            try { await SaveAsync(token); }
            catch { if (previous is null) values.Remove(scoped); else values[scoped] = previous; throw; }
        }
        finally { _gate.Release(); }
    }

    internal IPlatformSettings ForPlugin(string pluginId) => new ScopedSettings(this, pluginId);
    private static void ValidatePart(string part)
    {
        if (string.IsNullOrWhiteSpace(part) || part.Length > 80 ||
            part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("Invalid plugin setting scope or key.");
    }
    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl)) throw new ArgumentException("Invalid setting key.");
    }
    private sealed class ScopedSettings(PlatformSettingsStore owner, string pluginId) : IPlatformSettings
    {
        public ValueTask<string?> GetAsync(string key, CancellationToken token) => owner.GetScopedAsync(pluginId, key, token);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_values is not null)
        {
            return;
        }

        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 2 * 1024 * 1024)
            {
                return;
            }

            var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                return;
            }

            foreach (var pair in loaded)
            {
                // Retain bounded non-secret legacy values without interpreting them. A value is
                // exposed only through a trusted declaration and its choice/endpoint validator.
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 256 && !pair.Key.Any(char.IsControl) &&
                    pair.Value is not null && pair.Value.Length <= 2048 && !pair.Value.Any(char.IsControl) && _values.Count < 128)
                    _values[pair.Key] = pair.Value;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged settings file is equivalent to an unconfigured online platform.
            _values.Clear();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("平台设置文件缺少父目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    _values,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // The completed settings write is unaffected by a stale temporary file.
            }
        }
    }
}
