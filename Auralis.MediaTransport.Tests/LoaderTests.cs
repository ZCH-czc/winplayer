using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Auralis.MediaTransport;
using Auralis.MediaTransport.Host;

internal static class LoaderTests
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("NOT TESTED transport loader: Windows file leases required."); return; }
        var root = Path.Combine(Path.GetTempPath(), "Auralis-transport-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(LoaderTests).Assembly.Location);
            start.ArgumentList.Add("--loader-worker"); start.ArgumentList.Add(root);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
            Console.Write(await output);
            if (child.ExitCode != 0) throw new Exception("Transport loader child failed: " + await error);
        }
        finally { Directory.Delete(root, recursive: true); }
        // Child exit, not ALC.Unload, is what guarantees mapped fixture DLLs can be removed.
    }

    internal static async Task RunWorkerAsync(string root)
    {
        var full = Path.GetFullPath(root);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
            !Path.GetFileName(full).StartsWith("Auralis-transport-loader-", StringComparison.Ordinal) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(full).Any())
            throw new InvalidOperationException("Worker requires empty synthetic temporary directory.");
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        string Events() => (string?)AppContext.GetData("Auralis.Transport.Test.Events") ?? "";
        void Clear() => AppContext.SetData("Auralis.Transport.Test.Events", "");
        var package = Path.Combine(root, "package"); Directory.CreateDirectory(package);
        var file = Path.Combine(package, "Auralis.MediaTransport.Fixture.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixture-payload", Path.GetFileName(file)), file);
        JsonObject Row(string path) => new() { ["path"] = Path.GetFileName(path), ["length"] = new FileInfo(path).Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
        var json = new JsonObject
        {
            ["schemaVersion"] = 1, ["kind"] = "mediaTransport", ["id"] = "fixture.transport", ["displayName"] = "Synthetic",
            ["version"] = "1.0.0", ["contractApiVersion"] = 1, ["minimumHostVersion"] = "0.1.0", ["runtimeIdentifier"] = "win-x64",
            ["entryAssembly"] = Path.GetFileName(file), ["entryType"] = "SyntheticTransport.Factory",
            ["capabilities"] = new JsonArray("authorizedHttp", "completeBuffering", "prefetch", "independentResources", "sharedBudget", "sharedRequests"),
            ["files"] = new JsonArray(Row(file))
        };
        var manifest = Path.Combine(package, MediaTransportComponentManifest.FileName);
        void Write() => File.WriteAllText(manifest, json.ToJsonString());
        MediaTransportPackageSnapshot Snapshot() => MediaTransportComponentCatalog.Discover(package, MediaTransportCapabilities.Full).Single().Package!;
        MediaTransportRegistration Registration(bool enabled = true)
        {
            var snapshot = Snapshot();
            return MediaTransportPackageLoader.CreateRegistration(snapshot, new(snapshot.Manifest.Descriptor.Id, snapshot.ManifestSha256), enabled);
        }
        var defaultRegistration = new MediaTransportRegistration(HttpMediaTransportFactory.Metadata, true, () => new HttpMediaTransportFactory());
        var context = new MediaTransportContext(Path.Combine(root, "cache"), new MediaTransferBudget());
        Write(); var first = Snapshot(); Clear();
        foreach (var grant in new[] { new MediaTransportPackageApproval("wrong", first.ManifestSha256),
            new MediaTransportPackageApproval(first.Manifest.Descriptor.Id, new string('0', 64)) })
        {
            try { MediaTransportPackageLoader.CreateRegistration(first, grant, true); throw new Exception("Grant accepted"); }
            catch (MediaTransportLoadException e) { Check(e.Issue == MediaTransportLoadIssue.ApprovalMismatch && e.InnerException is null, "exact ID/digest grant"); }
        }
        var registration = Registration();
        var registry = new MediaTransportRegistry([registration], defaultRegistration);
        registry.Inspect(MediaTransportCapabilities.Full);
        Check(Events() == "", "registration/inspection never activate DLL");
        await using (var unused = registry.CreateDeferred(registration.Descriptor.Id, context, MediaTransportCapabilities.Full)) { }
        Check(Events() == "", "unused lazy session does not load DLL");
        var selected = await registry.CreateAsync(registration.Descriptor.Id, context, MediaTransportCapabilities.Full);
        Check(!selected.UsedFallback && Events() == "activate;create;", "factory loaded with shared contract");
        await using (var resource = await selected.Session.PrepareAsync(new(new Uri("https://fixture.invalid/no-network"), null, null, "test"), null, default))
            Check(resource.Source.LocalPath == file, "executing DLL is from exact approved package");
        try { using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); throw new Exception("DLL writable"); }
        catch (IOException) { checks++; }
        try { Write(); throw new Exception("Manifest writable"); } catch (IOException) { checks++; }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppContext.SetData("Auralis.Transport.Test.CloseEntered", entered);
        AppContext.SetData("Auralis.Transport.Test.CloseGate", release.Task);
        var close = selected.Session.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!close.IsCompleted && Events().EndsWith("sessionDispose;factoryDispose;"), "session closes before asynchronous factory drain");
        try { Write(); throw new Exception("Lease released before factory close"); } catch (IOException) { checks++; }
        release.TrySetResult(); await close.WaitAsync(TimeSpan.FromSeconds(5)); await selected.Session.DisposeAsync();
        Check(Events().EndsWith("sessionDispose;factoryDispose;factoryClosed;"), "factory drain once");
        Write(); Check(Snapshot().ManifestSha256 == first.ManifestSha256, "manifest lease released on close");
        AppContext.SetData("Auralis.Transport.Test.CloseGate", null); AppContext.SetData("Auralis.Transport.Test.CloseEntered", null);
        var stale = Registration(); json["displayName"] = "Changed"; Write(); Clear();
        try { stale.Activate(); throw new Exception("Stale package loaded"); }
        catch (MediaTransportLoadException e) { Check(e.Issue == MediaTransportLoadIssue.IntegrityFailed && Events() == "" && !e.ToString().Contains(root), "changed snapshot rejected before code"); }
        json["displayName"] = "Synthetic";
        foreach (var type in new[] { "Missing", "NotAFactory", "PrivateFactory", "BrokenConstructor", "WrongDescriptor" })
        {
            json["entryType"] = "SyntheticTransport." + type; Write();
            var candidate = Registration();
            var recovering = new MediaTransportRegistry([candidate], defaultRegistration);
            var fallback = await recovering.CreateAsync(candidate.Descriptor.Id, context, MediaTransportCapabilities.Full);
            Check(fallback.UsedFallback && fallback.ComponentId == defaultRegistration.Descriptor.Id, "invalid loaded factory creation falls back: " + type);
            await fallback.Session.DisposeAsync(); Write(); // Rejected candidate must not retain ordinary manifest lease.
        }
        json["entryType"] = "SyntheticTransport.Factory";
        Write(); Clear();
        var disabled = Registration(false);
        var disabledSelection = await new MediaTransportRegistry([disabled], defaultRegistration).CreateAsync(disabled.Descriptor.Id, context, MediaTransportCapabilities.Full);
        Check(disabledSelection.UsedFallback && Events() == "", "disabled package not executed");
        await disabledSelection.Session.DisposeAsync();
        var contract = Path.Combine(package, "Auralis.MediaTransport.Abstractions.dll");
        File.Copy(typeof(IMediaTransportSession).Assembly.Location, contract);
        json["files"]!.AsArray().Add(Row(contract)); Write(); Clear();
        try { Registration().Activate(); throw new Exception("Private contract loaded"); }
        catch (MediaTransportLoadException e) { Check(e.Issue == MediaTransportLoadIssue.DependencyInvalid && Events() == "", "shared contract cannot be replaced"); }
        File.Delete(contract); json["files"]!.AsArray().RemoveAt(1); Write();
        var hostCopy = Path.Combine(package, "Auralis.MediaTransport.Host.dll");
        File.Copy(typeof(MediaTransportRegistry).Assembly.Location, hostCopy);
        json["files"]!.AsArray().Add(Row(hostCopy)); Write(); Clear();
        try { Registration().Activate(); throw new Exception("Private host loaded"); }
        catch (MediaTransportLoadException e) { Check(e.Issue == MediaTransportLoadIssue.DependencyInvalid && Events() == "", "host implementation dependency rejected before factory"); }
        File.Delete(hostCopy); json["files"]!.AsArray().RemoveAt(1); Write();
        json["contractApiVersion"] = 99; Write(); Clear();
        try { Registration().Activate(); throw new Exception("Incompatible direct activation"); }
        catch (MediaTransportLoadException e) { Check(e.Issue == MediaTransportLoadIssue.IntegrityFailed && Events() == "", "direct activation also enforces API compatibility"); }
        json["contractApiVersion"] = 1; Write();
        AppContext.SetData("Auralis.Transport.Test.CloseThrows", true);
        var failureSession = await new MediaTransportRegistry([Registration()], defaultRegistration).CreateAsync("fixture.transport", context, MediaTransportCapabilities.Full);
        try { await failureSession.Session.DisposeAsync(); throw new Exception("Missing disposal fault"); }
        catch (MediaTransportComponentException e) { Check(e.Issues.Contains(MediaTransportIssue.FactoryDisposeFailed) && !e.ToString().Contains("private fixture error"), "factory disposal fault sanitized"); }
        Write(); Check(Snapshot().ManifestSha256 == first.ManifestSha256, "failed dispose still releases manifest lease");
        AppContext.SetData("Auralis.Transport.Test.CloseThrows", null);
        await InstalledLoaderTests.RunAsync(root, package);
        // Exercise the real separately loaded HTTP factory, not just a synthetic interface fixture.
        Check(typeof(HttpMediaTransportFactory).GetConstructor(Type.EmptyTypes) is not null, "bundled factory has actual CLR parameterless constructor");
        var httpPackage = Path.Combine(root, "http-package"); Directory.CreateDirectory(httpPackage);
        var httpDll = Path.Combine(httpPackage, "Auralis.MediaTransport.Http.dll");
        File.Copy(typeof(HttpMediaTransportFactory).Assembly.Location, httpDll);
        var httpManifest = json.DeepClone();
        httpManifest["id"] = HttpMediaTransportFactory.Metadata.Id;
        httpManifest["displayName"] = HttpMediaTransportFactory.Metadata.DisplayName;
        httpManifest["version"] = HttpMediaTransportFactory.Metadata.ComponentVersion.ToString(3);
        httpManifest["entryAssembly"] = Path.GetFileName(httpDll);
        httpManifest["entryType"] = typeof(HttpMediaTransportFactory).FullName;
        httpManifest["files"] = new JsonArray(Row(httpDll));
        File.WriteAllText(Path.Combine(httpPackage, MediaTransportComponentManifest.FileName), httpManifest.ToJsonString());
        var httpInstall = Path.Combine(root, "installed-http");
        var httpStore = new MediaTransportInstallationStore(httpInstall, MediaTransportCapabilities.Full);
        var httpArchive = Path.Combine(root, "http.auralis-transport.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(httpPackage, httpArchive);
        var httpPreview = await httpStore.PreviewArchiveAsync(httpArchive);
        await httpStore.ImportApprovedAsync(httpPreview, new(httpPreview.Descriptor.Id, httpPreview.ManifestSha256));
        await httpPreview.DisposeAsync();
        await httpStore.SetEnabledAsync(httpPreview.Descriptor.Id, true);
        await httpStore.SelectAsync(httpPreview.Descriptor.Id);
        var httpBoot = new MediaTransportInstallationStore(httpInstall, MediaTransportCapabilities.Full).ReadStartupPlan();
        var httpRegistration = httpBoot.Registrations.Single();
        var budget = new ObservedBudget();
        var httpContext = new MediaTransportContext(Path.Combine(root, "http-cache"), budget);
        // Same-ID default deliberately throws: success must come from the approved external DLL.
        var httpRegistry = new MediaTransportRegistry([httpRegistration], new(httpRegistration.Descriptor, true,
            () => throw new Exception("Bundled fallback must not execute in this case")));
        var httpSelection = await httpRegistry.CreateAsync(httpRegistration.Descriptor.Id, httpContext, MediaTransportCapabilities.Full);
        Check(!httpSelection.UsedFallback && !Directory.Exists(httpContext.CacheDirectory), "external HTTP factory creates without network/cache writes");
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            var requestBytes = new List<byte>(); var buffer = new byte[1];
            while (requestBytes.Count < 8192)
            {
                if (await stream.ReadAsync(buffer, timeout.Token) == 0) throw new Exception("Incomplete synthetic request");
                requestBytes.Add(buffer[0]);
                if (requestBytes.Count >= 4 && requestBytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            }
            var requestText = System.Text.Encoding.ASCII.GetString(requestBytes.ToArray());
            Check(requestText.StartsWith("GET /fixture HTTP/", StringComparison.Ordinal), "loaded HTTP component reaches loopback fixture only");
            var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, timeout.Token); await stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, timeout.Token);
        });
        string? cached = null;
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            await using var resource = await httpSelection.Session.PrepareAsync(new(new Uri($"http://127.0.0.1:{port}/fixture"),
                null, "audio/mpeg", "fixture", useHostTransport: true), null, timeout.Token);
            cached = resource.Source.LocalPath;
            Check(File.ReadAllBytes(cached).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "real loaded transport downloads complete fixture");
            Check(budget.Calls == 1, "loaded component consumes host-owned budget interface");
            await server;
        }
        finally
        {
            timeout.Cancel(); listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
            await httpSelection.Session.DisposeAsync();
        }
        Check(cached is not null && !File.Exists(cached), "loaded transport closes its prepared resource");
        Console.WriteLine($"PASS transport loader: {checks} assertions; trusted real DLL, inert grant, shared ABI, file leases, async close, creation fallback and real HTTP loopback. No accounts/external network.");
    }
    private sealed class ObservedBudget : IMediaTransferBudget
    {
        private readonly MediaTransferBudget _inner = new();
        public int Calls;
        public IMediaTransferReservation Reserve(bool prefetch) { Interlocked.Increment(ref Calls); return _inner.Reserve(prefetch); }
    }
}
