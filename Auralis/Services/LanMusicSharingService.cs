using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Auralis.Services;

public sealed record LanSharedTrack(
    string SourceId,
    string SourcePath,
    string Title,
    string Artist,
    string Album,
    string Extension,
    long Size,
    double? DurationSeconds,
    DateTime DateAdded,
    string? ArtworkPath);

public sealed record LanMusicSharingState(
    bool Enabled,
    bool IsRunning,
    int Port,
    IReadOnlyList<string> BaseUrls,
    IReadOnlyList<string> PairingUrls,
    DateTimeOffset? PairingExpiresAt,
    int TrackCount,
    int ActiveSessionCount,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// Hosts the opt-in, read-only LAN browser player. The public catalog is an atomic snapshot and
/// the HTTP surface resolves opaque per-process IDs back to trusted local paths; request input is
/// never interpreted as a path. Online-provider handles, credentials and leases are outside this
/// service by design.
/// </summary>
public sealed class LanMusicSharingService : IAsyncDisposable
{
    private const string SessionCookieName = "AuralisLanSession";
    private const long MaximumCoverBytes = 16L * 1024 * 1024;
    private static readonly HashSet<string> SharedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(2);

    private readonly IAppLogger _logger;
    private readonly string _webRoot;
    private readonly Func<IReadOnlyList<IPAddress>> _addressProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _streamGate = new(6, 6);
    private readonly object _catalogLock = new();
    private readonly LanAccessSessionManager _sessions = new();
    private readonly System.Threading.Timer _networkChangeTimer;
    private Dictionary<string, LanSharedTrack> _sourceTracks = new(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<string, LanCatalogEntry> _catalog = new(StringComparer.Ordinal);
    private byte[] _catalogSecret = RandomNumberGenerator.GetBytes(32);
    private WebApplication? _application;
    private IReadOnlyList<IPAddress> _boundAddresses = [];
    private int _port = LanSharingSettingsStore.DefaultPort;
    private volatile bool _enabled;
    private volatile bool _disposed;
    private int _disposeStarted;
    private int _networkRestartPending;
    private string? _errorCode;
    private string? _errorMessage;

    internal LanMusicSharingService(
        IAppLogger logger,
        string? webRoot = null,
        Func<IReadOnlyList<IPAddress>>? addressProvider = null)
    {
        _logger = logger;
        _webRoot = webRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot", "lan");
        _addressProvider = addressProvider ?? LanAddressPolicy.GetAdvertisableAddresses;
        _networkChangeTimer = new System.Threading.Timer(
            _ => _ = RestartAfterNetworkChangeAsync(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<LanMusicSharingState>? StateChanged;

    public LanMusicSharingState CurrentState => CreateState();

    public void ReplaceLibrary(IEnumerable<LanSharedTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        lock (_catalogLock)
        {
            _sourceTracks = [];
            foreach (var track in tracks)
            {
                if (TryNormalizeSourcePath(track, out var normalizedPath))
                {
                    _sourceTracks[normalizedPath] = track;
                }
            }

            if (_application is not null)
            {
                RebuildCatalogLocked();
            }
            else
            {
                _catalog = new ConcurrentDictionary<string, LanCatalogEntry>(StringComparer.Ordinal);
            }
        }
        RaiseStateChanged();
    }

    public void UpsertLibrary(IEnumerable<LanSharedTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        lock (_catalogLock)
        {
            var normalizedTracks = new List<(string Path, LanSharedTrack Track)>();
            foreach (var track in tracks)
            {
                if (!TryNormalizeSourcePath(track, out var normalizedPath))
                {
                    continue;
                }

                _sourceTracks[normalizedPath] = track;
                normalizedTracks.Add((normalizedPath, track));
            }

            if (_application is not null && normalizedTracks.Count > 0)
            {
                // Clone the immutable-in-practice published snapshot, update only changed paths,
                // then swap once. This keeps batch visibility atomic without re-reading every
                // file in a 50k-track library for a one-song watcher event.
                var updated = new ConcurrentDictionary<string, LanCatalogEntry>(_catalog, StringComparer.Ordinal);
                foreach (var (path, track) in normalizedTracks)
                {
                    var publicId = CreatePublicId(path);
                    if (TryCreateCatalogEntry(track, out var entry))
                    {
                        updated[publicId] = entry;
                    }
                    else
                    {
                        updated.TryRemove(publicId, out _);
                    }
                }

                _catalog = updated;
            }
        }
        RaiseStateChanged();
    }

    public void RemoveTracks(IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        lock (_catalogLock)
        {
            var removedPaths = new List<string>();
            foreach (var path in sourcePaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        var normalizedPath = Path.GetFullPath(path);
                        if (_sourceTracks.Remove(normalizedPath))
                        {
                            removedPaths.Add(normalizedPath);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or
                                                       UnauthorizedAccessException or
                                                       ArgumentException or
                                                       NotSupportedException)
                    {
                        // Invalid removal input cannot name a catalog entry and is ignored.
                    }
                }
            }

            if (_application is not null && removedPaths.Count > 0)
            {
                var updated = new ConcurrentDictionary<string, LanCatalogEntry>(_catalog, StringComparer.Ordinal);
                foreach (var path in removedPaths)
                {
                    updated.TryRemove(CreatePublicId(path), out _);
                }

                _catalog = updated;
            }
        }
        RaiseStateChanged();
    }

    public void RemoveTrackIds(IEnumerable<string> sourceIds)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        var ids = sourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return;
        }

        lock (_catalogLock)
        {
            var removedPaths = new List<string>();
            foreach (var path in _sourceTracks
                         .Where(pair => ids.Contains(pair.Value.SourceId))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _sourceTracks.Remove(path);
                removedPaths.Add(path);
            }

            if (_application is not null && removedPaths.Count > 0)
            {
                var updated = new ConcurrentDictionary<string, LanCatalogEntry>(_catalog, StringComparer.Ordinal);
                foreach (var path in removedPaths)
                {
                    updated.TryRemove(CreatePublicId(path), out _);
                }

                _catalog = updated;
            }
        }
        RaiseStateChanged();
    }

    public async Task ApplySettingsAsync(LanSharingSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        settings = LanSharingSettingsStore.Normalize(settings);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _enabled = settings.Enabled;
            if (!settings.Enabled)
            {
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                _errorCode = null;
                _errorMessage = null;
                return;
            }

            if (_application is not null && _port == settings.Port)
            {
                return;
            }

            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(settings.Port, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            RaiseStateChanged();
        }
    }

    public void RegeneratePairingLink()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sessions.RotatePairingToken();
        RaiseStateChanged();
    }

    public void RevokeAllSessions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sessions.RevokeAll();
        RaiseStateChanged();
    }

    // Kestrel/hosting teardown has its own awaits. ConfigureAwait on our outer await alone
    // cannot stop those nested operations capturing WPF's non-pumping OnExit context.
    public ValueTask DisposeAsync() => new(Task.Run(DisposeCoreAsync));

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        await _networkChangeTimer.DisposeAsync().ConfigureAwait(false);

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _enabled = false;
            using var cancellation = new CancellationTokenSource(StopBudget);
            await StopCoreAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            // Network-change callbacks and aborted Kestrel responses may finish just after the
            // shutdown path. These two tiny process-lifetime semaphores intentionally remain
            // undisposed so a late callback cannot throw ObjectDisposedException during exit.
        }
    }

    private async Task StartCoreAsync(int port, CancellationToken cancellationToken)
    {
        _port = port;
        var indexPath = Path.Combine(_webRoot, "index.html");
        var scriptPath = Path.Combine(_webRoot, "lan-player.js");
        var stylePath = Path.Combine(_webRoot, "lan-player.css");
        if (!File.Exists(indexPath) || !File.Exists(scriptPath) || !File.Exists(stylePath))
        {
            SetError("assetsMissing", "局域网播放器资源不完整，请重新安装 Auralis。");
            return;
        }

        var lanAddresses = GetSafeLanAddresses();
        var listenAddresses = new[] { IPAddress.Loopback }.Concat(lanAddresses).Distinct().ToArray();
        WebApplication? app = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(LanMusicSharingService).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 8 * 1024;
                options.Limits.MaxConcurrentConnections = 64;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(1);
                foreach (var address in listenAddresses)
                {
                    options.Listen(address, port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
                }
            });

            app = builder.Build();
            ConfigurePipeline(app, indexPath, scriptPath, stylePath, listenAddresses);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _application = app;
            _boundAddresses = listenAddresses;
            lock (_catalogLock)
            {
                CryptographicOperations.ZeroMemory(_catalogSecret);
                _catalogSecret = RandomNumberGenerator.GetBytes(32);
                RebuildCatalogLocked();
            }
            _sessions.RevokeAll();
            _errorCode = null;
            _errorMessage = lanAddresses.Count == 0
                ? "当前没有可用的私有局域网地址；服务仅可在本机预览。"
                : null;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _logger.Log(
                AppLogLevel.Information,
                "lan.player",
                "server.started",
                new Dictionary<string, object?>
                {
                    [AppLogFieldNames.Status] = "running",
                    ["port"] = port,
                    ["addressCount"] = lanAddresses.Count,
                    ["trackCount"] = _catalog.Count
                });
        }
        catch (Exception exception)
        {
            if (app is not null)
            {
                try
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeException)
                {
                    _logger.Log(
                        AppLogLevel.Warning,
                        "lan.player",
                        "server.start-cleanup-failed",
                        exception: disposeException);
                }
            }
            SetError(
                exception is OperationCanceledException ? "startCanceled" : "bindFailed",
                exception is OperationCanceledException
                    ? "局域网播放器启动已取消。"
                    : "无法监听所选端口，请更换端口或检查 Windows 防火墙设置。");
            _logger.Log(AppLogLevel.Warning, "lan.player", "server.start-failed", exception: exception);
        }
    }

    private void ConfigurePipeline(
        WebApplication app,
        string indexPath,
        string scriptPath,
        string stylePath,
        IReadOnlyList<IPAddress> listenAddresses)
    {
        app.Use(async (context, next) =>
        {
            ApplySecurityHeaders(context.Response);
            if (!LanAddressPolicy.IsAllowedPeer(context.Connection.RemoteIpAddress) ||
                !IsAllowedHost(context.Request.Host, listenAddresses, _port))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var path = context.Request.Path;
            if (path.StartsWithSegments("/api/lan/v1"))
            {
                context.Response.Headers.CacheControl = "private, no-store";
            }

            if (path.StartsWithSegments("/api/lan/v1") &&
                !path.Equals("/api/lan/v1/session", StringComparison.OrdinalIgnoreCase) &&
                !_sessions.IsAuthorized(
                    context.Request.Cookies[SessionCookieName],
                    context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "pairingRequired" });
                return;
            }

            await next();
        });

        app.MapGet("/", (HttpContext context) => StaticFile(context, indexPath, "text/html; charset=utf-8", noStore: true));
        app.MapGet("/lan-player.js", (HttpContext context) => StaticFile(context, scriptPath, "text/javascript; charset=utf-8"));
        app.MapGet("/lan-player.css", (HttpContext context) => StaticFile(context, stylePath, "text/css; charset=utf-8"));
        app.MapGet("/favicon.ico", () => Results.NotFound());

        app.MapPost("/api/lan/v1/session", async (HttpContext context) =>
        {
            if (!HasSameOriginWhenPresent(context.Request))
            {
                return Results.Json(new { error = "originRejected" }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!context.Request.HasJsonContentType())
            {
                return Results.Json(
                    new { error = "jsonRequired" },
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }

            LanSessionRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<LanSessionRequest>(
                    context.Request.Body,
                    JsonOptions,
                    context.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "invalidRequest" });
            }

            var result = _sessions.TryExchange(
                request?.AccessToken,
                context.Connection.RemoteIpAddress,
                out var sessionId);
            if (result != LanPairingResult.Success || sessionId is null)
            {
                var status = result is LanPairingResult.RateLimited or LanPairingResult.CapacityReached
                    ? StatusCodes.Status429TooManyRequests
                    : StatusCodes.Status401Unauthorized;
                return Results.Json(
                    new { error = result.ToString().ToLowerInvariant() },
                    statusCode: status);
            }

            context.Response.Cookies.Append(
                SessionCookieName,
                sessionId,
                new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = false,
                    Path = "/",
                    MaxAge = LanAccessSessionManager.SessionLifetime
                });
            RaiseStateChanged();
            return Results.Json(new
            {
                version = 1,
                authenticated = true,
                expiresInSeconds = (int)LanAccessSessionManager.SessionLifetime.TotalSeconds
            });
        });

        app.MapGet("/api/lan/v1/library", () =>
        {
            var snapshot = _catalog;
            var tracks = snapshot.Values
                .OrderBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(track => track.Artist, StringComparer.CurrentCultureIgnoreCase)
                .Select(track => new
                {
                    track.Id,
                    track.Title,
                    track.Artist,
                    track.Album,
                    track.Extension,
                    track.Size,
                    track.DurationSeconds,
                    track.DateAdded,
                    contentType = ResolveAudioContentType(track.Extension),
                    audioUrl = $"/api/lan/v1/tracks/{track.Id}/audio",
                    coverUrl = track.ArtworkPath is null ? null : $"/api/lan/v1/tracks/{track.Id}/cover"
                })
                .ToArray();
            return Results.Json(new
            {
                version = 1,
                generatedAt = DateTimeOffset.UtcNow,
                trackCount = tracks.Length,
                tracks
            }, JsonOptions);
        });

        app.MapMethods("/api/lan/v1/tracks/{id}/audio", ["GET", "HEAD"], async (HttpContext context, string id) =>
        {
            if (!_catalog.TryGetValue(id, out var track) || !File.Exists(track.SourcePath))
            {
                await Results.NotFound().ExecuteAsync(context);
                return;
            }

            if (!await _streamGate.WaitAsync(0, context.RequestAborted))
            {
                await Results.Json(
                        new { error = "streamCapacityReached" },
                        statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context);
                return;
            }

            try
            {
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.AcceptRanges = "bytes";
                context.Response.Headers.ContentDisposition = "inline";
                context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
                var info = new FileInfo(track.SourcePath);
                await Results.File(
                        track.SourcePath,
                        ResolveAudioContentType(track.Extension),
                        enableRangeProcessing: true,
                        lastModified: new DateTimeOffset(info.LastWriteTimeUtc))
                    .ExecuteAsync(context);
            }
            finally
            {
                _streamGate.Release();
            }
        });

        app.MapMethods("/api/lan/v1/tracks/{id}/cover", ["GET", "HEAD"], (HttpContext context, string id) =>
        {
            if (!_catalog.TryGetValue(id, out var track) ||
                track.ArtworkPath is not { } artworkPath ||
                !File.Exists(artworkPath))
            {
                return Results.NotFound();
            }

            var info = new FileInfo(artworkPath);
            if (info.Length is <= 0 or > MaximumCoverBytes)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(
                artworkPath,
                ResolveImageContentType(Path.GetExtension(artworkPath)),
                enableRangeProcessing: false,
                lastModified: new DateTimeOffset(info.LastWriteTimeUtc));
        });

        app.MapFallback(() => Results.NotFound());
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _networkChangeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _networkRestartPending, 0);
        _sessions.RevokeAll();
        var app = _application;
        _application = null;
        _boundAddresses = [];
        lock (_catalogLock)
        {
            _catalog = new ConcurrentDictionary<string, LanCatalogEntry>(StringComparer.Ordinal);
        }
        if (app is null)
        {
            return;
        }

        try
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The app-wide shutdown budget is authoritative; disposal still releases listeners.
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
            _logger.Log(AppLogLevel.Information, "lan.player", "server.stopped");
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_enabled || _application is null)
        {
            return;
        }

        Interlocked.Exchange(ref _networkRestartPending, 1);
        _networkChangeTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private async Task RestartAfterNetworkChangeAsync()
    {
        if (Interlocked.Exchange(ref _networkRestartPending, 0) == 0 || _disposed || !_enabled)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !_enabled)
            {
                return;
            }

            var currentAddresses = GetSafeLanAddresses();
            var previousAddresses = _boundAddresses.Where(address => !IPAddress.IsLoopback(address)).ToArray();
            if (currentAddresses.SequenceEqual(previousAddresses))
            {
                return;
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await StopCoreAsync(cancellation.Token).ConfigureAwait(false);
            if (_enabled && !_disposed)
            {
                await StartCoreAsync(_port, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            SetError("networkChanged", "网络发生变化，局域网播放器暂时不可用；请在设置中重新启用。");
            _logger.Log(AppLogLevel.Warning, "lan.player", "network-rebind.failed", exception: exception);
        }
        finally
        {
            _lifecycleGate.Release();
            RaiseStateChanged();
        }
    }

    private LanMusicSharingState CreateState()
    {
        var baseUrls = _application is null
            ? Array.Empty<string>()
            : _boundAddresses
                .OrderBy(address => IPAddress.IsLoopback(address) ? 1 : 0)
                .Select(address => $"http://{FormatAddress(address)}:{_port}/")
                .ToArray();
        var token = _sessions.AccessToken;
        var pairingUrls = baseUrls
            .Select(url => url + "#access=" + Uri.EscapeDataString(token))
            .ToArray();
        return new LanMusicSharingState(
            _enabled,
            _application is not null,
            _port,
            baseUrls,
            pairingUrls,
            _application is null ? null : _sessions.PairingExpiresAt,
            GetSourceTrackCount(),
            _sessions.ActiveSessionCount,
            _errorCode,
            _errorMessage);
    }

    private void RebuildCatalogLocked()
    {
        var rebuilt = new ConcurrentDictionary<string, LanCatalogEntry>(StringComparer.Ordinal);
        foreach (var track in _sourceTracks.Values)
        {
            if (TryCreateCatalogEntry(track, out var entry))
            {
                rebuilt.TryAdd(entry.Id, entry);
            }
        }

        _catalog = rebuilt;
    }

    private string CreatePublicId(string path)
    {
        using var hmac = new HMACSHA256(_catalogSecret);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return Convert.ToBase64String(digest.AsSpan(0, 18))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private string? NormalizeArtworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var coverRoot = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Auralis",
                    "Covers"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(coverRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void SetError(string code, string message)
    {
        _errorCode = code;
        _errorMessage = message;
    }

    private void RaiseStateChanged()
    {
        if (_disposed)
        {
            return;
        }

        var state = CreateState();
        foreach (EventHandler<LanMusicSharingState> handler in StateChanged?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, state);
            }
            catch (Exception exception)
            {
                _logger.Log(AppLogLevel.Warning, "lan.player", "state-observer.failed", exception: exception);
            }
        }
    }

    private static bool TryNormalizeSourcePath(LanSharedTrack track, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(track.SourcePath) || string.IsNullOrWhiteSpace(track.Title))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(track.SourcePath);
            var extension = Path.GetExtension(normalizedPath);
            if (!Path.IsPathFullyQualified(normalizedPath) ||
                !SharedAudioExtensions.Contains(extension))
            {
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    private bool TryCreateCatalogEntry(LanSharedTrack track, out LanCatalogEntry entry)
    {
        entry = null!;
        if (!TryNormalizeSourcePath(track, out var normalizedPath) || !File.Exists(normalizedPath))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(normalizedPath);
            entry = new LanCatalogEntry(
                CreatePublicId(normalizedPath),
                normalizedPath,
                NormalizeDisplayText(track.Title, 512, "未知歌曲"),
                NormalizeDisplayText(track.Artist, 256, "未知艺术家"),
                NormalizeDisplayText(track.Album, 256, "本地音乐"),
                info.Extension.TrimStart('.').ToUpperInvariant(),
                info.Length,
                track.DurationSeconds is >= 0 and <= 86_400 ? track.DurationSeconds : null,
                track.DateAdded,
                NormalizeArtworkPath(track.ArtworkPath));
            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            entry = null!;
            return false;
        }
    }

    private int GetSourceTrackCount()
    {
        lock (_catalogLock)
        {
            return _sourceTracks.Count;
        }
    }

    private static string NormalizeDisplayText(string? value, int maximumLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = new string(value
            .Where(character => !char.IsControl(character) || character is '\t')
            .ToArray())
            .Trim();
        if (normalized.Length == 0)
        {
            return fallback;
        }

        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static IResult StaticFile(HttpContext context, string path, string contentType, bool noStore = false)
    {
        context.Response.Headers.CacheControl = noStore ? "no-store" : "public, max-age=3600";
        return Results.File(path, contentType, enableRangeProcessing: false);
    }

    private static void ApplySecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy =
            "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; " +
            "script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; connect-src 'self'; form-action 'none'";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    }

    private static bool IsAllowedHost(
        HostString hostHeader,
        IReadOnlyList<IPAddress> listenAddresses,
        int expectedPort)
    {
        if (hostHeader.Port != expectedPort)
        {
            return false;
        }

        var host = hostHeader.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        host = host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return listenAddresses.Any(candidate => candidate.Equals(address));
    }

    private IReadOnlyList<IPAddress> GetSafeLanAddresses()
    {
        try
        {
            var safeAddresses = new List<IPAddress>();
            var seen = new HashSet<IPAddress>();
            foreach (var candidate in _addressProvider() ?? [])
            {
                if (candidate is null)
                {
                    continue;
                }

                var address = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
                if (!IPAddress.IsLoopback(address) && LanAddressPolicy.IsAllowedPeer(address) && seen.Add(address))
                {
                    // Provider order is intentional: the first URL should be the real LAN adapter,
                    // not an alphabetically earlier host-only virtual adapter.
                    safeAddresses.Add(address);
                }
            }
            return safeAddresses;
        }
        catch (Exception exception) when (exception is NetworkInformationException or SocketException)
        {
            _logger.Log(AppLogLevel.Warning, "lan.player", "address-enumeration.failed", exception: exception);
            return [];
        }
    }

    private static bool HasSameOriginWhenPresent(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
               string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatAddress(IPAddress address)
    {
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }

    private static string ResolveAudioContentType(string extension)
    {
        return extension.TrimStart('.').ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "m4a" => "audio/mp4",
            "aac" => "audio/aac",
            "ogg" => "audio/ogg",
            "opus" => "audio/ogg",
            "wma" => "audio/x-ms-wma",
            _ => "application/octet-stream"
        };
    }

    private static string ResolveImageContentType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
    }

    private sealed record LanSessionRequest(string? AccessToken);

    private sealed record LanCatalogEntry(
        string Id,
        string SourcePath,
        string Title,
        string Artist,
        string Album,
        string Extension,
        long Size,
        double? DurationSeconds,
        DateTime DateAdded,
        string? ArtworkPath);
}
