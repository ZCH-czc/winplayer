using System.IO;
using System.Net.Http;
using Auralis.MediaTransport;
using Auralis.Platform.Abstractions;

namespace Auralis.Services;

/// <summary>Platform lease adapter only. Transport and cache are owned by a separate component.
/// Windows own prepared pins until activation transfers ownership here. Only the decoder receives URIs.</summary>
internal sealed class OnlinePlaybackSourceService : IAsyncDisposable
{
    private readonly IMediaTransportSession _transport;
    private IMediaTransportResource? _active;
    internal OnlinePlaybackSourceService() : this(MediaTransportServices.CreateDefault()) { }
    internal OnlinePlaybackSourceService(HttpMessageHandler handler, string? cacheDirectory = null)
        : this(MediaTransportServices.CreateIsolated(handler,cacheDirectory ?? MediaTransportServices.CacheDirectory)) { }
    internal OnlinePlaybackSourceService(IMediaTransportSession transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    internal Task<IMediaTransportResource> PrepareAsync(PlatformStreamLease lease, CancellationToken token) => PrepareAsync(lease, null, token);
    internal async Task<IMediaTransportResource> PrepareAsync(PlatformStreamLease lease, string? trackCacheKey, CancellationToken cancellationToken, bool bufferRemote = false)
    {
        ArgumentNullException.ThrowIfNull(lease);
        try
        {
            return await _transport.PrepareAsync(new(lease.Url, lease.ExpiresAt, lease.MimeType, lease.Quality.Id,
                lease.RequestHeaders, lease.UseHostTransport), trackCacheKey, cancellationToken, bufferRemote).ConfigureAwait(false);
        }
        catch (MediaTransportException failure) { throw new OnlinePlaybackException(Message(failure.Failure)); }
    }
    internal async Task<IMediaTransportResource> PrepareVideoAsync(PlatformVideoLease video, CancellationToken token)
    {
        var candidates = new[] { video.Url }.Concat(video.AlternateUrls).Distinct().Take(3).ToArray();
        for (var i = 0; i < candidates.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await PrepareAsync(new PlatformStreamLease(candidates[i], video.ExpiresAt, video.MimeType,
                    new PlatformAudioQuality("video", "视频"), video.RequestHeaders) { UseHostTransport = video.UseHostTransport }, token).ConfigureAwait(false);
            }
            catch (OnlinePlaybackException) when (i + 1 < candidates.Length && !token.IsCancellationRequested)
            { /* Only provider-authorized alternatives; never guessed CDN addresses. */ }
        }
        throw new OnlinePlaybackException("视频流暂不可用。");
    }
    // Caller closes the previous decoder first. Swap ownership before yielding so a later operation
    // cannot release the replacement by accidentally observing the old active slot.
    internal Task ActivateAsync(IMediaTransportResource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var previous = Interlocked.Exchange(ref _active, source);
        return previous is null || ReferenceEquals(previous, source) ? Task.CompletedTask : previous.DisposeAsync().AsTask();
    }
    internal Task DiscardPreparedAsync(IMediaTransportResource source) => source.DisposeAsync().AsTask();
    internal Task ReleaseAsync() => Interlocked.Exchange(ref _active, null)?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
    private static string Message(MediaTransportFailure failure) => failure switch
    {
        MediaTransportFailure.Expired => "在线音频授权已过期，请重试播放。",
        MediaTransportFailure.InvalidUri => "在线音频流必须使用 HTTPS，或使用本机 loopback HTTP 地址。",
        MediaTransportFailure.Unavailable => "在线音频流暂时不可用。",
        MediaTransportFailure.Incomplete => "在线音频下载不完整，请重试播放。",
        MediaTransportFailure.IncompleteRange => "在线音频仅返回了不完整片段，请重试播放。",
        MediaTransportFailure.TooLarge => "在线音频文件超过安全缓冲上限。",
        MediaTransportFailure.InvalidRedirect => "在线音频流返回了无效的跳转地址。",
        MediaTransportFailure.InvalidHeaders => "在线音频所需的请求头不受播放器支持。",
        MediaTransportFailure.BudgetExceeded => "正在准备的媒体过多，请稍后重试。当前播放不会中断。",
        _ => "无法准备在线音频流，请检查网络后重试。"
    };
}
internal sealed class OnlinePlaybackException(string message) : Exception(message);
