using LibVLCSharp.Shared;

namespace Auralis.Services;

/// <summary>
/// Keeps bundled LibVLC players on the same Windows output path. Audio-only playback and
/// MV playback must use this implementation rather than maintaining subtly different defaults.
/// </summary>
internal static class LibVlcAudioOutputConfiguration
{
    // MediaPlayer eagerly opens an audio output, before persisted Web settings arrive. The
    // bundled mmdevice module can access-violate during that construction on Windows. Selecting
    // an output later (or catching managed exceptions) cannot protect startup. Use the same
    // compatible default for construction, playback and MV; explicit device choices remain valid.
    internal const string StartupOutputOption = "--aout=directsound";

    internal static AudioOutputSettings Normalize(LibVLC libVlc, AudioOutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(libVlc);
        ArgumentNullException.ThrowIfNull(settings);

        var availableModules = GetModules(libVlc);
        var requestedModule = string.IsNullOrWhiteSpace(settings.OutputModule)
            ? "auto"
            : settings.OutputModule.Trim();
        var outputModule = requestedModule.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                           availableModules.Any(module => module.Id.Equals(requestedModule, StringComparison.Ordinal))
            ? requestedModule
            : "auto";
        var channel = settings.Channel switch
        {
            "reverseStereo" or "left" or "right" or "dolby" => settings.Channel,
            _ => "stereo"
        };

        return new AudioOutputSettings(
            outputModule,
            settings.OutputDeviceId?.Trim() ?? string.Empty,
            channel,
            Math.Clamp(settings.BufferMilliseconds, 100, 5000));
    }

    internal static AudioOutputModuleInfo[] GetModules(LibVLC libVlc)
    {
        ArgumentNullException.ThrowIfNull(libVlc);
        try
        {
            return (libVlc.AudioOutputs ?? [])
                .Where(output => !string.IsNullOrWhiteSpace(output.Name))
                .Select(output => new AudioOutputModuleInfo(
                    output.Name,
                    string.IsNullOrWhiteSpace(output.Description) ? output.Name : output.Description))
                .DistinctBy(output => output.Id, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            // Some incomplete third-party VLC runtimes fail enumeration. The player can still
            // fall back to LibVLC's own automatic Windows output choice.
            return [];
        }
    }

    internal static AudioOutputSettings ApplySelection(
        LibVLC libVlc,
        MediaPlayer player,
        AudioOutputSettings settings)
    {
        var normalized = Normalize(libVlc, settings);
        var module = ResolveModule(libVlc, normalized);
        if (!string.IsNullOrWhiteSpace(module))
        {
            _ = player.SetAudioOutput(module);
        }

        ApplyDeviceAndChannel(libVlc, player, normalized);
        return normalized;
    }

    internal static void ApplyDeviceAndChannel(
        LibVLC libVlc,
        MediaPlayer player,
        AudioOutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(player);
        var normalized = Normalize(libVlc, settings);
        var module = ResolveModule(libVlc, normalized);
        if (!string.IsNullOrWhiteSpace(module))
        {
            if (!string.IsNullOrWhiteSpace(normalized.OutputDeviceId))
            {
                player.SetOutputDevice(normalized.OutputDeviceId, module);
            }
            else if (module.Equals("mmdevice", StringComparison.OrdinalIgnoreCase))
            {
                // An empty identifier resets mmdevice to the current Windows eConsole endpoint.
                // Passing null through LibVLC is unsupported and can terminate the host.
                player.SetOutputDevice(string.Empty, module);
            }
        }

        var channel = normalized.Channel switch
        {
            "reverseStereo" => AudioOutputChannel.RStereo,
            "left" => AudioOutputChannel.Left,
            "right" => AudioOutputChannel.Right,
            "dolby" => AudioOutputChannel.Dolbys,
            _ => AudioOutputChannel.Stereo
        };
        _ = player.SetChannel(channel);
    }

    internal static string? ResolveModule(LibVLC libVlc, AudioOutputSettings settings)
    {
        var normalized = Normalize(libVlc, settings);
        if (!normalized.OutputModule.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.OutputModule;
        }

        // Keep automatic playback on the same safe output selected before player construction.
        // DirectSound follows the Windows default device and still uses the shared Windows mixer.
        var modules = GetModules(libVlc);
        return modules.FirstOrDefault(module => module.Id.Equals("directsound", StringComparison.OrdinalIgnoreCase))?.Id
            ?? modules.FirstOrDefault(module =>
                module.Id.Equals("waveout", StringComparison.OrdinalIgnoreCase))?.Id;
    }
}
