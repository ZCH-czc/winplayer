using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

internal static class PlatformPluginInvocation
{
    internal static Task<PlatformResult<T>> InvokeAsync<T>(
        Func<CancellationToken, ValueTask<PlatformResult<T>>> operation,
        TimeSpan timeout,
        CancellationToken callerToken,
        Action<Exception> reportException,
        string failureMessage) =>
        InvokeCoreAsync(
            async cancellationToken =>
                await operation(cancellationToken).ConfigureAwait(false),
            timeout,
            callerToken,
            reportException,
            failureMessage);

    internal static Task<PlatformResult<T>> InvokeAsync<T>(
        Func<CancellationToken, Task<PlatformResult<T>>> operation,
        TimeSpan timeout,
        CancellationToken callerToken,
        Action<Exception> reportException,
        string failureMessage) =>
        InvokeCoreAsync(operation, timeout, callerToken, reportException, failureMessage);

    private static async Task<PlatformResult<T>> InvokeCoreAsync<T>(
        Func<CancellationToken, Task<PlatformResult<T>>> operation,
        TimeSpan timeout,
        CancellationToken callerToken,
        Action<Exception> reportException,
        string failureMessage)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            timeoutSource.Token);

        try
        {
            var result = await operation(linkedSource.Token)
                .WaitAsync(linkedSource.Token)
                .ConfigureAwait(false);

            if (!result.IsSuccess && result.Error is null)
            {
                return PlatformResult<T>.Failure(
                    PlatformErrorCode.InvalidResponse,
                    "The plugin returned an invalid empty failure result.");
            }

            return result;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            return PlatformResult<T>.Failure(
                PlatformErrorCode.Cancelled,
                "The platform operation was cancelled by the caller.");
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return PlatformResult<T>.Failure(
                PlatformErrorCode.Timeout,
                "The platform operation exceeded the host timeout.",
                isTransient: true);
        }
        catch (Exception exception)
        {
            reportException(exception);
            return PlatformResult<T>.Failure(
                PlatformErrorCode.Unknown,
                failureMessage,
                isTransient: false);
        }
    }
}
