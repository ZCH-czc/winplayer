using System.IO;
using System.Text.Json;

namespace Auralis.Services;

public sealed record LanSharingSettings(
    int SchemaVersion = LanSharingSettingsStore.CurrentSchemaVersion,
    bool Enabled = false,
    int Port = LanSharingSettingsStore.DefaultPort);

public sealed class LanSharingSettingsStore
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultPort = 43821;
    public const int MinimumPort = 1024;
    public const int MaximumPort = 65535;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _settingsFile;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public LanSharingSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Auralis",
            "lan-sharing.json"))
    {
    }

    internal LanSharingSettingsStore(string settingsFile)
    {
        _settingsFile = settingsFile;
    }

    public async Task<LanSharingSettings> LoadAsync()
    {
        if (!File.Exists(_settingsFile))
        {
            return Normalize(null);
        }

        try
        {
            await using var stream = File.OpenRead(_settingsFile);
            return Normalize(await JsonSerializer.DeserializeAsync<LanSharingSettings>(stream, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Normalize(null);
        }
    }

    public async Task SaveAsync(LanSharingSettings settings)
    {
        settings = Normalize(settings);
        await _writeGate.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_settingsFile)!;
            Directory.CreateDirectory(directory);
            var temporaryFile = Path.Combine(directory, Path.GetFileName(_settingsFile) + ".tmp");
            try
            {
                await using (var stream = new FileStream(
                                 temporaryFile,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
                    await stream.FlushAsync();
                }

                File.Move(temporaryFile, _settingsFile, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryFile);
                }
                catch (IOException)
                {
                    // A failed cleanup must not replace the original settings error.
                }
                catch (UnauthorizedAccessException)
                {
                    // A failed cleanup must not replace the original settings error.
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public static LanSharingSettings Normalize(LanSharingSettings? settings)
    {
        var port = settings?.Port ?? DefaultPort;
        if (port is < MinimumPort or > MaximumPort)
        {
            port = DefaultPort;
        }

        return new LanSharingSettings(
            CurrentSchemaVersion,
            settings?.SchemaVersion == CurrentSchemaVersion && settings.Enabled,
            port);
    }
}
