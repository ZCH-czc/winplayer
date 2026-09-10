using Auralis.Services;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Diagnostics;

internal static class SingleInstanceTests
{
    internal static bool RunChild(string[] args)
    {
        if (args.Length != 2 || args[0] != "--single-instance-child") return false;
        if (!args[1].StartsWith("Auralis.Tests.Instance.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1]["Auralis.Tests.Instance.".Length..], "N", out _))
            throw new ArgumentException("Only a random test namespace is accepted.");
        using var secondary = new SingleInstanceCoordinator(args[1]);
        Check(!secondary.IsPrimary, "child process sees the existing host");
        Check(secondary.ForwardAsync(new(null, "child-build")).GetAwaiter().GetResult(), "real process forwards successfully");
        return true;
    }

    internal static void Run()
    {
        var name = "Auralis.Tests.Instance." + Guid.NewGuid().ToString("N");
        var requests = new ConcurrentQueue<SingleInstanceCoordinator.Activation>();
        using (var primary = new SingleInstanceCoordinator(name))
        {
            Check(primary.IsPrimary, "first host owns mutex");
            var client = Task.Run(() =>
            {
                using var secondary = new SingleInstanceCoordinator(name);
                Check(!secondary.IsPrimary, "second thread cannot become another host");
                Check(secondary.ForwardAsync(new("C:\\test\\a song.flac", "0.16.6 test")).GetAwaiter().GetResult(), "startup race forwards activation");
                Check(!secondary.ForwardAsync(new(null, new string('x', 17000))).GetAwaiter().GetResult(), "reject oversized sender");
            });
            Thread.Sleep(150);
            primary.Listen(requests.Enqueue);
            client.GetAwaiter().GetResult();
            Check(requests.TryDequeue(out var received) && received.AudioFile == "C:\\test\\a song.flac", "file activation survives IPC without interpretation");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(SingleInstanceTests).Assembly.Location);
            start.ArgumentList.Add("--single-instance-child"); start.ArgumentList.Add(name);
            using (var child = Process.Start(start)!)
            {
                Check(child.WaitForExit(12000) && child.ExitCode == 0, "real secondary exits without a second host");
            }
            Check(requests.TryDequeue(out var childRequest) && childRequest.Build == "child-build", "real child build activation received");
            using (var malformed = new NamedPipeClientStream(".", name, PipeDirection.InOut))
            {
                malformed.Connect(2000);
                malformed.Write(BitConverter.GetBytes(999999));
            }
            Task.Run(() =>
            {
                using var secondary = new SingleInstanceCoordinator(name);
                Check(secondary.ForwardAsync(new(null, "new-build")).GetAwaiter().GetResult(), "malformed payload does not kill listener");
            }).GetAwaiter().GetResult();
            Check(requests.TryDequeue(out var next) && next.Build == "new-build", "activation-only request delivered");
        }
        using var restarted = new SingleInstanceCoordinator(name);
        Check(restarted.IsPrimary, "normal shutdown releases ownership");
        Console.WriteLine("PASS single instance: ownership, delayed listener, file forwarding, payload limits, recovery, shutdown.");
    }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
}
