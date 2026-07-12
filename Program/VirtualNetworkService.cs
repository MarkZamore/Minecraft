using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Minecraft;

public sealed class VirtualNetworkService
{
    public IReadOnlyList<NetworkAdapterInfo> GetAdapters(string? preferredAdapterId = null)
    {
        var adapters = new List<NetworkAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var interfaceIndex = 0;
            try
            {
                interfaceIndex = props.GetIPv4Properties()?.Index ?? 0;
            }
            catch (NetworkInformationException)
            {
                // Some virtual adapters expose IPv4 addresses but no IPv4 properties.
            }
            foreach (var address in props.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = address.Address;
                var mask = address.IPv4Mask;
                if (mask is null || !IsUsableIPv4(ip)) continue;

                var isPreferred = IsPreferredNetwork(nic, ip);
                adapters.Add(new NetworkAdapterInfo
                {
                    Id = nic.Id,
                    InterfaceIndex = interfaceIndex,
                    Name = nic.Name,
                    Description = nic.Description,
                    IPv4 = ip.ToString(),
                    Mask = mask.ToString(),
                    Broadcast = CalculateBroadcast(ip, mask).ToString(),
                    IsPreferredNetwork = isPreferred,
                    NetworkType = DetectNetworkType(nic, ip),
                    SortPriority = CalculatePriority(nic, ip, isPreferred, preferredAdapterId)
                });
            }
        }

        return adapters
            .OrderBy(a => a.SortPriority)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.IPv4, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string DetectNetworkType(NetworkAdapterInfo adapter)
    {
        if (IPAddress.TryParse(adapter.IPv4, out var ip))
        {
            return DetectNetworkType(adapter.Name, adapter.Description, ip);
        }

        return NormalizeNetworkType(adapter.NetworkType);
    }

    public static string NormalizeNetworkType(string? networkType)
    {
        if (string.IsNullOrWhiteSpace(networkType))
        {
            return "Unknown";
        }

        return networkType.Trim() switch
        {
            "VPN" => "VPN",
            "LAN" => "LAN",
            "Unknown" => "Unknown",
            _ => "VPN"
        };
    }

    public static bool IsInSameNetwork(IPAddress address, IPAddress adapterAddress, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var adapterBytes = adapterAddress.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addressBytes.Length != 4 || adapterBytes.Length != 4 || maskBytes.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if ((addressBytes[i] & maskBytes[i]) != (adapterBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<IPAddress> EnumerateProbeAddresses(NetworkAdapterInfo adapter, int maxSubnetSize)
    {
        if (!IPAddress.TryParse(adapter.IPv4, out var adapterIp) ||
            !IPAddress.TryParse(adapter.Mask, out var mask))
        {
            return Array.Empty<IPAddress>();
        }

        var adapterValue = ToUInt32(adapterIp);
        var maskValue = ToUInt32(mask);
        var network = adapterValue & maskValue;
        var broadcast = network | ~maskValue;
        var count = broadcast >= network ? broadcast - network + 1 : 0;

        if (count > 2 && count <= (uint)maxSubnetSize)
        {
            return EnumerateRange(network + 1, broadcast - 1, adapterValue, maxSubnetSize);
        }

        return Array.Empty<IPAddress>();
    }

    private static List<IPAddress> EnumerateRange(uint first, uint last, uint excluded, int limit)
    {
        var result = new List<IPAddress>();
        for (var value = first; value <= last && result.Count < limit; value++)
        {
            if (value == excluded) continue;
            result.Add(FromUInt32(value));
            if (value == uint.MaxValue) break;
        }

        return result;
    }

    private static int CalculatePriority(NetworkInterface nic, IPAddress ip, bool isPreferred, string? preferredAdapterId)
    {
        if (!string.IsNullOrWhiteSpace(preferredAdapterId) &&
            string.Equals(nic.Id, preferredAdapterId, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (IsVirtualRange(ip)) return 10;
        if (isPreferred) return 20;
        if (IsPrivateLan(ip)) return 30;
        return 50;
    }

    private static bool IsPreferredNetwork(NetworkInterface nic, IPAddress ip)
    {
        return IsVirtualRange(ip) ||
               IsPrivateLan(ip) ||
               nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp ||
               LooksVirtual(nic.Name) ||
               LooksVirtual(nic.Description);
    }

    private static string DetectNetworkType(NetworkInterface nic, IPAddress ip)
    {
        if (IsVirtualRange(ip) ||
            nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp ||
            LooksVirtual(nic.Name) ||
            LooksVirtual(nic.Description))
        {
            return "VPN";
        }

        if (IsPrivateLan(ip))
        {
            return "LAN";
        }

        return "Unknown";
    }

    private static string DetectNetworkType(string name, string description, IPAddress ip)
    {
        if (IsVirtualRange(ip) ||
            LooksVirtual(name) ||
            LooksVirtual(description))
        {
            return "VPN";
        }

        if (IsPrivateLan(ip))
        {
            return "LAN";
        }

        return "Unknown";
    }

    private static bool LooksVirtual(string value)
    {
        return value.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("TUN", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableIPv4(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 &&
               bytes[0] != 0 &&
               bytes[0] != 127 &&
               !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsVirtualRange(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] is 25 or 26 ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127));
    }

    private static bool IsPrivateLan(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168));
    }

    private static IPAddress CalculateBroadcast(IPAddress ip, IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var result = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            result[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        }
        return new IPAddress(result);
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static IPAddress FromUInt32(uint value)
    {
        return new IPAddress(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
    }
}
