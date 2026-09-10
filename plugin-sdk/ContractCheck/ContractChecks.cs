using Auralis.Platform.Abstractions;

namespace Auralis.PluginContractCheck;

// Findings deliberately contain only fixed codes, never provider messages, URLs or headers.
public enum Finding
{
    Passed, NotExecuted, InvalidResult, InvalidErrorCode, InvalidRetryAfter, UnexpectedResult,
    ThrewException, TimedOut, CancellationIgnored, InvalidLeaseUrl, ExpiredLease, MissingExpiry,
    InvalidHeader, InvalidVideoAlternates, InvalidQuality, InvalidPage, WrongProvider
}

/// <summary>Offline assertions for plugin authors. These are not playback or access-rights probes.</summary>
public static class ContractChecks
{
    public static Finding Result<T>(PlatformResult<T> result)
    {
        if (result.IsSuccess) return result.Value is null ? Finding.InvalidResult : Finding.Passed;
        if (result.Error is not { } error) return Finding.InvalidResult; // default(PlatformResult) is invalid.
        if (!Enum.IsDefined(error.Code)) return Finding.InvalidErrorCode;
        if (error.RetryAfter < TimeSpan.Zero) return Finding.InvalidRetryAfter;
        return Finding.Passed;
    }

    public static Finding AudioLease(PlatformStreamLease lease, IPlatformTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(clock);
        var shape = Lease(lease.Url, lease.ExpiresAt, lease.RequestHeaders, clock);
        if (shape is not (Finding.Passed or Finding.MissingExpiry)) return shape;
        return lease.Quality.BitrateKbps is <= 0 ? Finding.InvalidQuality : shape;
    }

    public static Finding VideoLease(PlatformVideoLease lease, IPlatformTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(clock);
        var shape = Lease(lease.Url, lease.ExpiresAt, lease.RequestHeaders, clock);
        if (shape is not (Finding.Passed or Finding.MissingExpiry)) return shape;
        if (lease.AlternateUrls is null || lease.AlternateUrls.Any(url => !ValidUrl(url)))
            return Finding.InvalidVideoAlternates;
        var audioCheck = lease.AudioStream is { } audio ? AudioLease(audio, clock) : Finding.Passed;
        return audioCheck != Finding.Passed ? audioCheck : shape;
    }

    private static Finding Lease(Uri url, DateTimeOffset? expiry, IReadOnlyDictionary<string, string> headers, IPlatformTimeProvider clock)
    {
        if (!ValidUrl(url)) return Finding.InvalidLeaseUrl;
        if (expiry is { } time && time <= clock.GetUtcNow()) return Finding.ExpiredLease;
        if (headers.Any(h => string.IsNullOrEmpty(h.Key) || h.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) ||
            h.Value is null || h.Value.Any(c => c is '\r' or '\n' or '\0'))) return Finding.InvalidHeader;
        // The contract permits unknown expiry: report it as not verified, never invent a lifetime.
        return expiry is null ? Finding.MissingExpiry : Finding.Passed;
    }

    private static bool ValidUrl(Uri? url) => url is { IsAbsoluteUri: true } && string.IsNullOrEmpty(url.UserInfo) &&
        (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));

    public static Finding TrackPage(PlatformPage<PlatformTrack> page, string providerId, int requestedSize)
    {
        if (requestedSize is < 1 or > 200 || page.Items.Count > requestedSize || page.TotalCount is < 0 ||
            page.Items.Any(t => t is null)) return Finding.InvalidPage;
        return page.Items.Any(t => !t.Id.IsForProvider(providerId)) ? Finding.WrongProvider : Finding.Passed;
    }

    /// <summary>Calls an author-supplied fixture operation, not an arbitrary real platform endpoint.
    /// Timeout bounds observation only; use a disposable process for code which may ignore cancellation.</summary>
    public static async Task<Finding> OperationAsync<T>(Func<CancellationToken, Task<PlatformResult<T>>> operation,
        PlatformErrorCode? expectedError = null, Func<T, Finding>? inspect = null, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource();
        var budget = Budget(timeout);
        var task = Task.Run(() => operation(cancellation.Token));
        try
        {
            var result = await task.WaitAsync(budget).ConfigureAwait(false);
            var shape = Result(result);
            if (shape != Finding.Passed) return shape;
            if (expectedError is { } code) return !result.IsSuccess && result.Error!.Code == code ? Finding.Passed : Finding.UnexpectedResult;
            return !result.IsSuccess ? Finding.UnexpectedResult : inspect?.Invoke(result.Value) ?? Finding.Passed;
        }
        catch (TimeoutException) { return Finding.TimedOut; }
        catch (Exception) { return Finding.ThrewException; }
        finally { Observe(cancellation.CancelAsync()); Observe(task); }
    }

    /// <summary>Tests pre-cancellation, or delayed cancellation for a deliberately blocked fixture operation, without Host error mapping.</summary>
    public static async Task<Finding> CancellationAsync<T>(Func<CancellationToken, Task<PlatformResult<T>>> operation, TimeSpan? timeout = null, TimeSpan? cancelAfter = null)
    {
        using var cancellation = new CancellationTokenSource();
        var budget = Budget(timeout);
        if (cancelAfter is { } delay)
        {
            if (delay <= TimeSpan.Zero || delay >= budget) throw new ArgumentOutOfRangeException(nameof(cancelAfter));
            cancellation.CancelAfter(delay);
        }
        else cancellation.Cancel();
        var task = Task.Run(() => operation(cancellation.Token));
        try
        {
            var result = await task.WaitAsync(budget).ConfigureAwait(false);
            return cancellation.IsCancellationRequested && !result.IsSuccess && result.Error?.Code == PlatformErrorCode.Cancelled ? Finding.Passed : Finding.CancellationIgnored;
        }
        catch (OperationCanceledException e) when (cancellation.IsCancellationRequested && e.CancellationToken == cancellation.Token) { return Finding.Passed; }
        catch (TimeoutException) { return Finding.TimedOut; }
        catch (Exception) { return Finding.ThrewException; }
        finally { Observe(task); }
    }

    private static TimeSpan Budget(TimeSpan? value)
    {
        var budget = value ?? TimeSpan.FromSeconds(3);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(value));
        return budget;
    }
    private static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
