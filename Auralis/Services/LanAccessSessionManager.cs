using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Auralis.Services;

internal enum LanPairingResult
{
    Success,
    Invalid,
    Expired,
    RateLimited,
    CapacityReached
}

internal sealed class LanAccessSessionManager
{
    internal static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private const int MaximumFailuresPerWindow = 6;
    private const int MaximumSessions = 24;

    private readonly object _pairingLock = new();
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FailureState> _failures = new(StringComparer.Ordinal);
    private string _accessToken = string.Empty;
    private DateTimeOffset _pairingExpiresAt;

    internal LanAccessSessionManager()
    {
        RotatePairingToken();
    }

    internal string AccessToken
    {
        get
        {
            lock (_pairingLock)
            {
                return _accessToken;
            }
        }
    }

    internal DateTimeOffset PairingExpiresAt
    {
        get
        {
            lock (_pairingLock)
            {
                return _pairingExpiresAt;
            }
        }
    }

    internal int ActiveSessionCount
    {
        get
        {
            PruneExpiredSessions();
            return _sessions.Count;
        }
    }

    internal void RotatePairingToken()
    {
        lock (_pairingLock)
        {
            _accessToken = CreateToken();
            _pairingExpiresAt = DateTimeOffset.UtcNow.Add(PairingLifetime);
        }
    }

    internal LanPairingResult TryExchange(string? suppliedToken, IPAddress? remoteAddress, out string? sessionId)
    {
        sessionId = null;
        var sessionPeer = NormalizeSessionPeer(remoteAddress);
        if (sessionPeer is null)
        {
            return LanPairingResult.Invalid;
        }

        var rateLimitPeer = NormalizeRateLimitPeer(remoteAddress);
        if (IsRateLimited(rateLimitPeer))
        {
            return LanPairingResult.RateLimited;
        }

        lock (_pairingLock)
        {
            if (DateTimeOffset.UtcNow >= _pairingExpiresAt)
            {
                RegisterFailure(rateLimitPeer);
                return LanPairingResult.Expired;
            }

            if (!FixedTimeEquals(_accessToken, suppliedToken))
            {
                RegisterFailure(rateLimitPeer);
                return LanPairingResult.Invalid;
            }

            PruneExpiredSessions();
            if (_sessions.Count >= MaximumSessions)
            {
                return LanPairingResult.CapacityReached;
            }

            sessionId = CreateToken();
            _sessions[sessionId] = new SessionState(
                DateTimeOffset.UtcNow.Add(SessionLifetime),
                sessionPeer);
            _failures.TryRemove(rateLimitPeer, out _);

            // Pairing links are single-use. Existing authenticated browser sessions are preserved.
            _accessToken = CreateToken();
            _pairingExpiresAt = DateTimeOffset.UtcNow.Add(PairingLifetime);
            return LanPairingResult.Success;
        }
    }

    internal bool IsAuthorized(string? sessionId, IPAddress? remoteAddress)
    {
        var peer = NormalizeSessionPeer(remoteAddress);
        if (peer is null ||
            string.IsNullOrWhiteSpace(sessionId) ||
            !_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        return string.Equals(session.Peer, peer, StringComparison.Ordinal);
    }

    internal void RevokeAll()
    {
        _sessions.Clear();
        _failures.Clear();
        RotatePairingToken();
    }

    private static string CreateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool FixedTimeEquals(string expected, string? supplied)
    {
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private bool IsRateLimited(string peerKey)
    {
        if (!_failures.TryGetValue(peerKey, out var failure))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - failure.WindowStartedAt >= FailureWindow)
        {
            _failures.TryRemove(peerKey, out _);
            return false;
        }

        return failure.Count >= MaximumFailuresPerWindow;
    }

    private void RegisterFailure(string peerKey)
    {
        var now = DateTimeOffset.UtcNow;
        if (_failures.Count >= 256)
        {
            foreach (var (key, failure) in _failures)
            {
                if (now - failure.WindowStartedAt >= FailureWindow)
                {
                    _failures.TryRemove(key, out _);
                }
            }

            if (_failures.Count >= 256 && !_failures.ContainsKey(peerKey))
            {
                return;
            }
        }

        _failures.AddOrUpdate(
            peerKey,
            _ => new FailureState(now, 1),
            (_, current) => now - current.WindowStartedAt >= FailureWindow
                ? new FailureState(now, 1)
                : current with { Count = current.Count + 1 });
    }

    private void PruneExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, session) in _sessions)
        {
            if (session.ExpiresAt <= now)
            {
                _sessions.TryRemove(key, out _);
            }
        }
    }

    private static string? NormalizeSessionPeer(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static string NormalizeRateLimitPeer(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            Array.Clear(bytes, 8, bytes.Length - 8);
            return new IPAddress(bytes) + "/64";
        }

        return address.ToString();
    }

    private sealed record FailureState(DateTimeOffset WindowStartedAt, int Count);
    private sealed record SessionState(DateTimeOffset ExpiresAt, string Peer);
}
