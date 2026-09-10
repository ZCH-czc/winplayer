namespace Auralis.MediaTransport.Host;

/// <summary>One startup selection, shared by independently owned transport sessions. Does not
/// reread preferences or activate unselected components. No request-time replay or hot replacement.</summary>
public sealed class MediaTransportComponentComposition
{
    private readonly SemaphoreSlim _creation = new(1, 1);
    private readonly MediaTransportRegistry _registry;
    private readonly MediaTransportDescriptor _bundled;
    private readonly MediaTransportDescriptor? _optional;
    private readonly string? _preferred;
    private readonly MediaTransportCapabilities _required;
    private int _fellBack;
    private int _activated;
    public bool UsingBundled => _preferred is null || FellBack;
    public bool FellBack => Volatile.Read(ref _fellBack) != 0;
    public bool Activated => Volatile.Read(ref _activated) != 0;
    // Before first use this describes the selected startup path, not a loaded factory.
    public MediaTransportDescriptor CurrentDescriptor => UsingBundled ? _bundled : _optional!;

    public MediaTransportComponentComposition(MediaTransportStartupPlan plan, MediaTransportRegistration bundled,
        MediaTransportCapabilities required)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(bundled);
        if (required == MediaTransportCapabilities.None || (required & ~MediaTransportCapabilities.Full) != 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        var selected = plan.Issue == MediaTransportInstallationIssue.None && plan.SelectedId is not null
            ? plan.Registrations.SingleOrDefault(r => r.Descriptor.Id == plan.SelectedId) : null;
        _bundled = bundled.Descriptor; _optional = selected?.Descriptor; _preferred = selected?.Descriptor.Id;
        _required = required;
        _registry = new(selected is null ? [] : [selected], bundled);
    }

    public IMediaTransportSession CreateDeferred(MediaTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return MediaTransportRegistry.Defer(() => CreateAsync(context));
    }
    private async Task<MediaTransportSelection> CreateAsync(MediaTransportContext context)
    {
        // A managed creation failure latches before another session can attempt the optional factory.
        await _creation.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await _registry.CreateAsync(FellBack ? null : _preferred, context, _required).ConfigureAwait(false);
            if (result.UsedFallback) Volatile.Write(ref _fellBack, 1);
            Volatile.Write(ref _activated, 1);
            return result;
        }
        finally { _creation.Release(); }
    }
}
