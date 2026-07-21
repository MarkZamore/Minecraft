using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public interface IPeerRouteResolver
{
    IReadOnlyList<PeerCandidateEndpoint> GetSendCandidates(
        string? identityId,
        string? preferredProviderId = null,
        AddressFamily? addressFamily = null);

    bool IsKnownEndpoint(string? identityId, IPAddress address);
    void MarkEndpointHealthy(string identityId, PeerCandidateEndpoint endpoint);
    void MarkEndpointUnhealthy(string identityId, PeerCandidateEndpoint endpoint);
}

public sealed class PeerCandidateEndpoint : IEquatable<PeerCandidateEndpoint>
{
    public required string Address { get; init; }
    public string ProviderId { get; set; } = "";
    public string InterfaceId { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string NetworkType { get; set; } = "Unknown";
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSuccessUtc { get; set; }
    public bool IsObserved { get; set; }
    public bool IsConfirmed { get; set; }
    public int FailureScore { get; set; }
    public long Revision { get; set; }

    public bool IsReachable => FailureScore < 3;
    public string State => IsConfirmed ? "Healthy" : FailureScore > 0 ? "Unhealthy" : "Candidate";

    public PeerCandidateEndpoint Copy() => new()
    {
        Address = Address,
        ProviderId = ProviderId,
        InterfaceId = InterfaceId,
        AddressFamily = AddressFamily,
        NetworkType = NetworkType,
        LastSeenUtc = LastSeenUtc,
        LastSuccessUtc = LastSuccessUtc,
        IsObserved = IsObserved,
        IsConfirmed = IsConfirmed,
        FailureScore = FailureScore,
        Revision = Revision
    };

    public bool Equals(PeerCandidateEndpoint? other) =>
        other is not null &&
        string.Equals(Address, other.Address, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ProviderId, other.ProviderId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(InterfaceId, other.InterfaceId, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as PeerCandidateEndpoint);

    public override int GetHashCode() => HashCode.Combine(
        Address.Trim().ToLowerInvariant(),
        ProviderId.Trim().ToLowerInvariant(),
        InterfaceId.Trim().ToLowerInvariant());
}

public sealed class PeerRouteResolver : IPeerRouteResolver
{
    private const int MaxPeers = 1024;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan EndpointTtl = TimeSpan.FromDays(30);
    private readonly Dictionary<string, PeerRouteState> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _endpointIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void UpsertFromAnnouncement(PeerAnnouncement announcement, IPAddress? observedAddress = null)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        var now = DateTimeOffset.UtcNow;
        observedAddress ??= ParseAddress(announcement.NetworkAddress);

        var advertised = new List<(IPAddress Address, PeerAdvertisedEndpoint Metadata, bool Observed)>();
        foreach (var item in announcement.NetworkEndpoints ?? [])
        {
            if (!TryGetUsableAddress(item.Address, out var address)) continue;
            advertised.Add((address, item, observedAddress is not null && address.Equals(observedAddress)));
        }

        if (observedAddress is not null && IsUsableAddress(observedAddress) &&
            advertised.All(item => !item.Address.Equals(observedAddress)))
        {
            advertised.Insert(0, (observedAddress, new PeerAdvertisedEndpoint
            {
                Address = observedAddress.ToString(),
                ProviderId = announcement.NetworkProviderId,
                InterfaceId = announcement.NetworkInterfaceId,
                AddressFamily = observedAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                NetworkType = announcement.NetworkType
            }, true));
        }

        var identityId = NormalizeIdentity(announcement.IdentityId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(identityId))
            {
                identityId = ResolveLegacyIdentityLocked(advertised.Select(item => item.Address)) ??
                             (advertised.Count > 0 ? $"legacy:{advertised[0].Address}" : "");
            }
            if (string.IsNullOrWhiteSpace(identityId)) return;

            if (!_peers.TryGetValue(identityId, out var peer))
            {
                peer = new PeerRouteState(identityId);
                _peers.Add(identityId, peer);
            }

            peer.PlayerName = announcement.PlayerName?.Trim() ?? "";
            peer.LastSeenUtc = now;

            foreach (var group in advertised.GroupBy(item => item.Address.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                var item = group.OrderByDescending(candidate => candidate.Observed).First();
                var key = item.Address.ToString();
                if (!peer.Endpoints.TryGetValue(key, out var endpoint))
                {
                    endpoint = new PeerCandidateEndpoint { Address = key };
                    peer.Endpoints.Add(key, endpoint);
                }

                endpoint.ProviderId = FirstNonEmpty(item.Metadata.ProviderId, announcement.NetworkProviderId);
                endpoint.InterfaceId = FirstNonEmpty(item.Metadata.InterfaceId, announcement.NetworkInterfaceId);
                endpoint.AddressFamily = item.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
                endpoint.NetworkType = VirtualNetworkService.NormalizeNetworkType(
                    FirstNonEmpty(item.Metadata.NetworkType, announcement.NetworkType));
                endpoint.LastSeenUtc = now;
                endpoint.IsObserved |= item.Observed;
                endpoint.Revision = NextRevision(endpoint.Revision);
                if (item.Observed)
                {
                    endpoint.IsConfirmed = true;
                    endpoint.FailureScore = 0;
                }
                AddEndpointIndexLocked(key, identityId);
            }

            PruneLocked(now);
        }
    }

    public IReadOnlyList<PeerCandidateEndpoint> GetSendCandidates(
        string? identityId,
        string? preferredProviderId = null,
        AddressFamily? addressFamily = null)
    {
        identityId = NormalizeIdentity(identityId);
        preferredProviderId = preferredProviderId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(identityId)) return Array.Empty<PeerCandidateEndpoint>();

        lock (_gate)
        {
            if (!_peers.TryGetValue(identityId, out var peer)) return Array.Empty<PeerCandidateEndpoint>();

            return peer.Endpoints.Values
                .Where(endpoint => DateTimeOffset.UtcNow - endpoint.LastSeenUtc <= EndpointTtl)
                .Where(endpoint => addressFamily is null || GetAddressFamily(endpoint) == addressFamily)
                .Where(endpoint => IsAllowedProvider(endpoint, preferredProviderId))
                .OrderByDescending(endpoint =>
                    !string.IsNullOrWhiteSpace(preferredProviderId) &&
                    string.Equals(endpoint.ProviderId, preferredProviderId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(endpoint => endpoint.IsConfirmed)
                .ThenByDescending(endpoint => endpoint.LastSuccessUtc)
                .ThenByDescending(endpoint => endpoint.IsObserved)
                .ThenBy(endpoint => GetAddressFamily(endpoint) == AddressFamily.InterNetwork ? 0 : 1)
                .ThenBy(endpoint => endpoint.FailureScore)
                .ThenByDescending(endpoint => endpoint.LastSeenUtc)
                .ThenBy(endpoint => endpoint.Address, StringComparer.OrdinalIgnoreCase)
                .Select(endpoint => endpoint.Copy())
                .ToArray();
        }
    }

    public bool IsKnownEndpoint(string? identityId, IPAddress address)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId) || !IsUsableAddress(address)) return false;
        lock (_gate)
        {
            return _peers.TryGetValue(identityId, out var peer) && peer.Endpoints.ContainsKey(address.ToString());
        }
    }

    public bool TryGetIdentity(IPAddress address, out string identityId)
    {
        lock (_gate)
        {
            if (_endpointIndex.TryGetValue(address.ToString(), out var identities) && identities.Count == 1)
            {
                identityId = identities.First();
                return true;
            }
        }
        identityId = "";
        return false;
    }

    public void MarkEndpointHealthy(string identityId, PeerCandidateEndpoint endpoint)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId)) return;
        lock (_gate)
        {
            if (!TryGetEndpointLocked(identityId, endpoint.Address, out var existing)) return;
            existing.FailureScore = 0;
            existing.IsConfirmed = true;
            existing.LastSeenUtc = DateTimeOffset.UtcNow;
            existing.LastSuccessUtc = existing.LastSeenUtc;
            existing.Revision = NextRevision(existing.Revision);
        }
    }

    public void MarkEndpointUnhealthy(string identityId, PeerCandidateEndpoint endpoint)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId)) return;
        lock (_gate)
        {
            if (!TryGetEndpointLocked(identityId, endpoint.Address, out var existing)) return;
            existing.FailureScore = Math.Min(9, existing.FailureScore + 1);
            existing.IsConfirmed = false;
            existing.Revision = NextRevision(existing.Revision);
        }
    }

    public KnownPeerCache Export()
    {
        lock (_gate)
        {
            return new KnownPeerCache
            {
                Peers = _peers.Values
                    .OrderByDescending(peer => peer.LastSeenUtc)
                    .Take(MaxPeers)
                    .Select(peer => new KnownPeerIdentityRecord
                    {
                        IdentityId = peer.IdentityId.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase)
                            ? ""
                            : peer.IdentityId,
                        PlayerName = peer.PlayerName,
                        Endpoints = peer.Endpoints.Values.Select(endpoint => new KnownPeerEndpointRecord
                        {
                            Address = endpoint.Address,
                            ProviderId = endpoint.ProviderId,
                            InterfaceId = endpoint.InterfaceId,
                            NetworkType = endpoint.NetworkType,
                            LastSeenUtc = endpoint.LastSeenUtc,
                            LastSuccessUtc = endpoint.LastSuccessUtc,
                            IsObserved = endpoint.IsObserved,
                            IsConfirmed = endpoint.IsConfirmed,
                            FailureScore = endpoint.FailureScore,
                            Revision = endpoint.Revision
                        }).ToList()
                    }).ToList()
            };
        }
    }

    public void Load(KnownPeerCache? cache)
    {
        if (cache is null) return;
        lock (_gate)
        {
            _peers.Clear();
            _endpointIndex.Clear();
            foreach (var record in cache.Peers ?? [])
            {
                var usable = record.Endpoints
                    .Select(endpoint => (Record: endpoint, Address: ParseAddress(
                        string.IsNullOrWhiteSpace(endpoint.Address) ? endpoint.Ip : endpoint.Address)))
                    .Where(item => item.Address is not null && IsUsableAddress(item.Address))
                    .ToArray();
                if (usable.Length == 0) continue;

                var identityId = NormalizeIdentity(record.IdentityId);
                if (string.IsNullOrWhiteSpace(identityId)) identityId = $"legacy:{usable[0].Address}";
                var peer = new PeerRouteState(identityId)
                {
                    PlayerName = record.PlayerName?.Trim() ?? "",
                    LastSeenUtc = usable.Max(item => item.Record.LastSeenUtc)
                };

                foreach (var item in usable)
                {
                    var address = item.Address!;
                    var endpoint = new PeerCandidateEndpoint
                    {
                        Address = address.ToString(),
                        ProviderId = item.Record.ProviderId?.Trim() ?? "",
                        InterfaceId = item.Record.InterfaceId?.Trim() ?? "",
                        AddressFamily = address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                        NetworkType = VirtualNetworkService.NormalizeNetworkType(item.Record.NetworkType),
                        LastSeenUtc = item.Record.LastSeenUtc,
                        LastSuccessUtc = item.Record.LastSuccessUtc,
                        IsObserved = item.Record.IsObserved,
                        IsConfirmed = item.Record.IsConfirmed || item.Record.LastSuccessUtc != default,
                        FailureScore = Math.Clamp(item.Record.FailureScore, 0, 9),
                        Revision = Math.Max(0, item.Record.Revision)
                    };
                    peer.Endpoints[endpoint.Address] = endpoint;
                    AddEndpointIndexLocked(endpoint.Address, identityId);
                }
                _peers[identityId] = peer;
            }
            PruneLocked(DateTimeOffset.UtcNow);
        }
    }

    public void Prune(DateTimeOffset now)
    {
        lock (_gate) PruneLocked(now);
    }

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (var peer in _peers.Values.ToArray())
        {
            foreach (var endpoint in peer.Endpoints.Values
                         .Where(endpoint => now - endpoint.LastSeenUtc > EndpointTtl)
                         .ToArray())
            {
                peer.Endpoints.Remove(endpoint.Address);
                RemoveEndpointIndexLocked(endpoint.Address, peer.IdentityId);
            }
            if (peer.Endpoints.Count == 0 || now - peer.LastSeenUtc > PeerTtl) RemovePeerLocked(peer);
        }

        foreach (var peer in _peers.Values.OrderByDescending(item => item.LastSeenUtc).Skip(MaxPeers).ToArray())
        {
            RemovePeerLocked(peer);
        }
    }

    private static bool IsAllowedProvider(PeerCandidateEndpoint endpoint, string preferredProviderId)
    {
        if (string.IsNullOrWhiteSpace(preferredProviderId))
        {
            return string.IsNullOrWhiteSpace(endpoint.ProviderId) ||
                   string.Equals(endpoint.NetworkType, "LAN", StringComparison.OrdinalIgnoreCase);
        }
        return string.IsNullOrWhiteSpace(endpoint.ProviderId) ||
               string.Equals(endpoint.ProviderId, preferredProviderId, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetEndpointLocked(string identityId, string address, out PeerCandidateEndpoint endpoint)
    {
        endpoint = null!;
        return _peers.TryGetValue(identityId, out var peer) &&
               peer.Endpoints.TryGetValue(address.Trim(), out endpoint!);
    }

    private string? ResolveLegacyIdentityLocked(IEnumerable<IPAddress> addresses)
    {
        foreach (var address in addresses)
        {
            if (_endpointIndex.TryGetValue(address.ToString(), out var identities) && identities.Count == 1)
            {
                return identities.First();
            }
        }
        return null;
    }

    private void AddEndpointIndexLocked(string address, string identityId)
    {
        if (!_endpointIndex.TryGetValue(address, out var identities))
        {
            identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _endpointIndex.Add(address, identities);
        }
        identities.Add(identityId);
    }

    private void RemoveEndpointIndexLocked(string address, string identityId)
    {
        if (!_endpointIndex.TryGetValue(address, out var identities)) return;
        identities.Remove(identityId);
        if (identities.Count == 0) _endpointIndex.Remove(address);
    }

    private void RemovePeerLocked(PeerRouteState peer)
    {
        foreach (var endpoint in peer.Endpoints.Keys) RemoveEndpointIndexLocked(endpoint, peer.IdentityId);
        _peers.Remove(peer.IdentityId);
    }

    private static AddressFamily GetAddressFamily(PeerCandidateEndpoint endpoint) =>
        endpoint.AddressFamily.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;

    private static string NormalizeIdentity(string? identityId) => identityId?.Trim() ?? "";

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : second?.Trim() ?? "";

    private static long NextRevision(long current) =>
        current >= long.MaxValue ? long.MaxValue : Math.Max(1, current + 1);

    private static IPAddress? ParseAddress(string? value) =>
        TryGetUsableAddress(value, out var address) ? address : null;

    private static bool TryGetUsableAddress(string? value, out IPAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().Trim('[', ']', ',', ';', '"');
        var slash = normalized.IndexOf('/');
        if (slash > 0) normalized = normalized[..slash];
        return IPAddress.TryParse(normalized, out address!) && IsUsableAddress(address);
    }

    private static bool IsUsableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast || address.IsIPv6LinkLocal)
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 0 && !(bytes[0] == 169 && bytes[1] == 254);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private sealed class PeerRouteState
    {
        public PeerRouteState(string identityId)
        {
            IdentityId = identityId;
            LastSeenUtc = DateTimeOffset.UtcNow;
        }

        public string IdentityId { get; }
        public string PlayerName { get; set; } = "";
        public DateTimeOffset LastSeenUtc { get; set; }
        public Dictionary<string, PeerCandidateEndpoint> Endpoints { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
