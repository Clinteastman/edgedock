using System.Runtime.InteropServices;

namespace EdgeDock;

internal sealed record SystemAudioSnapshot(
    string EndpointName,
    double VolumePercent,
    bool IsMuted,
    bool IsAvailable,
    string? StatusMessage = null);

/// <summary>Reads and changes the current Windows default multimedia playback endpoint.</summary>
internal sealed class SystemAudioService : IDisposable
{
    private bool _disposed;

    public SystemAudioSnapshot GetSnapshot()
    {
        if (_disposed)
        {
            return Unavailable("Audio controls have closed.");
        }

        try
        {
            return WithEndpoint((device, volume) =>
            {
                Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var level));
                Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
                return new SystemAudioSnapshot(GetFriendlyName(device), Math.Round(level * 100, MidpointRounding.AwayFromZero), muted, true);
            });
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return Unavailable("No default playback device is available.");
        }
    }

    public SystemAudioSnapshot SetVolume(double percent)
    {
        if (_disposed)
        {
            return Unavailable("Audio controls have closed.");
        }

        try
        {
            WithEndpoint((_, volume) =>
            {
                Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar((float)Math.Clamp(percent / 100, 0, 1), IntPtr.Zero));
                return 0;
            });
            return GetSnapshot();
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return Unavailable("Windows could not change the system volume.");
        }
    }

    public SystemAudioSnapshot SetMute(bool muted)
    {
        if (_disposed)
        {
            return Unavailable("Audio controls have closed.");
        }

        try
        {
            WithEndpoint((_, volume) =>
            {
                Marshal.ThrowExceptionForHR(volume.SetMute(muted, IntPtr.Zero));
                return 0;
            });
            return GetSnapshot();
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return Unavailable("Windows could not change the mute setting.");
        }
    }

    public void Dispose() => _disposed = true;

    private static T WithEndpoint<T>(Func<IMMDevice, IAudioEndpointVolume, T> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device));
            var endpointVolumeId = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref endpointVolumeId, ClsCtx.All, IntPtr.Zero, out var activated));
            volume = (IAudioEndpointVolume)activated;
            return action(device, volume);
        }
        finally
        {
            ReleaseComObject(volume);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = default(PropVariant);
        var hasValue = false;
        try
        {
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccessMode.Read, out store));
            var key = PropertyKeys.DeviceFriendlyName;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
            hasValue = true;
            return value.GetString() ?? "Default playback device";
        }
        finally
        {
            if (hasValue)
            {
                PropVariantClear(ref value);
            }

            ReleaseComObject(store);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static SystemAudioSnapshot Unavailable(string message) => new("Audio unavailable", 0, false, false, message);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, ClsCtx clsCtx, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);

        [PreserveSig]
        int OpenPropertyStore(StorageAccessMode accessMode, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out DeviceState state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
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
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notificationCallback);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notificationCallback);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelInDecibels, IntPtr eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelInDecibels);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channelNumber, float levelInDecibels, IntPtr eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channelNumber, float level, IntPtr eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channelNumber, out float levelInDecibels);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channelNumber, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(IntPtr eventContext);

        [PreserveSig]
        int VolumeStepDown(IntPtr eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float minimumLevelInDecibels, out float maximumLevelInDecibels, out float volumeIncrementInDecibels);
    }

    private enum EDataFlow { Render, Capture, All }
    private enum ERole { Console, Multimedia, Communications }
    private enum StorageAccessMode { Read }
    private enum DeviceState { Active = 0x1 }
    private enum ClsCtx { All = 23 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;

        public string? GetString() => VariantType == 31 ? Marshal.PtrToStringUni(PointerValue) : null;
    }

    private static class PropertyKeys
    {
        public static readonly PropertyKey DeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    }
}
