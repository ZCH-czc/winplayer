using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>Revocation is serialized with writes so a late authentication cannot restore a disabled session.</summary>
public sealed class RevocablePlatformCredentialStore(IPlatformCredentialStore inner) : IPlatformCredentialStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _revoked;
    public async Task RevokeAsync(CancellationToken token)
    {
        Interlocked.Exchange(ref _revoked, 1);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        _gate.Release(); // Drain a write that had already started before revocation.
    }
    public async ValueTask<PlatformCredential?> GetAsync(string key, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return Volatile.Read(ref _revoked) != 0 ? null : await inner.GetAsync(key, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> secret, DateTimeOffset? expiry, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _revoked) != 0) throw new InvalidOperationException("Plugin credentials revoked until restart.");
            await inner.SetAsync(key, secret, expiry, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public ValueTask DeleteAsync(string key, CancellationToken token) => inner.DeleteAsync(key, token);
}
