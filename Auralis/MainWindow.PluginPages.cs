using System.Text.Json;
using Auralis.Platform.Host;

namespace Auralis;

public partial class MainWindow
{
    // One visible plugin page. Reuses the existing shutdown/navigation cancellation owner.
    private async Task ReadPluginPageAsync(JsonElement root, bool global = false)
    {
        var handle = JsonText(root, "handle");
        var providerId = global ? JsonText(root, "providerId") : null;
        var entryId = JsonText(root, "entryId");
        var navigationHandle = JsonText(root, "navigationHandle");
        if ((global ? handle is not null || !PlatformPageEntry.ValidId(providerId) : handle is not { Length: > 0 and <= 128 }) || !PlatformPageEntry.ValidId(entryId) ||
            (navigationHandle is not null && (navigationHandle.Length > 128 || !navigationHandle.StartsWith("page-"))) ||
            !root.TryGetProperty("requestId", out var id) || !id.TryGetInt64(out var requestId) ||
            requestId is <= 0 or > 9007199254740991L) return;
        Dictionary<string, string>? inputs = null;
        var invalidInputs = false;
        if (root.TryGetProperty("inputValues", out var submitted))
        {
            inputs = new(StringComparer.Ordinal);
            invalidInputs = submitted.ValueKind != JsonValueKind.Object;
            if (!invalidInputs)
                foreach (var field in submitted.EnumerateObject())
                {
                    if (inputs.Count >= 4 || !PlatformPageEntry.ValidId(field.Name) ||
                        field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() is not { Length: <= 256 } value ||
                        !inputs.TryAdd(field.Name, value)) { invalidInputs = true; break; }
                }
        }
        CancelPlatformExtras("plugin-page");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _extrasRequests["plugin-page"] = cancellation;
        object? page = null, error = null;
        try
        {
            if (invalidInputs) throw new ArgumentException("Invalid page query.");
            if (!global) await RestoreSavedHandleAsync(handle!);
            var result = global
                ? await OnlinePlatforms.ReadPluginGlobalPageAsync(providerId!, entryId!, navigationHandle,
                    JsonText(root, "language") ?? "zh-CN", cancellation.Token, inputs)
                : await OnlinePlatforms.ReadPluginPageAsync(handle!, entryId!, navigationHandle,
                    JsonText(root, "language") ?? "zh-CN", cancellation.Token, inputs);
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.IsSuccess) page = result.Value; else error = ToPlatformError(result.Error);
        }
        catch (OperationCanceledException) { error = new { message = "读取超时，请重试。" }; }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or ArgumentException)
        { error = new { message = "无法读取插件页面，请重试。" }; }
        finally
        {
            if (_extrasRequests.TryGetValue("plugin-page", out var current) && ReferenceEquals(current, cancellation))
            {
                _extrasRequests.Remove("plugin-page");
                await ExecuteScriptAsync($"window.Auralis?.setPluginPage({JsonSerializer.Serialize(new { handle, providerId, entryId, requestId, page, error }, WebJsonOptions)})");
            }
        }
    }
}
