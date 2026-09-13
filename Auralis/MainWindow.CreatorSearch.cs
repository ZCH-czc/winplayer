using System.Text.Json;

namespace Auralis;

public partial class MainWindow
{
    private async Task SearchPlatformCreatorsAsync(JsonElement root)
    {
        var providerId = JsonText(root, "providerId");
        var query = JsonText(root, "query");
        var pageHandle = JsonText(root, "pageHandle");
        if (providerId is not { Length: > 0 and <= 128 } || string.IsNullOrWhiteSpace(query) || query.Length > 512 || pageHandle?.Length > 128 ||
            !root.TryGetProperty("requestId", out var id) || !id.TryGetInt64(out var requestId) || requestId is <= 0 or > 9007199254740991) return;
        const string kind = "creator-search";
        CancelPlatformExtras(kind);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cancellation.Token;
        _extrasRequests[kind] = cancellation;
        object items = Array.Empty<object>(); object? error = null; string? nextPageHandle = null; long? totalCount = null;
        try
        {
            var result = await OnlinePlatforms.SearchCreatorsAsync(providerId, query, pageHandle, token);
            token.ThrowIfCancellationRequested();
            if (result.IsSuccess) { items = result.Value.Items; nextPageHandle = result.Value.NextPageHandle; totalCount = result.Value.TotalCount; }
            else error = ToPlatformError(result.Error);
        }
        catch (OperationCanceledException) { error = new { message = "读取超时，请重试。" }; }
        finally
        {
            if (_extrasRequests.TryGetValue(kind, out var current) && ReferenceEquals(current, cancellation))
            {
                _extrasRequests.Remove(kind);
                await ExecuteScriptAsync($"window.Auralis?.setCreatorSearchResult({JsonSerializer.Serialize(new { providerId, query, requestId, pageHandle, items, nextPageHandle, totalCount, error }, WebJsonOptions)})");
            }
        }
    }
}
