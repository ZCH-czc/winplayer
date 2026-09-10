using System.IO;
using System.Text.Json;
using System.Windows;
using Auralis.MediaTransport;
using Auralis.Platform.Abstractions;
using Auralis.Services;
using Microsoft.Web.WebView2.Core;

namespace Auralis;

public partial class MainWindow
{
    private readonly OnlinePlaybackSourceService _embeddedVideoSource = new();
    private readonly OnlinePlaybackSourceService _embeddedAudioSource = new();
    private CancellationTokenSource? _embeddedVideoCancellation;
    private CancellationTokenSource? _embeddedOpenTimeout;
    private Uri? _currentAudioSource;
    private bool _embeddedVideoActive;
    private bool _embeddedVideoOpened;
    private double _embeddedResumePosition;
    private long _embeddedVideoRequestId;

    private void StopEmbeddedVideo(bool restoreAudio)
    {
        CancelAndDispose(ref _embeddedVideoCancellation);
        CancelAndDispose(ref _embeddedOpenTimeout);
        var wasActive = _embeddedVideoActive;
        _embeddedVideoActive = false;
        if (!wasActive) return;
        var position = _embeddedVideoOpened ? _mediaPlayer.Position.TotalSeconds : _embeddedResumePosition;
        var playing = _mediaPlayer.WantsPlayback;
        _mediaPlayer.Close();
        _ = _embeddedVideoSource.ReleaseAsync();
        _ = _embeddedAudioSource.ReleaseAsync();
        if (restoreAudio && _currentAudioSource is not null) _mediaPlayer.Open(_currentAudioSource, playing, position);
    }

    private async Task SetEmbeddedVideoAsync(JsonElement root)
    {
        var handle = JsonText(root, "handle");
        if (!root.TryGetProperty("requestId", out var request) || !request.TryGetInt64(out var requestId) ||
            !root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        if (!enabled.GetBoolean())
        {
            if (handle != _currentTrackId) return;
            _embeddedVideoRequestId = requestId;
            StopEmbeddedVideo(true);
            await SendEmbeddedVideoStateAsync(handle, requestId, false, null);
            return;
        }
        if (handle != _currentTrackId || !_currentPlaybackIsOnline || _currentAudioSource is null ||
            !OnlinePlatforms.TryGetTrack(handle!, out var track) || track?.HasMusicVideo != true)
        {
            await SendEmbeddedVideoStateAsync(handle, requestId, false, "歌曲入口已失效，请重新选择歌曲。");
            return;
        }
        StopEmbeddedVideo(true);
        _embeddedVideoRequestId = requestId;
        var cancellation = ReplaceCancellation(ref _embeddedVideoCancellation);
        var token = cancellation.Token;
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        Task<IMediaTransportResource>? sourceTask = null;
        Task<IMediaTransportResource?>? audioTask = null;
        var resourcesActivated = false;
        try
        {
            var result = await OnlinePlatforms.AcquireVideoAsync(handle!, token);
            token.ThrowIfCancellationRequested();
            if (!result.IsSuccess)
            {
                await SendEmbeddedVideoStateAsync(handle, requestId, false, result.Error?.Message ?? "此视频暂不可用。");
                return;
            }
            var video = result.Value;
            sourceTask = PrepareEmbeddedVideoSourceAsync(video, token);
            audioTask = video.AudioStream is null ? Task.FromResult<IMediaTransportResource?>(null) : PrepareVideoAudioAsync(video.AudioStream, token);
            await Task.WhenAll((Task)sourceTask, audioTask);
            var source = await sourceTask;
            var audio = await audioTask;
            token.ThrowIfCancellationRequested();
            if (handle != _currentTrackId || !ReferenceEquals(_embeddedVideoCancellation, cancellation)) return;
            var position = _mediaPlayer.Position.TotalSeconds;
            var playing = _mediaPlayer.WantsPlayback;
            _embeddedVideoActive = true;
            _embeddedVideoOpened = false;
            _embeddedResumePosition = position;
            _mediaPlayer.Close();
            var activateVideo = _embeddedVideoSource.ActivateAsync(source);
            var activateAudio = audio is null ? _embeddedAudioSource.ReleaseAsync() : _embeddedAudioSource.ActivateAsync(audio);
            resourcesActivated = true;
            await Task.WhenAll(activateVideo, activateAudio);
            token.ThrowIfCancellationRequested();
            if (handle != _currentTrackId || !ReferenceEquals(_embeddedVideoCancellation, cancellation)) return;
            _mediaPlayer.Open(source.Source, playing, position, video: true, audioSlave: audio?.Source);
            if (_embeddedVideoActive) _ = WatchEmbeddedOpenAsync(handle!, requestId);
            // MediaOpened acknowledges readiness. Until then keep the Web loading surface;
            // do not expose a black child HWND or prematurely hide the loading affordance.
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_embeddedVideoCancellation, cancellation))
            {
                StopEmbeddedVideo(true);
                await SendEmbeddedVideoStateAsync(handle, requestId, false, "视频加载超时，音频继续播放。");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OnlinePlaybackException)
        {
            if (!ReferenceEquals(_embeddedVideoCancellation, cancellation)) return;
            StopEmbeddedVideo(true);
            await SendEmbeddedVideoStateAsync(handle, requestId, false, exception is OnlinePlaybackException
                ? exception.Message.Replace("在线音频", "视频") + " 音频继续播放，可重试。"
                : "暂时无法加载视频，已保留音频播放。");
        }
        finally
        {
            // When one branch fails/cancels, WhenAll has still drained both. Release the successful
            // sibling too; a late preparation must never become a pin retained until application exit.
            if (!resourcesActivated)
            {
                if (sourceTask?.IsCompletedSuccessfully == true) await sourceTask.Result.DisposeAsync();
                if (audioTask?.IsCompletedSuccessfully == true && audioTask.Result is { } preparedAudio) await preparedAudio.DisposeAsync();
            }
            ClearCancellation(ref _embeddedVideoCancellation, cancellation);
        }
    }

    private async Task<IMediaTransportResource?> PrepareVideoAudioAsync(PlatformStreamLease lease, CancellationToken token) =>
        await _embeddedAudioSource.PrepareAsync(lease, token);

    private Task<IMediaTransportResource> PrepareEmbeddedVideoSourceAsync(PlatformVideoLease video, CancellationToken token) =>
        _embeddedVideoSource.PrepareVideoAsync(video, token);

    private async Task WatchEmbeddedOpenAsync(string handle, long requestId)
    {
        var cancellation = ReplaceCancellation(ref _embeddedOpenTimeout);
        try
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                await Task.Delay(50, cancellation.Token);
                if (_windowClosed || !_embeddedVideoActive || _currentTrackId != handle || _embeddedVideoRequestId != requestId) return;
                if (_mediaPlayer.ReadVideoFrame() is null) continue;
                _embeddedVideoOpened = true;
                await SendEmbeddedVideoStateAsync(handle, requestId, true, null);
                return;
            }
            StopEmbeddedVideo(true);
            await SendEmbeddedVideoStateAsync(handle, requestId, false, "视频缓冲超时，已返回音频。");
        }
        catch (OperationCanceledException) { }
        finally { ClearCancellation(ref _embeddedOpenTimeout, cancellation); }
    }

    private void SetEmbeddedVideoBounds(JsonElement root)
    {
        // Retained for old cached UI compatibility. Video now participates in Web composition.
    }

    private void ServeVideoFrame(CoreWebView2WebResourceRequestedEventArgs e, Uri uri)
    {
        var expected = $"/{Uri.EscapeDataString(_currentTrackId ?? string.Empty)}/{_embeddedVideoRequestId}";
        var bytes = _embeddedVideoActive && uri.AbsolutePath == expected && e.Request.Method == "GET"
            ? _mediaPlayer.ReadVideoFrame() : null;
        e.Response = PlayerWebView.CoreWebView2.Environment.CreateWebResourceResponse(
            bytes is null ? null : new MemoryStream(bytes, writable: false), bytes is null ? 404 : 200,
            bytes is null ? "Not Found" : "OK",
            "Content-Type: image/jpeg\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: http://app.auralis.local");
    }

    private Task SendEmbeddedVideoStateAsync(string? handle, long requestId, bool enabled, string? message) =>
        ExecuteScriptAsync($"window.Auralis?.setEmbeddedVideoState({JsonSerializer.Serialize(new { handle, requestId, enabled, error = message is null ? null : new { message } }, WebJsonOptions)})");
}
