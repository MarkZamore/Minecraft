using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Minecraft;

public sealed class NetworkNeighborService
{
    private const ushort AddressFamilyUnspecified = 0;

    public IReadOnlyDictionary<int, IReadOnlyList<IPAddress>> GetNeighbors()
    {
        var result = GetIpNetTable2(AddressFamilyUnspecified, out var table);
        if (result != 0)
        {
            throw new Win32Exception((int)result, "Could not query the Windows network neighbor table.");
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibIpNetRow2>();
            var firstRowOffset = Align(sizeof(uint), IntPtr.Size);
            var rows = new Dictionary<int, HashSet<IPAddress>>();
            for (var index = 0; index < count; index++)
            {
                var pointer = IntPtr.Add(table, firstRowOffset + index * rowSize);
                var row = Marshal.PtrToStructure<MibIpNetRow2>(pointer);
                if (row.State is not (NeighborState.Reachable or NeighborState.Stale or NeighborState.Delay or NeighborState.Probe or NeighborState.Permanent))
                {
                    continue;
                }
                var address = ParseSockAddr(row.Address);
                if (address is null || !IsUsable(address)) continue;
                if (!rows.TryGetValue((int)row.InterfaceIndex, out var addresses))
                {
                    addresses = new HashSet<IPAddress>();
                    rows[(int)row.InterfaceIndex] = addresses;
                }
                addresses.Add(address);
            }

            return rows.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<IPAddress>)pair.Value
                    .OrderBy(address => address.AddressFamily)
                    .ThenBy(address => address.ToString(), StringComparer.Ordinal)
                    .ToArray());
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private static IPAddress? ParseSockAddr(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 28) return null;
        var family = (AddressFamily)BitConverter.ToUInt16(bytes, 0);
        if (family == AddressFamily.InterNetwork)
        {
            return new IPAddress(bytes.AsSpan(4, 4));
        }
        if (family == AddressFamily.InterNetworkV6)
        {
            var scopeId = BitConverter.ToUInt32(bytes, 24);
            return new IPAddress(bytes.AsSpan(8, 16), scopeId);
        }
        return null;
    }

    private static bool IsUsable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6Multicast ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct MibIpNetRow2
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 28)]
        public byte[] Address;
        public uint InterfaceIndex;
        public ulong InterfaceLuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] PhysicalAddress;
        public uint PhysicalAddressLength;
        public NeighborState State;
        public byte Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Padding;
        public uint ReachabilityTime;
    }

    private enum NeighborState
    {
        Unreachable = 0,
        Incomplete = 1,
        Probe = 2,
        Delay = 3,
        Stale = 4,
        Reachable = 5,
        Permanent = 6,
        Maximum = 7
    }
}
