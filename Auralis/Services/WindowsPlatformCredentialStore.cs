using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Auralis.Platform.Abstractions;

namespace Auralis.Services;

/// <summary>
/// Stores plugin-scoped account or service credentials in Windows Credential Manager. The WebView never receives
/// these values, and temporary managed/unmanaged copies are cleared after every operation.
/// </summary>
internal sealed class WindowsPlatformCredentialStore : IPlatformCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int HeaderBytes = 9;
    private const int MaximumSecretBytes = 2400;

    private readonly string _pluginId;

    internal WindowsPlatformCredentialStore(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _pluginId = pluginId;
    }

    public ValueTask<PlatformCredential?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = GetTargetName(key);
        if (!CredRead(target, CredTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return ValueTask.FromResult<PlatformCredential?>(null);
            }

            throw new Win32Exception(error, "无法从 Windows 凭据管理器读取在线平台凭据。");
        }

        byte[]? blob = null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize is < HeaderBytes or > HeaderBytes + MaximumSecretBytes ||
                credential.CredentialBlob == nint.Zero)
            {
                return ValueTask.FromResult<PlatformCredential?>(null);
            }

            blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            if (blob[0] != 1)
            {
                return ValueTask.FromResult<PlatformCredential?>(null);
            }

            var expiryMilliseconds = BitConverter.ToInt64(blob, 1);
            DateTimeOffset? expiresAt = expiryMilliseconds < 0
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(expiryMilliseconds);
            var result = new PlatformCredential(blob.AsSpan(HeaderBytes), expiresAt);
            return ValueTask.FromResult<PlatformCredential?>(result);
        }
        finally
        {
            if (blob is not null)
            {
                CryptographicOperations.ZeroMemory(blob);
            }

            CredFree(credentialPointer);
        }
    }

    public ValueTask SetAsync(
        string key,
        ReadOnlyMemory<byte> secret,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (secret.IsEmpty || secret.Length > MaximumSecretBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                $"在线平台凭据必须为 1 至 {MaximumSecretBytes} 字节。");
        }

        var target = GetTargetName(key);
        var blob = new byte[HeaderBytes + secret.Length];
        blob[0] = 1;
        BitConverter.TryWriteBytes(
            blob.AsSpan(1, sizeof(long)),
            expiresAt?.ToUnixTimeMilliseconds() ?? -1);
        secret.Span.CopyTo(blob.AsSpan(HeaderBytes));

        var blobPointer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = CredPersistLocalMachine,
                UserName = "Auralis"
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法将在线平台凭据保存到 Windows 凭据管理器。");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            Marshal.Copy(new byte[HeaderBytes + secret.Length], 0, blobPointer, HeaderBytes + secret.Length);
            Marshal.FreeHGlobal(blobPointer);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredDelete(GetTargetName(key), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "无法从 Windows 凭据管理器删除在线平台凭据。");
            }
        }

        return ValueTask.CompletedTask;
    }

    private string GetTargetName(string key)
    {
        HostManagedHttpClientFactory.ValidateKey(key, nameof(key));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return $"Auralis.Platform/{_pluginId}/{digest}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
