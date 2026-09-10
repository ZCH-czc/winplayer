using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    PrintUsage(Console.Error);
    return 2;
}

if (args[0].ToLowerInvariant() is "help" or "-h" or "--help")
{
    PrintUsage(Console.Out);
    return 0;
}

if (string.Equals(args[0], "media-session", StringComparison.OrdinalIgnoreCase))
{
    var expectedTitle = args.Length > 1 ? string.Join(' ', args.Skip(1)) : null;
    try
    {
        return await MediaSessionDiagnostic.RunAsync(expectedTitle);
    }
    catch (Exception exception)
    {
        return ReportFailure("Auralis media-session diagnostic failed", exception, 5);
    }
}

if (string.Equals(args[0], "taskbar-widget", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        return TaskbarWidgetDiagnostic.Run(args.Skip(1).ToArray());
    }
    catch (Exception exception)
    {
        return ReportFailure("Auralis taskbar-widget diagnostic failed", exception, 5);
    }
}

if (!IsValidBrowserCommand(args))
{
    Console.Error.WriteLine("Invalid command or missing argument. Use --help for usage.");
    return 2;
}

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    using var targets = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:9222/json"));
    var target = targets.RootElement.EnumerateArray().FirstOrDefault(item =>
        item.TryGetProperty("type", out var type) && type.GetString() == "page" &&
        item.TryGetProperty("url", out var url) &&
        url.GetString()?.Contains("app.auralis.local", StringComparison.OrdinalIgnoreCase) == true);

    if (target.ValueKind == JsonValueKind.Undefined)
    {
        Console.Error.WriteLine("Auralis WebView2 target was not found on localhost:9222.");
        return 3;
    }

    var socketUrl = target.GetProperty("webSocketDebuggerUrl").GetString();
    if (string.IsNullOrWhiteSpace(socketUrl))
    {
        Console.Error.WriteLine("Auralis WebView2 target did not provide a debugger WebSocket URL.");
        return 3;
    }

    using var client = new CdpClient();
    await client.ConnectAsync(new Uri(socketUrl));

    switch (args[0].ToLowerInvariant())
    {
        case "snapshot":
            await PrintEvaluationAsync(client, "JSON.stringify({title:document.title,url:location.href,theme:document.documentElement.dataset.theme,density:document.documentElement.dataset.density,body:document.body.innerText,buttons:Array.from(document.querySelectorAll('button')).filter(x=>getComputedStyle(x).visibility!=='hidden').map(x=>({id:x.id,label:x.getAttribute('aria-label')||x.innerText.trim(),className:x.className})),inputs:Array.from(document.querySelectorAll('input')).map(x=>({id:x.id,type:x.type,value:x.value,label:x.getAttribute('aria-label')}))})");
            break;

        case "click":
            await ClickAsync(client, args[1]);
            Console.WriteLine("clicked");
            break;

        case "text":
            await ClickAsync(client, args[1]);
            await client.SendAsync("Input.insertText", new { text = args[2] });
            Console.WriteLine("typed");
            break;

        case "screenshot":
            var capture = await client.SendAsync("Page.captureScreenshot", new { format = "png", fromSurface = true });
            var base64 = capture.GetProperty("result").GetProperty("data").GetString()
                ?? throw new InvalidOperationException("CDP did not return screenshot data.");
            await File.WriteAllBytesAsync(args[1], Convert.FromBase64String(base64));
            Console.WriteLine(Path.GetFullPath(args[1]));
            break;

        case "eval":
            await PrintEvaluationAsync(client, string.Join(' ', args.Skip(1)));
            break;
    }

    return 0;
}
catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or WebSocketException)
{
    return ReportFailure("Auralis WebView2 diagnostic endpoint is unavailable", exception, 4);
}
catch (Exception exception)
{
    // NativeTests is an automation helper. Operational failures must be deterministic instead of
    // reaching the .NET unhandled-exception UI and interrupting a desktop test run.
    return ReportFailure("Auralis native diagnostic failed", exception, 5);
}

static void PrintUsage(TextWriter writer)
{
    writer.WriteLine("Usage: media-session [expectedTitle] | taskbar-widget [--pid <id>] [--hwnd <handle>] [--screenshot <png-path>] [--screen-pixels] [--flyout] [--wait-visible-ms <0..30000>] [--settle-ms <0..5000>] | snapshot | click <selector> | text <selector> <value> | screenshot <path> | eval <expression>");
    writer.WriteLine("media-session prints Auralis metadata, playback/timeline state, and thumbnail presence as read-only JSON.");
    writer.WriteLine("taskbar-widget inspects the Auralis-owned no-activate window aligned over the Windows taskbar; --flyout selects the music flyout, while --screen-pixels preserves composed background pixels.");
    writer.WriteLine("help, -h, and --help print this text without connecting to WebView2.");
}

static bool IsValidBrowserCommand(IReadOnlyList<string> arguments) =>
    arguments[0].ToLowerInvariant() switch
    {
        "snapshot" => true,
        "click" => arguments.Count >= 2,
        "text" => arguments.Count >= 3,
        "screenshot" => arguments.Count >= 2,
        "eval" => arguments.Count >= 2,
        _ => false
    };

static int ReportFailure(string prefix, Exception exception, int exitCode)
{
    var detail = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
    if (detail.Length > 240)
    {
        detail = detail[..240] + "…";
    }

    Console.Error.WriteLine(string.IsNullOrWhiteSpace(detail) ? prefix + "." : $"{prefix}: {detail}");
    return exitCode;
}

static async Task ClickAsync(CdpClient client, string selector)
{
    var expression = BuildPointExpression(selector);
    var evaluation = await client.EvaluateAsync(expression, returnByValue: true);
    if (evaluation.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
    {
        var hoverExpression = $$"""
            (() => {
              const element = document.querySelector({{JsonSerializer.Serialize(selector)}});
              const hoverTarget = element?.closest('.track-row, .media-card, .artist-card');
              if (!hoverTarget) return null;
              const rect = hoverTarget.getBoundingClientRect();
              return rect.width && rect.height ? { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 } : null;
            })()
            """;
        var hoverPoint = await client.EvaluateAsync(hoverExpression, returnByValue: true);
        if (hoverPoint.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            await client.SendAsync("Input.dispatchMouseEvent", new
            {
                type = "mouseMoved",
                x = hoverPoint.GetProperty("x").GetDouble(),
                y = hoverPoint.GetProperty("y").GetDouble()
            });
            await Task.Delay(180);
            evaluation = await client.EvaluateAsync(expression, returnByValue: true);
        }
    }

    if (evaluation.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
    {
        throw new InvalidOperationException($"Visible element was not found: {selector}");
    }

    var x = evaluation.GetProperty("x").GetDouble();
    var y = evaluation.GetProperty("y").GetDouble();
    await client.SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
    await client.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
    await client.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
    await Task.Delay(300);
}

static string BuildPointExpression(string selector) => $$"""
        (() => {
          const element = document.querySelector({{JsonSerializer.Serialize(selector)}});
          if (!element) return null;
          const rect = element.getBoundingClientRect();
          const style = getComputedStyle(element);
          if (!rect.width || !rect.height || style.visibility === 'hidden' || style.display === 'none') return null;
          return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
        })()
        """;

static async Task PrintEvaluationAsync(CdpClient client, string expression)
{
    var value = await client.EvaluateAsync(expression, returnByValue: true);
    Console.WriteLine(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
}

sealed class CdpClient : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private int _nextId;

    public Task ConnectAsync(Uri uri) => _socket.ConnectAsync(uri, CancellationToken.None);

    public async Task<JsonElement> EvaluateAsync(string expression, bool returnByValue)
    {
        var response = await SendAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue,
            awaitPromise = true,
            userGesture = true
        });
        var envelope = response.GetProperty("result");
        if (envelope.TryGetProperty("exceptionDetails", out var details))
        {
            throw new InvalidOperationException(details.GetRawText());
        }
        var result = envelope.GetProperty("result");
        return result.TryGetProperty("value", out var value) ? value.Clone() : default;
    }

    public async Task<JsonElement> SendAsync(string method, object parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await _socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);

        while (true)
        {
            var message = await ReceiveMessageAsync();
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
            {
                continue;
            }
            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(error.GetRawText());
            }
            return root.Clone();
        }
    }

    private async Task<byte[]> ReceiveMessageAsync()
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("CDP connection closed unexpectedly.");
            }
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return stream.ToArray();
    }

    public void Dispose()
    {
        _socket.Abort();
        _socket.Dispose();
    }
}
