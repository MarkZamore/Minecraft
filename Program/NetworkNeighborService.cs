using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace Minecraft;

public sealed class NetworkNeighborService
{
    private const int ErrorInsufficientBuffer = 122;
    private const int DynamicEntry = 3;
    private const int StaticEntry = 4;

    public IReadOnlyDictionary<int, IReadOnlyList<IPAddress>> GetIPv4Neighbors()
    {
        var size = 0;
        var result = GetIpNetTable(IntPtr.Zero, ref size, order: false);
        if (result != ErrorInsufficientBuffer || size <= sizeof(int))
        {
            if (result == 0) return new Dictionary<int, IReadOnlyList<IPAddress>>();
            throw new Win32Exception(result, "Could not query the Windows IPv4 neighbor table.");
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetIpNetTable(buffer, ref size, order: false);
            if (result != 0)
            {
                throw new Win32Exception(result, "Could not read the Windows IPv4 neighbor table.");
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpNetRow>();
            var rows = new Dictionary<int, HashSet<IPAddress>>();
            var offset = sizeof(int);
            for (var index = 0; index < count && offset + rowSize <= size; index++, offset += rowSize)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow>(IntPtr.Add(buffer, offset));
                if (row.Type is not (DynamicEntry or StaticEntry))
                {
                    continue;
                }

                var physicalAddress = row.PhysicalAddress ?? Array.Empty<byte>();
                var physicalLength = Math.Min((int)row.PhysicalAddressLength, physicalAddress.Length);
                if (physicalLength > 0 &&
                    (physicalAddress.Take(physicalLength).All(value => value == 0) ||
                     physicalAddress.Take(physicalLength).All(value => value == byte.MaxValue)))
                {
                    continue;
                }

                var addressBytes = BitConverter.GetBytes(row.Address);
                var address = new IPAddress(addressBytes);
                if (!IsUsable(address)) continue;

                if (!rows.TryGetValue((int)row.InterfaceIndex, out var addresses))
                {
                    addresses = new HashSet<IPAddress>();
                    rows[(int)row.InterfaceIndex] = addresses;
                }
                addresses.Add(address);
            }

            return rows.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<IPAddress>)pair.Value.OrderBy(address => address.ToString(), StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsUsable(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               bytes[0] != 0 &&
               bytes[0] != 127 &&
               bytes[0] < 224 &&
               !(bytes[0] == 169 && bytes[1] == 254) &&
               !address.Equals(IPAddress.Broadcast);
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetIpNetTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow
    {
        public uint InterfaceIndex;
        public uint PhysicalAddressLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] PhysicalAddress;

        public uint Address;
        public int Type;
    }
}
