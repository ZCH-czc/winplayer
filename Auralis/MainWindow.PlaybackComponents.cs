using System.IO;
using System.Text.Json;
using Auralis.Playback.Host;
using Auralis.Services;
using Microsoft.Web.WebView2.Core;

namespace Auralis;

public partial class MainWindow
{
    private bool _playbackComponentBusy;
    private PlaybackArchivePreview? _playbackComponentPreview;
    private string? _playbackComponentPreviewToken;
    private readonly CancellationTokenSource _playbackComponentLifetime = new();

    private async Task HandlePlaybackComponentsAsync(JsonElement root, CoreWebView2WebMessageReceivedEventArgs message)
    {
        if (!Uri.TryCreate(message.Source, UriKind.Absolute, out var source) || source.Scheme != "http" ||
            source.Host != "app.auralis.local" || !source.IsDefaultPort || source.UserInfo.Length != 0 ||
            !root.TryGetProperty("requestId", out var request) || !request.TryGetInt64(out var requestId) ||
            requestId is < 1 or > 9007199254740991L || !root.TryGetProperty("operation", out var op) || op.ValueKind != JsonValueKind.String)
            return;
        var operation = op.GetString();
        if (operation is not ("list" or "pick" or "confirm" or "cancel" or "enable" or "select")) return;
        object? inventory = null; string? error = null; var cancelled = false;
        if (_playbackComponentBusy || (operation is not ("list" or "cancel") && (_transportComponentBusy || _transportComponentPreview is not null || _pluginManagementBusy || _platformPluginPreviewOpen))) error = "busy";
        else
        {
            _playbackComponentBusy = true;
            try
            {
                var store = PlaybackServices.Installations;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_playbackComponentLifetime.Token);
                if (operation == "pick")
                {
                    if (_playbackComponentPreview is not null) throw new InvalidOperationException();
                    var picker = new Microsoft.Win32.OpenFileDialog { Title = CurrentLanguageState.ResolvedLanguage == "en-US" ? "Import playback component" : "导入播放组件",
                        Filter = "Auralis (*.auralis-playback.zip;*.zip)|*.auralis-playback.zip;*.zip", CheckFileExists = true, Multiselect = false };
                    if (picker.ShowDialog(this) != true) cancelled = true;
                    else
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(45));
                        var prepared = await Task.Run(() => store.PreviewArchiveAsync(picker.FileName, timeout.Token));
                        if (_windowClosed || timeout.IsCancellationRequested)
                        {
                            await prepared.DisposeAsync();
                            throw new OperationCanceledException();
                        }
                        _playbackComponentPreview = prepared;
                        _playbackComponentPreviewToken = Guid.NewGuid().ToString("N");
                    }
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                }
                else
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    if (operation == "confirm")
                    {
                        if (_playbackComponentPreview is not { } preview || !root.TryGetProperty("token", out var token) ||
                            token.ValueKind != JsonValueKind.String || token.GetString() != _playbackComponentPreviewToken ||
                            !root.TryGetProperty("trust", out var trust) || trust.ValueKind != JsonValueKind.True) throw new InvalidOperationException();
                        // Only native-held preview identity determines the grant; JSON cannot alter it.
                        await Task.Run(() => store.ImportApprovedAsync(preview, new(preview.Descriptor.Id, preview.ManifestSha256), timeout.Token));
                        await ClearPlaybackComponentPreviewAsync();
                    }
                    else if (operation == "cancel") await ClearPlaybackComponentPreviewAsync();
                    else if (operation is "enable" or "select")
                    {
                        if (!root.TryGetProperty("id", out var id) || id.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidOperationException();
                        var componentId = id.GetString();
                        if (componentId is { Length: 0 or > 100 }) throw new InvalidOperationException();
                        if (operation == "select") await Task.Run(() => store.SelectAsync(componentId, timeout.Token));
                        else
                        {
                            if (componentId is null || !root.TryGetProperty("enabled", out var enabled) ||
                                enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidOperationException();
                            await Task.Run(() => store.SetEnabledAsync(componentId, enabled.GetBoolean(), timeout.Token));
                        }
                    }
                }
                inventory = await Task.Run(() => ReadPlaybackComponentInventoryAsync(timeout.Token));
            }
            catch (OperationCanceledException) { error = "playbackManagementFailed"; }
            catch { error = "playbackManagementFailed"; }
            finally { _playbackComponentBusy = false; }
        }
        var pending = _playbackComponentPreview;
        var previewView = pending is null ? null : new { token = _playbackComponentPreviewToken, id = pending.Descriptor.Id,
            displayName = pending.Descriptor.DisplayName, version = pending.Descriptor.ComponentVersion.ToString(),
            pending.ManifestSha256, pending.ArchiveSha256, pending.FileCount, pending.PayloadBytes, capabilities = pending.Descriptor.Capabilities.ToString() };
        var payload = JsonSerializer.Serialize(new { requestId, operation, inventory, preview = previewView, cancelled, error }, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setPlaybackComponents({payload})");
    }

    private static async Task<object> ReadPlaybackComponentInventoryAsync(CancellationToken token)
    {
        var state = PlaybackServices.Installations.ReadState();
        var items = new List<object>();
        foreach (var row in state.Components)
        {
            token.ThrowIfCancellationRequested();
            var findings = PlaybackComponentCatalog.Discover(Path.Combine(PlaybackServices.StorageRoot, "revisions", row.ManifestSha256),
                PlaybackCapabilities.CompletePlayer, cancellationToken: token);
            var finding = findings.Count == 1 ? findings[0] : null;
            var package = finding?.Package;
            var matches = package?.Manifest.Descriptor.Id == row.ComponentId && package.ManifestSha256 == row.ManifestSha256;
            var issue = matches ? finding!.Issue : PlaybackPackageIssue.ManifestChanged;
            if (issue == PlaybackPackageIssue.None) issue = await PlaybackComponentCatalog.VerifyPayloadAsync(package!, token);
            items.Add(new { id = row.ComponentId, displayName = matches ? package!.Manifest.Descriptor.DisplayName : row.ComponentId,
                version = matches ? package!.Manifest.Descriptor.ComponentVersion.ToString() : "", row.Enabled,
                available = issue == PlaybackPackageIssue.None, selected = state.SelectedId == row.ComponentId, issue = issue.ToString() });
        }
        var initial = PlaybackServices.InitialState;
        var restartRequired = state.Issue == PlaybackInstallationIssue.None && (state.SelectedId != initial.SelectedId ||
            !state.Components.OrderBy(c => c.ComponentId).SequenceEqual(initial.Components.OrderBy(c => c.ComponentId)));
        var composition = PlaybackServices.Composition;
        return new { items, state.SelectedId, restartRequired, stateError = state.Issue == PlaybackInstallationIssue.None ? null : state.Issue.ToString(),
            current = new { displayName = composition.ActiveDescriptor.DisplayName, version = composition.ActiveDescriptor.ComponentVersion.ToString(),
                bundled = composition.UsingBundled, fellBack = composition.FellBack } };
    }

    private async Task ClearPlaybackComponentPreviewAsync()
    {
        var pending = _playbackComponentPreview;
        _playbackComponentPreview = null; _playbackComponentPreviewToken = null;
        if (pending is not null) await pending.DisposeAsync();
    }
    private async Task ClosePlaybackComponentsAsync()
    {
        _playbackComponentLifetime.Cancel();
        try { await ClearPlaybackComponentPreviewAsync(); } catch { /* Never block window teardown on owned preview cleanup. */ }
    }
}
