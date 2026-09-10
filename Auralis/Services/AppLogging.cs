using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Auralis.Services;

/// <summary>Severity used by the host-owned, privacy-preserving application log.</summary>
internal enum AppLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Small logging surface shared by native services. Components and event IDs are stable identifiers;
/// user-controlled text belongs only in allow-listed structured fields and is redacted before writing.
/// </summary>
internal interface IAppLogger
{
    string? LogDirectory { get; }

    long DroppedEntryCount { get; }

    void Log(
        AppLogLevel level,
        string component,
        string eventId,
        IReadOnlyDictionary<string, object?>? fields = null,
        Exception? exception = null);

    ValueTask<bool> FlushAsync(TimeSpan timeout);

    ValueTask<bool> StopAsync(TimeSpan timeout);
}

/// <summary>Central allow-list for structured log properties.</summary>
internal static class AppLogFieldNames
{
    internal const string Action = "action";
    internal const string Count = "count";
    internal const string Dpi = "dpi";
    internal const string DroppedCount = "droppedCount";
    internal const string DurationMilliseconds = "durationMs";
    internal const string Message = "message";
    internal const string Operation = "operation";
    internal const string Path = "path";
    internal const string PluginId = "pluginId";
    internal const string Provider = "provider";
    internal const string Reason = "reason";
    internal const string Result = "result";
    internal const string Source = "source";
    internal const string Status = "status";
    internal const string Version = "version";
    internal const string WindowState = "windowState";

    internal static bool IsAllowed(string name) => Allowed.Contains(name);

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        Action,
        Count,
        Dpi,
        DroppedCount,
        DurationMilliseconds,
        Message,
        Operation,
        Path,
        PluginId,
        Provider,
        Reason,
        Result,
        Source,
        Status,
        Version,
        WindowState
    };
}

/// <summary>Creates a logger without allowing diagnostics failures to prevent application startup.</summary>
internal static class AppLoggerFactory
{
    internal static IAppLogger CreateDefault()
    {
        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
            {
                return NullAppLogger.Instance;
            }

            return new RollingFileAppLogger(new AppLogOptions(
                Path.Combine(localData, "Auralis", "Logs")));
        }
        catch
        {
            return NullAppLogger.Instance;
        }
    }
}

/// <summary>Settings are injectable so retention, rolling, and concurrency can be tested deterministically.</summary>
internal sealed record AppLogOptions(
    string DirectoryPath,
    long MaximumFileBytes = 4L * 1024L * 1024L,
    int MaximumFileCount = 5,
    int RetentionDays = 14,
    int QueueCapacity = 1024)
{
    internal AppLogOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DirectoryPath);
        if (MaximumFileBytes < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        }

        if (MaximumFileCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileCount));
        }

        if (RetentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(RetentionDays));
        }

        if (QueueCapacity is < 8 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        }

        return this;
    }
}

/// <summary>
/// Bounded, non-blocking JSONL logger. All directory and file work happens on a single background reader;
/// producers only copy a small scalar field set and attempt a channel write.
/// </summary>
internal sealed class RollingFileAppLogger : IAppLogger, IAsyncDisposable
{
    private const string FilePrefix = "auralis-";
    private const string FileExtension = ".jsonl";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AppLogOptions _options;
    private readonly Channel<LogCommand> _channel;
    private readonly Task _writerTask;
    private long _droppedEntryCount;
    private int _stopStarted;

    internal RollingFileAppLogger(AppLogOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _channel = Channel.CreateBounded<LogCommand>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        // Task.Run prevents directory creation, retention scans, and the first file open from running inline
        // on the WPF startup thread even when the first event is emitted immediately.
        _writerTask = Task.Run(ProcessQueueAsync);
    }

    public string? LogDirectory => _options.DirectoryPath;

    public long DroppedEntryCount => Interlocked.Read(ref _droppedEntryCount);

    public void Log(
        AppLogLevel level,
        string component,
        string eventId,
        IReadOnlyDictionary<string, object?>? fields = null,
        Exception? exception = null)
    {
        if (Volatile.Read(ref _stopStarted) != 0)
        {
            return;
        }

        try
        {
            var entry = new AppLogEntry(
                DateTimeOffset.UtcNow,
                level,
                AppLogSanitizer.NormalizeIdentifier(component, "application"),
                AppLogSanitizer.NormalizeIdentifier(eventId, "event"),
                SnapshotFields(fields),
                exception is null
                    ? null
                    : new AppLogException(exception.GetType().FullName ?? exception.GetType().Name, exception.HResult));

            if (!_channel.Writer.TryWrite(LogCommand.ForEntry(entry)))
            {
                Interlocked.Increment(ref _droppedEntryCount);
            }
        }
        catch
        {
            // Invalid or concurrently mutated diagnostic input is dropped instead of escaping to callers.
            Interlocked.Increment(ref _droppedEntryCount);
        }
    }

    public async ValueTask<bool> FlushAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || Volatile.Read(ref _stopStarted) != 0)
        {
            return false;
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _channel.Writer.WriteAsync(LogCommand.ForFlush(completion), timeoutSource.Token)
                .ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    public async ValueTask<bool> StopAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return _writerTask.IsCompleted;
        }

        if (Interlocked.Exchange(ref _stopStarted, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }

        try
        {
            await _writerTask.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            // A logger must never destabilize shutdown. ProcessQueueAsync already contains its own guard,
            // but this remains a final safety boundary for runtime or disposal failures.
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static IReadOnlyList<AppLogField> SnapshotFields(IReadOnlyDictionary<string, object?>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return Array.Empty<AppLogField>();
        }

        var snapshot = new List<AppLogField>(Math.Min(fields.Count, 16));
        foreach (var pair in fields)
        {
            if (snapshot.Count >= 16 || !AppLogFieldNames.IsAllowed(pair.Key))
            {
                continue;
            }

            if (TrySnapshotScalar(pair.Value, out var value))
            {
                snapshot.Add(new AppLogField(pair.Key, value));
            }
        }

        return snapshot;
    }

    private static bool TrySnapshotScalar(object? value, out object? snapshot)
    {
        switch (value)
        {
            case null:
                snapshot = null;
                return true;
            case string text:
                snapshot = text.Length <= AppLogSanitizer.MaximumInputLength
                    ? text
                    : text[..AppLogSanitizer.MaximumInputLength];
                return true;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                snapshot = value;
                return true;
            case DateTime timestamp:
                snapshot = timestamp.ToUniversalTime();
                return true;
            case DateTimeOffset timestamp:
                snapshot = timestamp.ToUniversalTime();
                return true;
            case TimeSpan duration:
                snapshot = duration.TotalMilliseconds;
                return true;
            case Enum enumValue:
                snapshot = enumValue.ToString();
                return true;
            default:
                snapshot = null;
                return false;
        }
    }

    private async Task ProcessQueueAsync()
    {
        StreamWriter? writer = null;
        long currentFileBytes = 0;
        var sequence = 0;
        var outputAvailable = true;

        try
        {
            try
            {
                Directory.CreateDirectory(_options.DirectoryPath);
                ApplyRetentionPolicy();
            }
            catch
            {
                outputAvailable = false;
            }

            await foreach (var command in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (command.FlushCompletion is not null)
                {
                    try
                    {
                        if (writer is not null)
                        {
                            await writer.FlushAsync().ConfigureAwait(false);
                        }

                        command.FlushCompletion.TrySetResult(outputAvailable);
                    }
                    catch
                    {
                        outputAvailable = false;
                        command.FlushCompletion.TrySetResult(false);
                    }

                    continue;
                }

                if (!outputAvailable || command.Entry is null)
                {
                    continue;
                }

                try
                {
                    var serialized = Serialize(command.Entry);
                    if (writer is null || (currentFileBytes > 0 && currentFileBytes + serialized.Length > _options.MaximumFileBytes))
                    {
                        if (writer is not null)
                        {
                            await writer.FlushAsync().ConfigureAwait(false);
                            await writer.DisposeAsync().ConfigureAwait(false);
                        }

                        writer = OpenNextFile(sequence++);
                        currentFileBytes = 0;
                        ApplyRetentionPolicy();
                    }

                    await writer.BaseStream.WriteAsync(serialized).ConfigureAwait(false);
                    currentFileBytes += serialized.Length;
                }
                catch
                {
                    outputAvailable = false;
                    if (writer is not null)
                    {
                        try
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            // Best effort only. The app continues with file logging disabled.
                        }

                        writer = null;
                    }
                }
            }
        }
        catch
        {
            // Logging is deliberately fail-closed and never faults the process or startup path.
        }
        finally
        {
            if (writer is not null)
            {
                try
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // The process is already shutting down; nothing else should depend on this flush.
                }
            }
        }
    }

    private StreamWriter OpenNextFile(int sequence)
    {
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{FilePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId:D6}-{sequence:D3}{FileExtension}");
        var path = Path.Combine(_options.DirectoryPath, name);
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new StreamWriter(stream, Utf8NoBom, bufferSize: 16 * 1024, leaveOpen: false);
    }

    private void ApplyRetentionPolicy()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
        var candidates = new DirectoryInfo(_options.DirectoryPath)
            .EnumerateFiles($"{FilePrefix}*{FileExtension}", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.Name, StringComparer.Ordinal)
            .ToArray();

        for (var index = 0; index < candidates.Length; index++)
        {
            if (candidates[index].LastWriteTimeUtc < cutoff || index >= _options.MaximumFileCount)
            {
                TryDelete(candidates[index]);
            }
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
            // A file may be inspected by the user or antivirus. Retention retries on the next roll/start.
        }
    }

    private static ReadOnlyMemory<byte> Serialize(AppLogEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = false }))
        {
            json.WriteStartObject();
            json.WriteString("timestampUtc", entry.TimestampUtc);
            json.WriteString("level", entry.Level.ToString());
            json.WriteString("component", entry.Component);
            json.WriteString("eventId", entry.EventId);

            if (entry.Fields.Count > 0)
            {
                json.WriteStartObject("fields");
                foreach (var field in entry.Fields.OrderBy(static field => field.Name, StringComparer.Ordinal))
                {
                    WriteField(json, field.Name, field.Value);
                }

                json.WriteEndObject();
            }

            if (entry.Exception is not null)
            {
                json.WriteStartObject("exception");
                json.WriteString("type", AppLogSanitizer.SanitizeText(entry.Exception.Type));
                json.WriteString("hresult", $"0x{entry.Exception.HResult:X8}");
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.Flush();
        }

        var result = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(result);
        result[^1] = (byte)'\n';
        return result;
    }

    private static void WriteField(Utf8JsonWriter json, string name, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(name);
                break;
            case string text:
                json.WriteString(name, AppLogSanitizer.SanitizeText(text));
                break;
            case bool boolean:
                json.WriteBoolean(name, boolean);
                break;
            case byte number:
                json.WriteNumber(name, number);
                break;
            case sbyte number:
                json.WriteNumber(name, number);
                break;
            case short number:
                json.WriteNumber(name, number);
                break;
            case ushort number:
                json.WriteNumber(name, number);
                break;
            case int number:
                json.WriteNumber(name, number);
                break;
            case uint number:
                json.WriteNumber(name, number);
                break;
            case long number:
                json.WriteNumber(name, number);
                break;
            case ulong number:
                json.WriteNumber(name, number);
                break;
            case float number when float.IsFinite(number):
                json.WriteNumber(name, number);
                break;
            case double number when double.IsFinite(number):
                json.WriteNumber(name, number);
                break;
            case decimal number:
                json.WriteNumber(name, number);
                break;
            case DateTime timestamp:
                json.WriteString(name, timestamp);
                break;
            case DateTimeOffset timestamp:
                json.WriteString(name, timestamp);
                break;
            default:
                json.WriteString(name, AppLogSanitizer.SanitizeText(Convert.ToString(value, CultureInfo.InvariantCulture)));
                break;
        }
    }

    private sealed record AppLogEntry(
        DateTimeOffset TimestampUtc,
        AppLogLevel Level,
        string Component,
        string EventId,
        IReadOnlyList<AppLogField> Fields,
        AppLogException? Exception);

    private sealed record AppLogField(string Name, object? Value);

    private sealed record AppLogException(string Type, int HResult);

    private sealed record LogCommand(AppLogEntry? Entry, TaskCompletionSource<bool>? FlushCompletion)
    {
        internal static LogCommand ForEntry(AppLogEntry entry) => new(entry, null);

        internal static LogCommand ForFlush(TaskCompletionSource<bool> completion) => new(null, completion);
    }
}

/// <summary>Privacy boundary applied to every untrusted string immediately before persistence.</summary>
internal static class AppLogSanitizer
{
    internal const int MaximumInputLength = 2048;
    private const int MaximumOutputLength = 1024;
    private const string Redacted = "[REDACTED]";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly Regex Url = new(
        @"\bhttps?://[^\s<>\""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex SensitiveHeader = new(
        @"(?im)\b(authorization|proxy-authorization|cookie|set-cookie)\s*:\s*[^\r\n]*",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex Bearer = new(
        @"(?i)\bbearer\s+[a-z0-9._~+/=-]+",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex SensitiveAssignment = new(
        @"(?i)([\""']?(?:access[_-]?token|refresh[_-]?token|token|credential|secret|password|passwd|cookie)[\""']?\s*[:=]\s*)(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s,;}\]]+)",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex DrivePath = new(
        @"(?i)\b[a-z]:[\\/][^\r\n\t]*",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex UncPath = new(
        @"\\\\[^\r\n\t]+",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex Identifier = new(
        @"[^a-z0-9._-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    internal static string NormalizeIdentifier(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var normalized = Identifier.Replace(value.Trim(), "-").Trim('-', '.', '_').ToLowerInvariant();
            return string.IsNullOrWhiteSpace(normalized)
                ? fallback
                : normalized[..Math.Min(normalized.Length, 80)];
        }
        catch (RegexMatchTimeoutException)
        {
            return fallback;
        }
    }

    internal static string SanitizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value.Length <= MaximumInputLength ? value : value[..MaximumInputLength];
        try
        {
            text = SensitiveHeader.Replace(text, static match => $"{match.Groups[1].Value}: {Redacted}");
            text = Bearer.Replace(text, $"Bearer {Redacted}");
            text = SensitiveAssignment.Replace(text, static match => $"{match.Groups[1].Value}{Redacted}");
            text = Url.Replace(text, RedactUrl);
            text = DrivePath.Replace(text, Redacted);
            text = UncPath.Replace(text, Redacted);
            text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return text.Length <= MaximumOutputLength ? text : text[..MaximumOutputLength];
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological value is never more important than the privacy boundary.
            return Redacted;
        }
    }

    private static string RedactUrl(Match match)
    {
        if (!Uri.TryCreate(match.Value.TrimEnd('.', ',', ';', ')', ']'), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "[REDACTED_URL]";
        }

        var authority = uri.IsDefaultPort
            ? uri.Host
            : string.Create(CultureInfo.InvariantCulture, $"{uri.Host}:{uri.Port}");
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var suffix = string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? string.Empty
            : "?[REDACTED_QUERY]";
        return $"{uri.Scheme}://{authority}{path}{suffix}";
    }
}

/// <summary>Fail-safe logger used only when the default diagnostics path cannot be initialized.</summary>
internal sealed class NullAppLogger : IAppLogger
{
    internal static NullAppLogger Instance { get; } = new();

    private NullAppLogger()
    {
    }

    public string? LogDirectory => null;

    public long DroppedEntryCount => 0;

    public void Log(
        AppLogLevel level,
        string component,
        string eventId,
        IReadOnlyDictionary<string, object?>? fields = null,
        Exception? exception = null)
    {
    }

    public ValueTask<bool> FlushAsync(TimeSpan timeout) => ValueTask.FromResult(false);

    public ValueTask<bool> StopAsync(TimeSpan timeout) => ValueTask.FromResult(true);
}
