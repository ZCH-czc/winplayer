using System.Windows;
using Auralis.Services;

namespace Auralis;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan LoggerShutdownBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FatalLogFlushBudget = TimeSpan.FromMilliseconds(500);
    private readonly IAppLogger _logger = AppLoggerFactory.CreateDefault();
    private readonly Lazy<PlatformBackendService> _platformBackend;
    private readonly Lazy<LanMusicSharingService> _lanMusicSharing;

    public App()
    {
        _platformBackend = new Lazy<PlatformBackendService>(
            () => new PlatformBackendService(_logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _lanMusicSharing = new Lazy<LanMusicSharingService>(
            () => new LanMusicSharingService(_logger),
            LazyThreadSafetyMode.ExecutionAndPublication);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _logger.Log(AppLogLevel.Information, "application", "lifecycle.starting");
    }

    /// <summary>
    /// Gets the optional online-platform backend. Access creates only a dormant host; callers must make an
    /// explicit discovery or routed request before any plugin file is read or any network access is possible.
    /// </summary>
    public PlatformBackendService PlatformBackend => _platformBackend.Value;

    /// <summary>
    /// Gets the dormant LAN browser player. Constructing it does not bind a port; a persisted,
    /// explicit user opt-in is still required before ApplySettingsAsync starts the endpoint.
    /// </summary>
    public LanMusicSharingService LanMusicSharing => _lanMusicSharing.Value;

    internal IAppLogger Logger => _logger;

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop externally reachable listeners first. Each optional backend is isolated so a
        // failure in one cleanup path can never skip another one.
        try
        {
            if (_lanMusicSharing.IsValueCreated)
            {
                // OnExit cannot pump dispatcher continuations. Enter asynchronous service cleanup
                // on the pool, rather than letting a nested await capture this UI context.
                Task.Run(async () => await _lanMusicSharing.Value.DisposeAsync().ConfigureAwait(false))
                    .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            _logger.Log(
                AppLogLevel.Warning,
                "lan.player",
                "shutdown.failed",
                exception: exception);
        }

        try
        {
            // Do not instantiate an unused optional backend merely to dispose it.
            if (_platformBackend.IsValueCreated)
            {
                Task.Run(async () => await _platformBackend.Value.DisposeAsync().ConfigureAwait(false))
                    .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            _logger.Log(
                AppLogLevel.Warning,
                "platform.host",
                "shutdown.failed",
                exception: exception);
        }
        finally
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

            _logger.Log(
                AppLogLevel.Information,
                "application",
                "lifecycle.exiting",
                new Dictionary<string, object?>
                {
                    [AppLogFieldNames.DroppedCount] = _logger.DroppedEntryCount
                });

            try
            {
                _logger.StopAsync(LoggerShutdownBudget).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Diagnostics must never prevent the WPF application from completing shutdown.
            }

            base.OnExit(e);
            _singleInstance?.Dispose();
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Log(
            AppLogLevel.Critical,
            "application.dispatcher",
            "unhandled-exception",
            exception: e.Exception);
        FlushFatalLog();
        // Deliberately do not set e.Handled. Logging must not hide an unknown UI-thread failure.
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        _logger.Log(
            AppLogLevel.Critical,
            "application.runtime",
            "unhandled-exception",
            new Dictionary<string, object?>
            {
                [AppLogFieldNames.Status] = e.IsTerminating ? "terminating" : "continuing"
            },
            e.ExceptionObject as Exception);
        FlushFatalLog();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.Log(
            AppLogLevel.Error,
            "application.tasks",
            "unobserved-exception",
            exception: e.Exception);
        // Do not call SetObserved: this hook records the failure without changing runtime semantics.
    }

    private void FlushFatalLog()
    {
        try
        {
            _logger.FlushAsync(FatalLogFlushBudget).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // A bounded best-effort flush must never replace or hide the original fatal exception.
        }
    }
}
