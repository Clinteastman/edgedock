using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EdgeDock;

internal enum WaveformCaptureState { Starting, Running, NoDevice, DeviceBusy, Unsupported, Failed }

/// <summary>An input device the waveform can listen to instead of the PC's sound.</summary>
internal sealed record WaveformInput(string Id, string Name);

/// <summary>
/// Listens to the default playback device (WASAPI loopback, shared mode) or to a chosen input,
/// on its own thread, and turns the sound into <see cref="WaveformColumn"/>s in <see cref="Ring"/>
/// and recent samples in <see cref="Samples"/>.
/// Follows default-device changes and retries when a device disappears. Audio is analysed as it
/// arrives and never stored or sent anywhere. Dispose stops the thread; it releases itself.
/// </summary>
internal sealed class WaveformCapture : IDisposable
{
    private const int AudclntShareModeShared = 0;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint BufferFlagsSilent = 0x2;
    private const long BufferDuration = 200_000; // 20 ms in 100 ns units.
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntEUnsupportedFormat = unchecked((int)0x88890008);
    private const int AudclntEDeviceInUse = unchecked((int)0x8889000A);
    private const int AudclntEServiceNotRunning = unchecked((int)0x88890010);
    private const int ENotFound = unchecked((int)0x80070490);
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly string? _inputId;
    private readonly AutoResetEvent _audioReady = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private int _state = (int)WaveformCaptureState.Starting;
    private int _disposed;

    /// <param name="inputId">An input endpoint ID, or null for what the PC is playing.</param>
    public WaveformCapture(string? inputId)
    {
        _inputId = inputId;
        _thread = new Thread(Run) { IsBackground = true, Name = "EdgeDock waveform capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>~10 seconds of columns; the drawing thread reads them as it goes.</summary>
    public WaveformRing Ring { get; } = new(2048);

    /// <summary>The newest mono samples, for the live views.</summary>
    public WaveformSampleTap Samples { get; } = new();

    public WaveformCaptureState State => (WaveformCaptureState)Volatile.Read(ref _state);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
    }

    // ---------- Capture thread ----------

    private void Run()
    {
        try
        {
            while (!_stop.WaitOne(0))
            {
                var retry = TimeSpan.FromSeconds(1);
                try
                {
                    CaptureUntilDeviceChanges();
                    retry = TimeSpan.Zero; // A device change: open the new one straight away.
                }
                catch (COMException exception)
                {
                    SetState(exception.HResult switch
                    {
                        ENotFound or AudclntEDeviceInvalidated or AudclntEServiceNotRunning => WaveformCaptureState.NoDevice,
                        AudclntEDeviceInUse => WaveformCaptureState.DeviceBusy,
                        AudclntEUnsupportedFormat => WaveformCaptureState.Unsupported,
                        _ => WaveformCaptureState.Failed
                    });
                    if (exception.HResult == AudclntEDeviceInUse) retry = TimeSpan.FromSeconds(3);
                }
                catch (Exception exception) when (exception is InvalidCastException or NotSupportedException or ArgumentException)
                {
                    SetState(WaveformCaptureState.Unsupported);
                    retry = TimeSpan.FromSeconds(3);
                }
                if (retry > TimeSpan.Zero && _stop.WaitOne(retry)) break;
            }
        }
        finally
        {
            _audioReady.Dispose();
            _stop.Dispose();
        }
    }

    private void SetState(WaveformCaptureState state) => Volatile.Write(ref _state, (int)state);

    private void CaptureUntilDeviceChanges()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var started = false;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var loopback = _inputId is null;
            if (loopback) Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device));
            else
            {
                Check(enumerator.GetDevice(_inputId!, out device));
                Check(device.GetState(out var deviceState));
                if (deviceState != DeviceStateActive) throw new COMException("Input not connected", ENotFound);
            }
            Check(device.GetId(out var deviceId));

            var format = IntPtr.Zero;
            WaveFormat wave;
            var eventDriven = true;
            try
            {
                client = Activate(device);
                Check(client.GetMixFormat(out format));
                wave = WaveFormat.Read(format);
                var flags = (loopback ? StreamFlagsLoopback : 0) | (eventDriven ? StreamFlagsEventCallback : 0);
                var result = client.Initialize(AudclntShareModeShared, flags, BufferDuration, 0, format, IntPtr.Zero);
                if (result < 0 && result != AudclntEDeviceInUse && result != AudclntEDeviceInvalidated)
                {
                    // Older Windows builds refuse event-driven loopback: poll every 10 ms instead.
                    ReleaseComObject(client);
                    client = Activate(device);
                    eventDriven = false;
                    result = client.Initialize(AudclntShareModeShared, flags & ~StreamFlagsEventCallback, BufferDuration, 0, format, IntPtr.Zero);
                }
                Check(result);
            }
            finally
            {
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            }

            if (eventDriven) Check(client.SetEventHandle(_audioReady.SafeWaitHandle.DangerousGetHandle()));
            var captureId = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref captureId, out var service));
            capture = (IAudioCaptureClient)service;
            var analyzer = new WaveformAnalyzer(wave.SampleRate, wave.Channels, Samples);
            var scratch = new float[wave.SampleRate * wave.Channels]; // One second; packets are far smaller.
            var bytes = new byte[scratch.Length * 4];
            Check(client.Start());
            started = true;
            SetState(WaveformCaptureState.Running);

            // Loopback gets no packets at all while nothing plays, so silence is filled in by
            // the clock: the drawing side then keeps scrolling at a steady speed.
            var clock = Stopwatch.StartNew();
            var columnsAtStart = analyzer.ColumnsWritten;
            // Device checks keep their own clock: the column clock above restarts when it drifts.
            var sinceDeviceCheck = Stopwatch.StartNew();
            WaitHandle[] waits = [_stop, _audioReady];
            while (true)
            {
                // The short timeout also paces the silence fill below while loopback is idle.
                var signalled = eventDriven ? WaitHandle.WaitAny(waits, 15) : (_stop.WaitOne(10) ? 0 : 1);
                if (signalled == 0) return;

                var read = false;
                while (true)
                {
                    Check(capture.GetNextPacketSize(out var packetFrames));
                    if (packetFrames == 0) break;
                    Check(capture.GetBuffer(out var data, out var frames, out var bufferFlags, out _, out _));
                    try
                    {
                        if ((bufferFlags & BufferFlagsSilent) != 0 || data == IntPtr.Zero) analyzer.ProcessSilence((int)frames, Ring);
                        else
                        {
                            var samples = Math.Min((int)frames * wave.Channels, scratch.Length);
                            wave.ToFloat(data, scratch.AsSpan(0, samples), ref bytes);
                            analyzer.Process(scratch.AsSpan(0, samples), Ring);
                        }
                    }
                    finally { Check(capture.ReleaseBuffer(frames)); }
                    read = true;
                }

                var expected = (long)(clock.Elapsed.TotalSeconds * WaveformAnalyzer.ColumnsPerSecond);
                var written = analyzer.ColumnsWritten - columnsAtStart;
                if (!read && expected - written > 4)
                {
                    // Over 20 ms with no packets: the stream is idle, so add the silence.
                    analyzer.ProcessSilence((int)((expected - written - 1) * analyzer.BlockFrames), Ring);
                }
                else if (written - expected > 40)
                {
                    // The device's clock ran ahead of ours; start counting again from here.
                    columnsAtStart = analyzer.ColumnsWritten;
                    clock.Restart();
                }

                if (sinceDeviceCheck.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    sinceDeviceCheck.Restart();
                    if (DeviceChanged(enumerator, device, deviceId, loopback)) return;
                }
            }
        }
        finally
        {
            if (started) client?.Stop();
            ReleaseComObject(capture);
            ReleaseComObject(client);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
            if (!_stop.WaitOne(0) && State == WaveformCaptureState.Running) SetState(WaveformCaptureState.Starting);
        }
    }

    /// <summary>The default output moved elsewhere, or the chosen input was unplugged.</summary>
    private static bool DeviceChanged(IMMDeviceEnumerator enumerator, IMMDevice device, string deviceId, bool loopback)
    {
        if (loopback)
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var current) != 0) return true;
            try { return current.GetId(out var currentId) != 0 || !string.Equals(currentId, deviceId, StringComparison.OrdinalIgnoreCase); }
            finally { ReleaseComObject(current); }
        }
        return device.GetState(out var state) != 0 || state != DeviceStateActive;
    }

    private static IAudioClient Activate(IMMDevice device)
    {
        var clientId = typeof(IAudioClient).GUID;
        Check(device.Activate(ref clientId, ClsCtxAll, IntPtr.Zero, out var activated));
        return (IAudioClient)activated;
    }

    private static void Check(int result)
    {
        if (result < 0) throw new COMException("Windows audio call failed", result);
    }

    /// <summary>The sample layout Windows' shared-mode mix format uses on this device.</summary>
    private readonly record struct WaveFormat(int SampleRate, int Channels, int BitsPerSample, int BlockAlign, bool IsFloat)
    {
        public static WaveFormat Read(IntPtr pointer)
        {
            var tag = (ushort)Marshal.ReadInt16(pointer, 0);
            var channels = (ushort)Marshal.ReadInt16(pointer, 2);
            var sampleRate = Marshal.ReadInt32(pointer, 4);
            var blockAlign = (ushort)Marshal.ReadInt16(pointer, 12);
            var bits = (ushort)Marshal.ReadInt16(pointer, 14);
            bool isFloat;
            if (tag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE: the sub-format GUID follows at byte 24.
            {
                var bytes = new byte[16];
                Marshal.Copy(pointer + 24, bytes, 0, 16);
                var subtype = new Guid(bytes);
                if (subtype == FloatSubtype) isFloat = true;
                else if (subtype == PcmSubtype) isFloat = false;
                else throw new NotSupportedException("Unsupported audio sub-format.");
            }
            else if (tag == 3) isFloat = true;
            else if (tag == 1) isFloat = false;
            else throw new NotSupportedException("Unsupported audio format.");
            if (channels == 0 || sampleRate <= 0 || blockAlign == 0) throw new NotSupportedException("Unusable audio format.");
            if (isFloat ? bits != 32 : bits is not (16 or 24 or 32)) throw new NotSupportedException("Unsupported sample size.");
            return new(sampleRate, channels, bits, blockAlign, isFloat);
        }

        /// <summary>Copies <paramref name="destination"/>.Length samples out of a capture buffer.</summary>
        public void ToFloat(IntPtr data, Span<float> destination, ref byte[] bytes)
        {
            // Containers can be wider than the samples (24 bits in 32): step by the real size.
            var stride = BlockAlign / Channels;
            var length = destination.Length * stride;
            if (bytes.Length < length) bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            var source = bytes.AsSpan(0, length);
            var bytesPerSample = BitsPerSample / 8;
            for (var i = 0; i < destination.Length; i++)
            {
                var sample = source.Slice(i * stride, bytesPerSample);
                destination[i] = IsFloat ? BinaryPrimitives.ReadSingleLittleEndian(sample) : bytesPerSample switch
                {
                    2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                    3 => ((sample[0] << 8) | (sample[1] << 16) | (sample[2] << 24)) / 2147483648f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f
                };
            }
        }
    }

    // ---------- Input list for Settings ----------

    /// <summary>Connected input devices (microphones, audio interfaces, mixers), by name.</summary>
    public static IReadOnlyList<WaveformInput> ListInputs()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Check(enumerator.EnumAudioEndpoints(EDataFlow.Capture, DeviceStateActive, out collection));
            Check(collection.GetCount(out var count));
            var inputs = new List<WaveformInput>((int)count);
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    Check(collection.Item(index, out device));
                    Check(device.GetId(out var id));
                    inputs.Add(new WaveformInput(id, FriendlyName(device) ?? "Audio input"));
                }
                catch (Exception exception) when (exception is COMException or InvalidCastException) { }
                finally { ReleaseComObject(device); }
            }
            return inputs.OrderBy(input => input.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
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

    private static string? FriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = default(PropVariant);
        var hasValue = false;
        try
        {
            if (device.OpenPropertyStore(0, out store) != 0) return null;
            var key = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
            if (store.GetValue(ref key, out value) != 0) return null;
            hasValue = true;
            return value.VariantType == 31 ? Marshal.PtrToStringUni(value.PointerValue) : null;
        }
        finally
        {
            if (hasValue) PropVariantClear(ref value);
            ReleaseComObject(store);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    // ---------- Core Audio interop (same style as SystemAudioService) ----------

    private const uint DeviceStateActive = 0x1;
    private const int ClsCtxAll = 23;
    private enum EDataFlow { Render, Capture, All }
    private enum ERole { Console, Multimedia, Communications }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
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
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        [PreserveSig] int OpenPropertyStore(int accessMode, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propertyCount);
        [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong performanceCounterPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

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
    }
}
