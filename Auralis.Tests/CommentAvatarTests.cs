using Auralis.Services;
using Auralis.Platform.Host;

internal static class CommentAvatarTests
{
    internal static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new CommentAvatarRegistry(() => now, 2);
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Comment avatar boundary regression."); }
        Check(PlatformCommentArtworkPolicy.TryCreate(["avatars.example.test", "covers.example.test", "profiles.example.test"], out var policy));
        string? Register(Uri uri) => registry.Register("fixture", uri, policy, static () => true);
        foreach (var input in new[] { "http://i0.avatars.example.test/a", "https://127.0.0.1/a", "https://avatars.example.test.evil.invalid/a", "https://evilavatars.example.test/a", "https://user@i0.avatars.example.test/a", "https://i0.avatars.example.test:444/a", "file:///a" })
            Check(Register(new Uri(input)) is null);
        var original = new Uri("https://i0.avatars.example.test/faces/a.jpg?private=value");
        var proxy = Register(original)!;
        Check(proxy.StartsWith("https://platform-art.auralis.local/avatar-") && !proxy.Contains("private"));
        var handle = new Uri(proxy).AbsolutePath.TrimStart('/');
        Check(registry.TryResolve(handle, out var actual, out var allowed) && actual == original);
        Check(allowed!(new Uri("https://i1.avatars.example.test/redirect")) && !allowed(new Uri("https://foreign.example.test/other-provider")));
        Check(Register(original) == proxy);
        now += TimeSpan.FromSeconds(1);
        Register(new Uri("https://p1.covers.example.test/b.jpg"));
        now += TimeSpan.FromSeconds(1);
        Register(new Uri("https://profiles.example.test/c.jpg"));
        Check(!registry.TryResolve(handle, out _, out _));
        var fresh = Register(original)!;
        now += TimeSpan.FromHours(2);
        Check(!registry.TryResolve(new Uri(fresh).AbsolutePath.TrimStart('/'), out _, out _));
        var active = true;
        var owned = registry.Register("first", original, policy, () => active)!;
        var other = registry.Register("second", original, policy, static () => true)!;
        Check(owned != other); // No cross-provider deduplication, even for the same URI.
        var ownedHandle = new Uri(owned).AbsolutePath.TrimStart('/');
        Check(registry.TryResolve(ownedHandle, out _, out var grant));
        active = false;
        Check(!grant!(original) && !registry.TryResolve(ownedHandle, out _, out _));
        Check(registry.TryResolve(new Uri(other).AbsolutePath.TrimStart('/'), out _, out _));
        Check(registry.Register("first", original, policy, () => active) is null);
        Check(registry.Register("bad", original, policy, () => throw new Exception("private")) is null);
        Check(registry.Register("none", original, PlatformCommentArtworkPolicy.DenyAll, static () => true) is null);
    }
}
