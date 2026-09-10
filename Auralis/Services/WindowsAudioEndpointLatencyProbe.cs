using System.Runtime.InteropServices;

namespace Auralis.Services;

/// <summary>
/// Reads the latency that Windows reports for a short-lived WASAPI shared-mode reference stream.
/// LibVLC owns Auralis' real WASAPI client and does not expose that COM object, so this value is a
/// conservative system-path estimate rather than a measurement of the complete playback pipeline.
/// </summary>
internal static class WindowsAudioEndpointLatencyProbe
{
    private const uint DeviceStateActive = 0x00000001;
    private const uint ClsctxAll = 23;
    private const uint StgmRead = 0;
    private const uint AudioClientStreamFlagsNoPersist = 0x00080000;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private static readonly nint InvalidHandleValue = new(-1);

    private static readonly Guid MmDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid AudioClientInterfaceId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly PropertyKey DeviceFriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);
    private static readonly PropertyKey DeviceContainerIdKey = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        2);
    private static readonly DevicePropertyKey DevpropDeviceContainerId = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        2);

    internal static AudioEndpointLatencyInfo Probe(string? requestedEndpointId, string? fallbackName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return AudioEndpointLatencyInfo.Unavailable("unsupportedPlatform");
        }

        object? enumeratorObject = null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? audioClientObject = null;
        IAudioClient? audioClient = null;
        nint mixFormat = nint.Zero;

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(MmDeviceEnumeratorClassId, throwOnError: true);
            enumeratorObject = Activator.CreateInstance(enumeratorType!);
            enumerator = (IMMDeviceEnumerator)enumeratorObject!;

            var endpointResult = string.IsNullOrWhiteSpace(requestedEndpointId)
                // LibVLC's Windows mmdevice module follows the eConsole role for its default
                // endpoint, so the diagnostics probe must use the same role.
                ? enumerator.GetDefaultAudioEndpoint(AudioDataFlow.Render, AudioRole.Console, out device)
                : enumerator.GetDevice(requestedEndpointId.Trim(), out device);
            if (endpointResult < 0 || device is null)
            {
                return AudioEndpointLatencyInfo.Unavailable("deviceUnavailable");
            }

            _ = device.GetId(out var endpointId);
            var deviceName = ReadStringProperty(device, DeviceFriendlyNameKey);
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                deviceName = string.IsNullOrWhiteSpace(fallbackName) ? endpointId : fallbackName.Trim();
            }

            var containerId = ReadGuidProperty(device, DeviceContainerIdKey);
            var isBluetooth = containerId is { } id && IsBluetoothContainer(id);
            if (device.GetState(out var deviceState) < 0 || (deviceState & DeviceStateActive) == 0)
            {
                return new AudioEndpointLatencyInfo(
                    endpointId ?? string.Empty,
                    deviceName ?? string.Empty,
                    isBluetooth,
                    null,
                    null,
                    null,
                    "deviceUnavailable");
            }

            var audioClientInterfaceId = AudioClientInterfaceId;
            var activateResult = device.Activate(
                ref audioClientInterfaceId,
                ClsctxAll,
                nint.Zero,
                out audioClientObject);
            if (activateResult < 0 || audioClientObject is not IAudioClient activatedClient)
            {
                return new AudioEndpointLatencyInfo(
                    endpointId ?? string.Empty,
                    deviceName ?? string.Empty,
                    isBluetooth,
                    null,
                    null,
                    null,
                    "probeUnavailable");
            }

            audioClient = activatedClient;
            double? enginePeriodMilliseconds = null;
            if (audioClient.GetDevicePeriod(out var defaultDevicePeriod, out _) >= 0)
            {
                enginePeriodMilliseconds = ReferenceTimeToMilliseconds(defaultDevicePeriod);
            }

            double? streamLatencyMilliseconds = null;
            if (audioClient.GetMixFormat(out mixFormat) >= 0 && mixFormat != nint.Zero)
            {
                // A zero-duration shared buffer asks Windows for the minimum-latency buffer that
                // matches the audio engine period. The stream is never started and emits no audio.
                var initializeResult = audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlagsNoPersist,
                    0,
                    0,
                    mixFormat,
                    nint.Zero);
                if (initializeResult >= 0 && audioClient.GetStreamLatency(out var streamLatencyReferenceTime) >= 0)
                {
                    streamLatencyMilliseconds = ReferenceTimeToMilliseconds(streamLatencyReferenceTime);
                }
            }

            var bluetoothTransportUnreported = isBluetooth &&
                                               (!streamLatencyMilliseconds.HasValue ||
                                                streamLatencyMilliseconds.Value <= 0);
            var estimatedLatencyMilliseconds = CalculateEstimatedLatency(
                isBluetooth,
                streamLatencyMilliseconds,
                enginePeriodMilliseconds);
            return new AudioEndpointLatencyInfo(
                endpointId ?? string.Empty,
                deviceName ?? string.Empty,
                isBluetooth,
                estimatedLatencyMilliseconds,
                streamLatencyMilliseconds,
                enginePeriodMilliseconds,
                bluetoothTransportUnreported ? "transportUnreported" :
                estimatedLatencyMilliseconds is not null ? "estimated" :
                enginePeriodMilliseconds is not null ? "periodOnly" : "probeUnavailable");
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
                                           InvalidOperationException or PlatformNotSupportedException)
        {
            return AudioEndpointLatencyInfo.Unavailable("probeUnavailable");
        }
        finally
        {
            if (mixFormat != nint.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormat);
            }

            ReleaseComObject(audioClientObject);
            ReleaseComObject(device);
            ReleaseComObject(enumeratorObject);
        }
    }

    internal static Task<AudioEndpointLatencyInfo> ProbeAsync(
        string? requestedEndpointId,
        string? fallbackName)
    {
        var completion = new TaskCompletionSource<AudioEndpointLatencyInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(Probe(requestedEndpointId, fallbackName));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Auralis audio endpoint diagnostics"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static double ReferenceTimeToMilliseconds(long referenceTime) =>
        Math.Round(Math.Max(0, referenceTime) / 10_000d, 1);

    internal static double? CalculateEstimatedLatency(
        bool isBluetooth,
        double? streamLatencyMilliseconds,
        double? enginePeriodMilliseconds)
    {
        if (!streamLatencyMilliseconds.HasValue || !enginePeriodMilliseconds.HasValue ||
            (isBluetooth && streamLatencyMilliseconds.Value <= 0))
        {
            return null;
        }

        return Math.Round(
            Math.Max(0, streamLatencyMilliseconds.Value) +
            Math.Max(0, enginePeriodMilliseconds.Value),
            1);
    }

    internal static int PropVariantSize => Marshal.SizeOf<PropVariant>();

    internal static bool IsBluetoothEnumeratorId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("BTH", StringComparison.OrdinalIgnoreCase));

    private static string? ReadStringProperty(IMMDevice device, PropertyKey key)
    {
        IPropertyStore? propertyStore = null;
        PropVariant value = default;
        try
        {
            if (device.OpenPropertyStore(StgmRead, out propertyStore) < 0 || propertyStore is null ||
                propertyStore.GetValue(ref key, out value) < 0)
            {
                return null;
            }

            return value.VarType == VariantType.LPWStr && value.PointerValue != nint.Zero
                ? Marshal.PtrToStringUni(value.PointerValue)
                : null;
        }
        finally
        {
            if (value.VarType != VariantType.Empty)
            {
                _ = PropVariantClear(ref value);
            }
            ReleaseComObject(propertyStore);
        }
    }

    private static Guid? ReadGuidProperty(IMMDevice device, PropertyKey key)
    {
        IPropertyStore? propertyStore = null;
        PropVariant value = default;
        try
        {
            if (device.OpenPropertyStore(StgmRead, out propertyStore) < 0 || propertyStore is null ||
                propertyStore.GetValue(ref key, out value) < 0 ||
                value.VarType != VariantType.Clsid || value.PointerValue == nint.Zero)
            {
                return null;
            }

            return Marshal.PtrToStructure<Guid>(value.PointerValue);
        }
        finally
        {
            if (value.VarType != VariantType.Empty)
            {
                _ = PropVariantClear(ref value);
            }
            ReleaseComObject(propertyStore);
        }
    }

    private static bool IsBluetoothContainer(Guid containerId)
    {
        foreach (var enumeratorId in new[] { "BTHENUM", "BTHLEDEVICE", "BTHHFENUM", "BTH" })
        {
            var deviceInfoSet = SetupDiGetClassDevsW(
                nint.Zero,
                enumeratorId,
                nint.Zero,
                DigcfPresent | DigcfAllClasses);
            if (deviceInfoSet == InvalidHandleValue)
            {
                continue;
            }

            try
            {
                for (uint index = 0; ; index++)
                {
                    var deviceInfo = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
                    if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                    {
                        break;
                    }

                    var propertyBuffer = new byte[16];
                    var containerIdProperty = DevpropDeviceContainerId;
                    if (SetupDiGetDevicePropertyW(
                            deviceInfoSet,
                            ref deviceInfo,
                            ref containerIdProperty,
                            out _,
                            propertyBuffer,
                            (uint)propertyBuffer.Length,
                            out var requiredSize,
                            0) && requiredSize >= 16 &&
                        new Guid(propertyBuffer) == containerId)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                _ = SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        return false;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private enum AudioDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum AudioRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    private enum AudioClientShareMode
    {
        Shared = 0,
        Exclusive = 1
    }

    private enum VariantType : ushort
    {
        Empty = 0,
        LPWStr = 31,
        Clsid = 72
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        internal Guid FormatId = formatId;
        internal uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevicePropertyKey(Guid formatId, uint propertyId)
    {
        internal Guid FormatId = formatId;
        internal uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        private ushort _varType;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        private PropVariantUnion _value;

        internal VariantType VarType => (VariantType)_varType;

        internal nint PointerValue => _value.PointerValue;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariantUnion
    {
        [FieldOffset(0)]
        internal nint PointerValue;

        // BLOB and CA* PROPVARIANT members are a 32-bit count followed by a
        // pointer. Including the largest union member makes PROPVARIANT 16
        // bytes on x86 and 24 bytes on x64/ARM64, matching the Windows SDK.
        [FieldOffset(0)]
        private CountedPointer _countedPointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CountedPointer
    {
        private uint _count;
        private nint _pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        internal int Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal nint Reserved;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(AudioDataFlow dataFlow, uint stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(AudioDataFlow dataFlow, AudioRole role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);

        [PreserveSig]
        int OpenPropertyStore(uint storageMode, out IPropertyStore propertyStore);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            AudioClientShareMode shareMode,
            uint streamFlags,
            long bufferDuration,
            long periodicity,
            nint format,
            nint audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrameCount);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint paddingFrameCount);

        [PreserveSig]
        int IsFormatSupported(AudioClientShareMode shareMode, nint format, out nint closestMatch);

        [PreserveSig]
        int GetMixFormat(out nint format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(nint eventHandle);

        [PreserveSig]
        int GetService(ref Guid interfaceId, out nint service);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(
        nint classGuid,
        string? enumerator,
        nint parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet,
        uint memberIndex,
        ref DeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        ref DevicePropertyKey propertyKey,
        out uint propertyType,
        [Out] byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);
}
