using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Minecraft;

internal sealed class VoiceQosSession : IDisposable
{
    private readonly Logger _logger;
    private readonly Socket _socket;
    private readonly object _gate = new();
    private IntPtr _handle;
    private uint _flowId;
    private readonly HashSet<string> _destinations = new(StringComparer.OrdinalIgnoreCase);
    private bool _qosUnavailable;
    private bool _enabled;

    private VoiceQosSession(Socket socket, Logger logger)
    {
        _socket = socket;
        _logger = logger;
    }

    public static VoiceQosSession AttachBestEffort(Socket socket, Logger logger, bool enabled)
    {
        var session = new VoiceQosSession(socket, logger);
        session.SetEnabled(enabled);
        return session;
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (enabled)
            {
                TryConfigure();
            }
            else
            {
                TryClearSocketPriority();
                DisposeNative();
                _qosUnavailable = false;
            }
        }
    }

    private void TryConfigure()
    {
        try
        {
            var level = _socket.AddressFamily == AddressFamily.InterNetworkV6
                ? SocketOptionLevel.IPv6
                : SocketOptionLevel.IP;
            _socket.SetSocketOption(level, SocketOptionName.TypeOfService, 46 << 2);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            _logger.Info($"Voice DSCP EF is unavailable; continuing without it: {ex.Message}");
        }

        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var version = new QosVersion { MajorVersion = 1, MinorVersion = 0 };
            if (!QOSCreateHandle(ref version, out _handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _logger.Info("Voice qWave handle enabled.");
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            DisposeNative();
            _logger.Info($"Voice qWave is unavailable; using socket priority only: {ex.Message}");
        }
    }

    private void TryClearSocketPriority()
    {
        try
        {
            var level = _socket.AddressFamily == AddressFamily.InterNetworkV6
                ? SocketOptionLevel.IPv6
                : SocketOptionLevel.IP;
            _socket.SetSocketOption(level, SocketOptionName.TypeOfService, 0);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or ObjectDisposedException)
        {
            _logger.Info($"Voice socket priority could not be reset: {ex.Message}");
        }
    }

    public void AddDestination(IPEndPoint endpoint)
    {
        lock (_gate)
        {
            AddDestinationCore(endpoint);
        }
    }

    private void AddDestinationCore(IPEndPoint endpoint)
    {
        if (!_enabled || _handle == IntPtr.Zero || _qosUnavailable || endpoint.AddressFamily != AddressFamily.InterNetwork) return;
        var key = endpoint.ToString();
        if (!_destinations.Add(key)) return;
        var nativeAddress = IntPtr.Zero;
        try
        {
            var addressBytes = endpoint.Address.GetAddressBytes();
            var socketAddress = new byte[16];
            socketAddress[0] = 2;
            socketAddress[1] = 0;
            socketAddress[2] = (byte)(endpoint.Port >> 8);
            socketAddress[3] = (byte)endpoint.Port;
            Buffer.BlockCopy(addressBytes, 0, socketAddress, 4, 4);
            nativeAddress = Marshal.AllocHGlobal(socketAddress.Length);
            Marshal.Copy(socketAddress, 0, nativeAddress, socketAddress.Length);
            if (!QOSAddSocketToFlow(
                    _handle,
                    _socket.Handle,
                    nativeAddress,
                    QosTrafficType.Voice,
                    0,
                    ref _flowId))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            _qosUnavailable = true;
            _logger.Info($"Voice qWave flow is unavailable; continuing with DSCP fallback: {ex.Message}");
        }
        finally
        {
            if (nativeAddress != IntPtr.Zero) Marshal.FreeHGlobal(nativeAddress);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _enabled = false;
            TryClearSocketPriority();
            DisposeNative();
        }
        GC.SuppressFinalize(this);
    }

    private void DisposeNative()
    {
        if (_handle == IntPtr.Zero) return;
        if (_flowId != 0)
        {
            _ = QOSRemoveSocketFromFlow(_handle, _socket.Handle, _flowId, 0);
            _flowId = 0;
        }
        _ = QOSCloseHandle(_handle);
        _handle = IntPtr.Zero;
        _destinations.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QosVersion
    {
        public ushort MajorVersion;
        public ushort MinorVersion;
    }

    private enum QosTrafficType
    {
        BestEffort = 0,
        Background = 1,
        ExcellentEffort = 2,
        AudioVideo = 3,
        Voice = 4,
        Control = 5
    }

    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSCreateHandle(ref QosVersion version, out IntPtr handle);

    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSAddSocketToFlow(
        IntPtr handle,
        IntPtr socket,
        IntPtr destinationAddress,
        QosTrafficType trafficType,
        uint flags,
        ref uint flowId);

    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSRemoveSocketFromFlow(IntPtr handle, IntPtr socket, uint flowId, uint flags);

    [DllImport("qwave.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSCloseHandle(IntPtr handle);
}
