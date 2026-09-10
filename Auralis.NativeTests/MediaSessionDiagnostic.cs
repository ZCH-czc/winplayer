using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Windows.Media.Control;
using Windows.Storage.Streams;

internal static class MediaSessionDiagnostic
{
    private const int SessionUnavailableExitCode = 3;
    private const int SessionNotFoundExitCode = 4;
    private const int AmbiguousSessionExitCode = 5;
    private const int MetadataUnavailableExitCode = 6;
    private const int MetadataMismatchExitCode = 7;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    internal static async Task<int> RunAsync(string? expectedTitle)
    {
        expectedTitle = string.IsNullOrWhiteSpace(expectedTitle) ? null : expectedTitle.Trim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        GlobalSystemMediaTransportControlsSessionManager manager;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                .AsTask(timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or UnauthorizedAccessException or
                                           InvalidOperationException or COMException)
        {
            Console.Error.WriteLine($"Unable to read Windows media sessions: {exception.Message}");
            return SessionUnavailableExitCode;
        }

        var sessions = manager.GetSessions().ToArray();
        var candidates = new List<SessionCandidate>(sessions.Length);
        foreach (var session in sessions)
        {
            candidates.Add(await ReadSessionAsync(session, timeout.Token));
        }

        var sourceMatches = candidates
            .Where(candidate => IsAuralisApplicationId(candidate.Snapshot.ApplicationId))
            .ToArray();
        var matchingTitle = expectedTitle is null
            ? Array.Empty<SessionCandidate>()
            : candidates.Where(candidate => string.Equals(
                candidate.Snapshot.Title,
                expectedTitle,
                StringComparison.Ordinal)).ToArray();

        SessionCandidate? selected = null;
        var matchKind = "sourceApplicationId";
        if (sourceMatches.Length == 1)
        {
            selected = sourceMatches[0];
        }
        else if (sourceMatches.Length > 1 && expectedTitle is not null)
        {
            var narrowed = sourceMatches.Where(candidate => string.Equals(
                candidate.Snapshot.Title,
                expectedTitle,
                StringComparison.Ordinal)).ToArray();
            if (narrowed.Length == 1)
            {
                selected = narrowed[0];
            }
        }
        else if (sourceMatches.Length == 0 && expectedTitle is not null &&
                 matchingTitle.Length == 1 && IsAuralisProcessRunning())
        {
            // Some unpackaged Win32 builds expose a generated source app id. In that case an exact
            // requested title plus a live Auralis.exe process is the narrow, read-only fallback.
            selected = matchingTitle[0];
            matchKind = "expectedTitleWithRunningProcess";
        }

        if (selected is null)
        {
            if (sourceMatches.Length > 1 ||
                (sourceMatches.Length == 0 && expectedTitle is not null && matchingTitle.Length > 1))
            {
                Console.Error.WriteLine(
                    "More than one Windows media session could represent Auralis; provide the exact expected title after 'media-session'.");
                return AmbiguousSessionExitCode;
            }

            var visibleApplicationIds = candidates
                .Select(candidate => candidate.Snapshot.ApplicationId)
                .Where(applicationId => !string.IsNullOrWhiteSpace(applicationId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Console.Error.WriteLine("No Auralis Windows media session was found.");
            if (visibleApplicationIds.Length > 0)
            {
                Console.Error.WriteLine($"Visible source application IDs: {string.Join(", ", visibleApplicationIds)}");
            }
            return SessionNotFoundExitCode;
        }

        var result = new MediaSessionDiagnosticResult(
            sessions.Length,
            matchKind,
            expectedTitle,
            expectedTitle is null || string.Equals(selected.Snapshot.Title, expectedTitle, StringComparison.Ordinal),
            selected.Snapshot);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));

        if (selected.Snapshot.ReadError is not null)
        {
            Console.Error.WriteLine($"Auralis media metadata could not be read: {selected.Snapshot.ReadError}");
            return MetadataUnavailableExitCode;
        }

        if (!result.MetadataMatches)
        {
            Console.Error.WriteLine(
                $"Auralis media title mismatch. Expected '{expectedTitle}', actual '{selected.Snapshot.Title}'.");
            return MetadataMismatchExitCode;
        }

        return 0;
    }

    private static async Task<SessionCandidate> ReadSessionAsync(
        GlobalSystemMediaTransportControlsSession session,
        CancellationToken cancellationToken)
    {
        string applicationId;
        try
        {
            applicationId = session.SourceAppUserModelId ?? string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return new SessionCandidate(EmptySnapshot(string.Empty, exception.Message));
        }

        try
        {
            var media = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var thumbnail = media.Thumbnail;
            var snapshot = new MediaSessionSnapshot(
                applicationId,
                media.Title ?? string.Empty,
                media.Artist ?? string.Empty,
                media.AlbumTitle ?? string.Empty,
                thumbnail is not null,
                GetDirectContentType(thumbnail),
                playback.PlaybackStatus.ToString(),
                new MediaTimelineSnapshot(
                    ToSeconds(timeline.StartTime),
                    ToSeconds(timeline.EndTime),
                    ToSeconds(timeline.Position),
                    ToSeconds(timeline.MinSeekTime),
                    ToSeconds(timeline.MaxSeekTime),
                    timeline.LastUpdatedTime),
                null);
            return new SessionCandidate(snapshot);
        }
        catch (Exception exception) when (exception is OperationCanceledException or UnauthorizedAccessException or
                                           InvalidOperationException or COMException)
        {
            return new SessionCandidate(EmptySnapshot(applicationId, exception.Message));
        }
    }

    private static MediaSessionSnapshot EmptySnapshot(string applicationId, string error) =>
        new(
            applicationId,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            null,
            "Unavailable",
            new MediaTimelineSnapshot(0, 0, 0, 0, 0, DateTimeOffset.MinValue),
            error);

    private static bool IsAuralisApplicationId(string applicationId) =>
        applicationId.Contains("auralis", StringComparison.OrdinalIgnoreCase);

    private static double ToSeconds(TimeSpan value) =>
        Math.Round(value.TotalSeconds, 3, MidpointRounding.AwayFromZero);

    private static string? GetDirectContentType(IRandomAccessStreamReference? thumbnail)
    {
        // Do not call OpenReadAsync: a diagnostic presence check must not open, fetch, or read the
        // artwork payload. Most references expose no MIME type until opened, so null is expected.
        if (thumbnail is not IRandomAccessStreamWithContentType stream)
        {
            return null;
        }

        try
        {
            var contentType = stream.ContentType;
            return string.IsNullOrWhiteSpace(contentType) ? null : contentType;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static bool IsAuralisProcessRunning()
    {
        var processes = Process.GetProcessesByName("Auralis");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private sealed record SessionCandidate(MediaSessionSnapshot Snapshot);
}

internal sealed record MediaSessionDiagnosticResult(
    int SessionCount,
    string MatchKind,
    string? ExpectedTitle,
    bool MetadataMatches,
    MediaSessionSnapshot Session);

internal sealed record MediaSessionSnapshot(
    string ApplicationId,
    string Title,
    string Artist,
    string Album,
    bool ThumbnailPresent,
    string? ThumbnailContentType,
    string PlaybackStatus,
    MediaTimelineSnapshot Timeline,
    string? ReadError);

internal sealed record MediaTimelineSnapshot(
    double StartSeconds,
    double EndSeconds,
    double PositionSeconds,
    double MinimumSeekSeconds,
    double MaximumSeekSeconds,
    DateTimeOffset LastUpdatedAt);
