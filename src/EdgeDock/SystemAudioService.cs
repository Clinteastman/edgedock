using System.Runtime.InteropServices;

namespace EdgeDock;

internal sealed record SystemAudioSnapshot(
    string EndpointName,
    double VolumePercent,
    bool IsMuted,
    bool IsAvailable,
    string? StatusMessage = null);

internal sealed record AudioDevice(string Id, string Name, bool IsDefault);

internal sealed record MicrophoneSnapshot(string Name, bool IsMuted, bool IsAvailable)
{
    public static MicrophoneSnapshot Unavailable { get; } = new("No microphone", false, false);
}

/// <summary>
/// Reads and changes Windows audio: the default playback device and its volume, the default
/// microphone's mute, and each app's volume. Use from the UI thread. Loading or refreshing
/// never changes anything; only the explicit Set methods do.
/// </summary>
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

    // ---------- Output devices ----------

    /// <summary>Active playback devices, with the current default marked.</summary>
    public IReadOnlyList<AudioDevice> GetOutputs()
    {
        if (_disposed) return [];
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var defaultId = DefaultId(enumerator, EDataFlow.Render, ERole.Multimedia);
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            var devices = new List<AudioDevice>((int)count);
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    Marshal.ThrowExceptionForHR(collection.Item(index, out device));
                    Marshal.ThrowExceptionForHR(device.GetId(out var id));
                    devices.Add(new AudioDevice(id, GetFriendlyName(device), string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally { ReleaseComObject(device); }
            }
            return devices.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return [];
        }
        finally
        {
            ReleaseComObject(collection);
            ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Makes a device the Windows default for every role, as the Sound settings page does.
    /// Windows has no public API for this; IPolicyConfig is the long-standing interface the
    /// Sound control panel and tools such as EarTrumpet use. Returns false if it is refused.
    /// </summary>
    public bool SetDefaultOutput(string deviceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(deviceId)) return false;
        IPolicyConfig? policy = null;
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            ERole[] roles = [ERole.Console, ERole.Multimedia, ERole.Communications];
            // The three roles change one at a time, so remember them to undo a partial switch.
            var previous = roles.ToDictionary(role => role, role => DefaultId(enumerator, EDataFlow.Render, role));
            policy = (IPolicyConfig)new PolicyConfigComObject();
            var switched = new List<ERole>();
            foreach (var role in roles)
            {
                if (policy.SetDefaultEndpoint(deviceId, role) != 0)
                {
                    foreach (var done in switched)
                        if (previous[done] is { } before) policy.SetDefaultEndpoint(before, done);
                    return false;
                }
                switched.Add(role);
            }
            return true;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(policy);
            ReleaseComObject(enumerator);
        }
    }

    // ---------- Microphone ----------

    /// <summary>The default communications microphone, which calls and voice chat use.</summary>
    public MicrophoneSnapshot GetMicrophone()
    {
        if (_disposed) return MicrophoneSnapshot.Unavailable;
        try
        {
            return WithEndpoint(EDataFlow.Capture, ERole.Communications, (device, volume) =>
            {
                Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
                return new MicrophoneSnapshot(GetFriendlyName(device), muted, true);
            });
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return MicrophoneSnapshot.Unavailable;
        }
    }

    public MicrophoneSnapshot SetMicrophoneMute(bool muted)
    {
        if (_disposed) return MicrophoneSnapshot.Unavailable;
        try
        {
            WithEndpoint(EDataFlow.Capture, ERole.Communications, (_, volume) =>
            {
                Marshal.ThrowExceptionForHR(volume.SetMute(muted, IntPtr.Zero));
                return 0;
            });
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException) { }
        return GetMicrophone();
    }

    // ---------- Apps ----------

    /// <summary>Apps with a sound session on the default output, one row per app.</summary>
    public IReadOnlyList<AudioSessionInfo> GetSessions()
    {
        if (_disposed) return [];
        try
        {
            var sessions = new List<AudioSessionInfo>();
            var seen = new HashSet<uint>();
            ForEachSession((control, volume) =>
            {
                Marshal.ThrowExceptionForHR(control.GetState(out var state));
                if (state == AudioSessionState.Expired) return;
                Marshal.ThrowExceptionForHR(control.GetProcessId(out var processId));
                var isSystem = control.IsSystemSoundsSession() == 0;
                control.GetDisplayName(out var sessionName);
                Marshal.ThrowExceptionForHR(volume.GetMasterVolume(out var level));
                Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
                seen.Add(processId);
                var process = isSystem ? default : DescribeProcess(processId);
                sessions.Add(new AudioSessionInfo(
                    AudioNaming.KeyFor(processId, isSystem),
                    processId,
                    AudioNaming.DisplayName(sessionName, process.Description, process.Name, isSystem),
                    process.Path,
                    Math.Round(level * 100, MidpointRounding.AwayFromZero),
                    muted,
                    isSystem,
                    state == AudioSessionState.Active));
            });
            foreach (var stale in _processes.Keys.Where(processId => !seen.Contains(processId)).ToArray()) _processes.Remove(stale);
            return AudioNaming.Arrange(sessions);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Sets every session of one app (or System sounds) to the same level.</summary>
    public void SetSessionVolume(string key, double percent) =>
        ForSessionsWithKey(key, volume =>
        {
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMasterVolume((float)Math.Clamp(percent / 100, 0, 1), ref context));
        });

    public void SetSessionMute(string key, bool muted) =>
        ForSessionsWithKey(key, volume =>
        {
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMute(muted, ref context));
        });

    private void ForSessionsWithKey(string key, Action<ISimpleAudioVolume> change)
    {
        if (_disposed) return;
        try
        {
            ForEachSession((control, volume) =>
            {
                Marshal.ThrowExceptionForHR(control.GetProcessId(out var processId));
                if (AudioNaming.KeyFor(processId, control.IsSystemSoundsSession() == 0) == key) change(volume);
            });
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException) { }
    }

    private static void ForEachSession(Action<IAudioSessionControl2, ISimpleAudioVolume> visit)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device));
            var managerId = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref managerId, ClsCtx.All, IntPtr.Zero, out var activated));
            manager = (IAudioSessionManager2)activated;
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
            Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));
            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    Marshal.ThrowExceptionForHR(sessions.GetSession(index, out control));
                    // One COM object answers for the session, its process and its volume.
                    visit((IAudioSessionControl2)control, (ISimpleAudioVolume)control);
                }
                finally { ReleaseComObject(control); }
            }
        }
        finally
        {
            ReleaseComObject(sessions);
            ReleaseComObject(manager);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private readonly Dictionary<uint, (string? Name, string? Description, string? Path)> _processes = [];

    /// <summary>Process name, description and path, cached while the app keeps its session.</summary>
    private (string? Name, string? Description, string? Path) DescribeProcess(uint processId)
    {
        if (_processes.TryGetValue(processId, out var known)) return known;
        string? name = null, description = null, path = null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { }
        // Limited query access works for most processes, including many elevated ones.
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                var size = (uint)buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size)) path = buffer.ToString();
            }
            finally { CloseHandle(handle); }
        }
        if (path is not null)
        {
            try { description = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription; }
            catch (FileNotFoundException) { }
        }
        return _processes[processId] = (name, description, path);
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator, EDataFlow flow, ERole role)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0) return null;
        try { return device.GetId(out var id) == 0 ? id : null; }
        finally { ReleaseComObject(device); }
    }

    private static T WithEndpoint<T>(Func<IMMDevice, IAudioEndpointVolume, T> action) =>
        WithEndpoint(EDataFlow.Render, ERole.Multimedia, action);

    private static T WithEndpoint<T>(EDataFlow flow, ERole role, Func<IMMDevice, IAudioEndpointVolume, T> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(flow, role, out device));
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
        int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);

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

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionId, uint flags, out IAudioSessionControl control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionId, uint flags, out ISimpleAudioVolume volume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out AudioSessionState state);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out AudioSessionState state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
        /// <summary>S_OK (0) for the System sounds session, S_FALSE otherwise.</summary>
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigComObject
    {
    }

    /// <summary>Undocumented but stable since Windows 7; only SetDefaultEndpoint is used.</summary>
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    private enum AudioSessionState { Inactive, Active, Expired }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

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
