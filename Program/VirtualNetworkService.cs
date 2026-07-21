using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Minecraft;

public sealed class VirtualNetworkService
{
    private const SocketOptionName UnicastInterfaceOption = (SocketOptionName)31;
    private readonly NetworkProviderCatalog _providers;
    private readonly object _sessionGate = new();
    private readonly object _snapshotGate = new();
    private Dictionary<string, DateTimeOffset> _sessionProviders =
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
    private string _selectedProviderId = "";
    private bool _sessionCaptured;
    private NetworkEnvironmentSnapshot? _cachedSnapshot;
    private readonly Dictionary<string, NetworkEndpointInfo?> _routeCache =
        new(StringComparer.OrdinalIgnoreCase);

    public VirtualNetworkService(Logger logger)
    {
        _providers = new NetworkProviderCatalog(logger);
    }

    public string SelectedProviderId
    {
        get
        {
            lock (_sessionGate) return _selectedProviderId;
        }
    }

    public IReadOnlyList<NetworkProviderOption> CaptureActiveProviders()
    {
        var activeProviders = _providers.GetLatestClientStarts();
        var endpoints = EnumerateEndpoints("");
        var available = _providers.Providers
            .Where(provider =>
                activeProviders.ContainsKey(provider.Id) &&
                endpoints.Any(endpoint => string.Equals(
                    endpoint.ProviderId,
                    provider.Id,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(provider => new NetworkProviderOption(
                provider.Id,
                provider.DisplayName,
                activeProviders[provider.Id]))
            .OrderByDescending(provider => provider.StartedAtUtc)
            .ThenBy(provider => provider.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        lock (_sessionGate)
        {
            _sessionProviders = available.ToDictionary(
                provider => provider.Id,
                provider => provider.StartedAtUtc,
                StringComparer.OrdinalIgnoreCase);
            _selectedProviderId = available.FirstOrDefault()?.Id ?? "";
            _sessionCaptured = true;
        }
        InvalidateSnapshot();
        return available;
    }

    public bool SelectProvider(string providerId)
    {
        providerId = providerId?.Trim() ?? "";
        var changed = false;
        lock (_sessionGate)
        {
            if (!_sessionCaptured || !_sessionProviders.ContainsKey(providerId)) return false;
            if (string.Equals(_selectedProviderId, providerId, StringComparison.OrdinalIgnoreCase)) return false;
            _selectedProviderId = providerId;
            changed = true;
        }
        if (changed) InvalidateSnapshot();
        return changed;
    }

    public async Task<bool> WaitForProviderAsync(
        string providerId,
        TimeSpan timeout,
        CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var active = _providers.GetLatestClientStarts();
            if (active.ContainsKey(providerId) && EnumerateEndpoints("").Any(endpoint =>
                    string.Equals(endpoint.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        }
        return false;
    }

    public NetworkEnvironmentSnapshot GetSnapshot()
    {
        lock (_snapshotGate)
        {
            if (_cachedSnapshot is not null) return _cachedSnapshot;
        }

        string selectedProviderId;
        lock (_sessionGate) selectedProviderId = _selectedProviderId;
        var endpoints = EnumerateEndpoints(selectedProviderId)
            .Where(endpoint => endpoint.IsPhysical ||
                (!string.IsNullOrWhiteSpace(selectedProviderId) &&
                 string.Equals(endpoint.ProviderId, selectedProviderId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var primary = SelectPrimaryEndpoint(endpoints, selectedProviderId);
        var ordered = endpoints
            .OrderByDescending(endpoint => primary is not null &&
                                           endpoint.InterfaceId == primary.InterfaceId &&
                                           endpoint.NetworkAddress == primary.NetworkAddress)
            .ThenBy(endpoint => endpoint.SortPriority)
            .ThenBy(endpoint => endpoint.InterfaceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(endpoint => endpoint.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(endpoint => endpoint.NetworkAddress, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var snapshot = new NetworkEnvironmentSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Endpoints = ordered,
            PrimaryEndpoint = primary
        };
        lock (_snapshotGate)
        {
            _cachedSnapshot ??= snapshot;
            return _cachedSnapshot;
        }
    }

    public void InvalidateSnapshot()
    {
        lock (_snapshotGate)
        {
            _cachedSnapshot = null;
            _routeCache.Clear();
        }
    }

    public NetworkEndpointInfo? SelectLocalEndpoint(
        IPAddress remoteAddress,
        string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (IPAddress.IsLoopback(remoteAddress)) return null;

        providerId = providerId?.Trim() ?? "";
        var snapshot = GetSnapshot();
        var cacheKey = $"{snapshot.Fingerprint}|{providerId}|{remoteAddress}";
        lock (_snapshotGate)
        {
            if (_routeCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var candidates = snapshot.Endpoints
            .Where(endpoint => endpoint.AddressFamily == remoteAddress.AddressFamily)
            .ToArray();
        if (candidates.Length == 0) return CacheRoute(cacheKey, null);

        var routedLocalAddress = ResolveRouteLocalAddress(remoteAddress);
        if (routedLocalAddress is null) return CacheRoute(cacheKey, null);

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var providerEndpoint = candidates
                .Where(endpoint => string.Equals(
                    endpoint.ProviderId,
                    providerId,
                    StringComparison.OrdinalIgnoreCase))
                .Where(endpoint => AddressMatches(endpoint, routedLocalAddress))
                .OrderByDescending(endpoint => endpoint.IsPreferredNetwork)
                .ThenBy(endpoint => endpoint.SortPriority)
                .FirstOrDefault();
            return CacheRoute(cacheKey, providerEndpoint);
        }

        var routed = candidates
            .Where(endpoint => endpoint.IsPhysical)
            .Where(endpoint => AddressMatches(endpoint, routedLocalAddress))
            .OrderByDescending(endpoint => endpoint.IsPreferredNetwork)
            .ThenBy(endpoint => endpoint.SortPriority)
            .FirstOrDefault();
        return CacheRoute(cacheKey, routed);
    }

    public bool CanRoute(IPAddress target, NetworkEndpointInfo endpoint)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (target.AddressFamily != endpoint.AddressFamily) return false;
        var selected = SelectLocalEndpoint(target, endpoint.ProviderId);
        return selected is not null &&
               string.Equals(selected.InterfaceId, endpoint.InterfaceId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(selected.NetworkAddress, endpoint.NetworkAddress, StringComparison.OrdinalIgnoreCase);
    }

    public TcpClient CreateBoundTcpClient(
        IPAddress remoteAddress,
        string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        var client = new TcpClient(remoteAddress.AddressFamily);
        if (IPAddress.IsLoopback(remoteAddress)) return client;

        var localEndpoint = SelectLocalEndpoint(remoteAddress, providerId) ??
            throw new SocketException((int)SocketError.NetworkUnreachable);
        ConfigureInterface(client.Client, localEndpoint);
        client.Client.Bind(new IPEndPoint(IPAddress.Parse(localEndpoint.NetworkAddress), 0));
        return client;
    }

    public UdpClient CreateBoundUdpClient(
        NetworkEndpointInfo endpoint,
        int port,
        bool reuseAddress)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var localAddress = IPAddress.Parse(endpoint.NetworkAddress);
        var udp = new UdpClient(localAddress.AddressFamily);
        if (localAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            udp.Client.DualMode = false;
        }
        if (reuseAddress)
        {
            udp.Client.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        ConfigureInterface(udp.Client, endpoint);
        udp.Client.Bind(new IPEndPoint(localAddress, port));
        return udp;
    }

    public static void ConfigureInterface(Socket socket, NetworkEndpointInfo endpoint)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.InterfaceIndex <= 0) return;

        if (endpoint.AddressFamily == AddressFamily.InterNetwork)
        {
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                UnicastInterfaceOption,
                IPAddress.HostToNetworkOrder(endpoint.InterfaceIndex));
        }
        else if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.SetSocketOption(
                SocketOptionLevel.IPv6,
                UnicastInterfaceOption,
                endpoint.InterfaceIndex);
        }
    }

    private List<NetworkEndpointInfo> EnumerateEndpoints(string selectedProviderId)
    {
        var endpoints = new List<NetworkEndpointInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var provider = _providers.MatchAdapter(nic.Name, nic.Description);
            var isVirtual = provider is not null || IsGenericVirtualInterface(nic);
            var isPhysical = !isVirtual &&
                             nic.NetworkInterfaceType is (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211);
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (!IsUsableAddress(address)) continue;

                var interfaceIndex = GetInterfaceIndex(properties, address.AddressFamily);
                if (interfaceIndex <= 0) continue;
                var prefixLength = Math.Clamp(unicast.PrefixLength, 0, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
                var hasDefaultRoute = HasDefaultRoute(properties, address.AddressFamily);
                var providerIsOpen = provider is not null && string.Equals(
                    provider.Id,
                    selectedProviderId,
                    StringComparison.OrdinalIgnoreCase);
                var networkType = isVirtual ? "VPN" : isPhysical ? "LAN" : "Unknown";
                var broadcast = address.AddressFamily == AddressFamily.InterNetwork
                    ? CalculateBroadcast(address, PrefixToMask(prefixLength)).ToString()
                    : string.Empty;
                endpoints.Add(new NetworkEndpointInfo
                {
                    InterfaceId = nic.Id,
                    InterfaceIndex = interfaceIndex,
                    InterfaceName = nic.Name,
                    Description = nic.Description,
                    NetworkAddress = address.ToString(),
                    PrefixLength = prefixLength,
                    BroadcastAddress = broadcast,
                    ProviderId = provider?.Id ?? string.Empty,
                    IsPreferredNetwork = providerIsOpen,
                    IsPhysical = isPhysical,
                    HasDefaultRoute = hasDefaultRoute,
                    NetworkType = networkType,
                    SortPriority = providerIsOpen ? 0 : isVirtual ? 20 : hasDefaultRoute && isPhysical ? 30 : isPhysical ? 40 : 50
                });
            }
        }
        return endpoints;
    }

    internal static NetworkEndpointInfo? SelectPrimaryEndpoint(
        IReadOnlyList<NetworkEndpointInfo> endpoints,
        string selectedProviderId)
    {
        return !string.IsNullOrWhiteSpace(selectedProviderId)
            ? endpoints
                .Where(endpoint => string.Equals(endpoint.ProviderId, selectedProviderId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(endpoint => endpoint.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ThenByDescending(endpoint => endpoint.HasDefaultRoute)
                .FirstOrDefault() ?? SelectPhysicalEndpoint(endpoints)
            : endpoints
                .Where(endpoint => endpoint.IsPhysical)
                .OrderByDescending(endpoint => endpoint.HasDefaultRoute)
                .ThenBy(endpoint => endpoint.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .FirstOrDefault();
    }

    private static NetworkEndpointInfo? SelectPhysicalEndpoint(IEnumerable<NetworkEndpointInfo> endpoints) =>
        endpoints
            .Where(endpoint => endpoint.IsPhysical)
            .OrderByDescending(endpoint => endpoint.HasDefaultRoute)
            .ThenBy(endpoint => endpoint.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .FirstOrDefault();

    public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
        NetworkEnvironmentSnapshot snapshot,
        CancellationToken token) =>
        _providers.GetDynamicPeerTargetsAsync(snapshot, token);

    public static string DetectNetworkType(NetworkEndpointInfo endpoint) =>
        NormalizeNetworkType(endpoint.NetworkType);

    public static string NormalizeNetworkType(string? networkType)
    {
        if (string.IsNullOrWhiteSpace(networkType)) return "Unknown";
        return networkType.Trim().ToUpperInvariant() switch
        {
            "VPN" => "VPN",
            "LAN" => "LAN",
            _ => "Unknown"
        };
    }

    private NetworkEndpointInfo? CacheRoute(string key, NetworkEndpointInfo? endpoint)
    {
        lock (_snapshotGate) _routeCache[key] = endpoint;
        return endpoint;
    }

    private static bool AddressMatches(NetworkEndpointInfo endpoint, IPAddress? address) =>
        address is not null &&
        string.Equals(endpoint.NetworkAddress, address.ToString(), StringComparison.OrdinalIgnoreCase);

    private static IPAddress? ResolveRouteLocalAddress(IPAddress target)
    {
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public static IReadOnlyList<IPAddress> EnumerateProbeAddresses(NetworkEndpointInfo endpoint, int maxSubnetSize)
    {
        if (endpoint.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(endpoint.NetworkAddress, out var adapterIp))
        {
            return Array.Empty<IPAddress>();
        }

        var mask = PrefixToMask(endpoint.PrefixLength);
        var adapterValue = ToUInt32(adapterIp);
        var maskValue = ToUInt32(mask);
        var network = adapterValue & maskValue;
        var broadcast = network | ~maskValue;
        var count = broadcast >= network ? broadcast - network + 1 : 0;
        if (count <= 2 || count > (uint)maxSubnetSize) return Array.Empty<IPAddress>();

        var result = new List<IPAddress>();
        for (var value = network + 1; value < broadcast && result.Count < maxSubnetSize; value++)
        {
            if (value != adapterValue) result.Add(FromUInt32(value));
            if (value == uint.MaxValue) break;
        }
        return result;
    }

    public static bool SupportsDirectedBroadcast(NetworkEndpointInfo endpoint)
    {
        if (endpoint.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(endpoint.NetworkAddress, out var adapterIp) ||
            !IPAddress.TryParse(endpoint.BroadcastAddress, out var broadcast))
        {
            return false;
        }
        var addressCount = endpoint.PrefixLength >= 32 ? 1UL : 1UL << (32 - endpoint.PrefixLength);
        return addressCount > 2 && !broadcast.Equals(adapterIp);
    }

    private static int GetInterfaceIndex(IPInterfaceProperties properties, AddressFamily family)
    {
        try
        {
            return family == AddressFamily.InterNetwork
                ? properties.GetIPv4Properties()?.Index ?? 0
                : properties.GetIPv6Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException)
        {
            return 0;
        }
    }

    private static bool HasDefaultRoute(IPInterfaceProperties properties, AddressFamily family) =>
        properties.GatewayAddresses.Any(gateway =>
            gateway.Address.AddressFamily == family &&
            !gateway.Address.Equals(IPAddress.Any) &&
            !gateway.Address.Equals(IPAddress.IPv6Any));

    private static bool IsUsableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6Multicast)
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 0 && !(bytes[0] == 169 && bytes[1] == 254);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal;
    }

    private static bool IsGenericVirtualInterface(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return true;
        return LooksVirtual(nic.Name) || LooksVirtual(nic.Description);
    }

    private static bool LooksVirtual(string value) =>
        value.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("TUN", StringComparison.OrdinalIgnoreCase);

    private static IPAddress PrefixToMask(int prefixLength)
    {
        var bits = Math.Clamp(prefixLength, 0, 32);
        var value = bits == 0 ? 0U : uint.MaxValue << (32 - bits);
        return FromUInt32(value);
    }

    private static IPAddress CalculateBroadcast(IPAddress ip, IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var result = new byte[4];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (byte)(ipBytes[index] | ~maskBytes[index]);
        }
        return new IPAddress(result);
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) => new(new[]
    {
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value
    });
}

public sealed record NetworkProviderOption(
    string Id,
    string DisplayName,
    DateTimeOffset StartedAtUtc);
