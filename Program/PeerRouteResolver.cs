using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public interface IPeerRouteResolver
{
    IReadOnlyList<PeerCandidateEndpoint> GetSendCandidates(
        string? identityId,
        string? preferredProviderId = null,
        AddressFamily? addressFamily = null);

    bool IsKnownEndpoint(
        string? identityId,
        IPAddress address,
        string? providerId,
        string? interfaceId);

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
        FailureScore = FailureScore
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

public sealed record PeerDiscoveryCandidate(string IdentityId, PeerCandidateEndpoint Endpoint);

public sealed record PeerDiscoveryBatch(
    IReadOnlyList<PeerDiscoveryCandidate> Candidates,
    int NextCursor);

public sealed class PeerRouteResolver : IPeerRouteResolver
{
    private static readonly TimeSpan ConfirmedEndpointTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan ObservedEndpointTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan CandidateEndpointTtl = TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, PeerRouteState> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private PeerDiscoveryCandidate[] _discoverySnapshot = [];
    private bool _discoverySnapshotDirty = true;

    public void UpsertFromAnnouncement(PeerAnnouncement announcement, IPAddress? observedAddress = null)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        if (!Guid.TryParse(announcement.IdentityId, out var identity)) return;

        var identityId = identity.ToString("D");
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

        lock (_gate)
        {
            if (!_peers.TryGetValue(identityId, out var peer))
            {
                peer = new PeerRouteState(identityId);
                _peers.Add(identityId, peer);
            }

            peer.PlayerName = announcement.PlayerName?.Trim() ?? "";
            peer.LastSeenUtc = now;

            foreach (var item in advertised
                         .GroupBy(candidate => BuildEndpointKey(
                                 candidate.Address,
                                 candidate.Metadata.ProviderId,
                                 candidate.Metadata.InterfaceId),
                             StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.OrderByDescending(candidate => candidate.Observed).First()))
            {
                var providerId = FirstNonEmpty(item.Metadata.ProviderId, announcement.NetworkProviderId);
                var interfaceId = FirstNonEmpty(item.Metadata.InterfaceId, announcement.NetworkInterfaceId);
                var key = BuildEndpointKey(item.Address, providerId, interfaceId);
                if (!peer.Endpoints.TryGetValue(key, out var endpoint))
                {
                    endpoint = new PeerCandidateEndpoint { Address = item.Address.ToString() };
                    peer.Endpoints.Add(key, endpoint);
                }

                endpoint.ProviderId = providerId;
                endpoint.InterfaceId = interfaceId;
                endpoint.AddressFamily = item.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
                endpoint.NetworkType = VirtualNetworkService.NormalizeNetworkType(
                    FirstNonEmpty(item.Metadata.NetworkType, announcement.NetworkType));
                endpoint.LastSeenUtc = now;
                endpoint.IsObserved |= item.Observed;
                if (item.Observed)
                {
                    endpoint.IsConfirmed = true;
                    endpoint.FailureScore = 0;
                }
            }

            _discoverySnapshotDirty = true;
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
            var now = DateTimeOffset.UtcNow;
            return peer.Endpoints.Values
                .Where(endpoint => !IsExpired(endpoint, now))
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

    public PeerDiscoveryBatch GetDiscoveryBatch(
        NetworkEndpointInfo localEndpoint,
        int cursor,
        int maxCount)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        maxCount = Math.Max(0, maxCount);
        if (maxCount == 0) return new PeerDiscoveryBatch([], Math.Max(0, cursor));

        lock (_gate)
        {
            PruneLocked(DateTimeOffset.UtcNow);
            EnsureDiscoverySnapshotLocked();
            if (_discoverySnapshot.Length == 0) return new PeerDiscoveryBatch([], 0);

            var normalizedCursor = (cursor & int.MaxValue) % _discoverySnapshot.Length;
            var result = new List<PeerDiscoveryCandidate>(Math.Min(maxCount, _discoverySnapshot.Length));
            var visited = 0;
            while (visited < _discoverySnapshot.Length && result.Count < maxCount)
            {
                var candidate = _discoverySnapshot[(normalizedCursor + visited) % _discoverySnapshot.Length];
                if (MatchesLocalEndpoint(candidate.Endpoint, localEndpoint))
                {
                    result.Add(new PeerDiscoveryCandidate(candidate.IdentityId, candidate.Endpoint.Copy()));
                }
                visited++;
            }

            return new PeerDiscoveryBatch(
                result,
                (normalizedCursor + Math.Max(1, visited)) % _discoverySnapshot.Length);
        }
    }

    public bool IsKnownEndpoint(
        string? identityId,
        IPAddress address,
        string? providerId,
        string? interfaceId)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId) || !IsUsableAddress(address)) return false;
        lock (_gate)
        {
            if (!_peers.TryGetValue(identityId, out var peer)) return false;
            var key = BuildEndpointKey(address, providerId, interfaceId);
            return peer.Endpoints.ContainsKey(key);
        }
    }

    public void MarkEndpointHealthy(string identityId, PeerCandidateEndpoint endpoint)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId)) return;
        lock (_gate)
        {
            if (!TryGetEndpointLocked(identityId, endpoint, out var existing)) return;
            existing.FailureScore = 0;
            existing.IsConfirmed = true;
            existing.LastSeenUtc = DateTimeOffset.UtcNow;
            existing.LastSuccessUtc = existing.LastSeenUtc;
            _discoverySnapshotDirty = true;
        }
    }

    public void MarkEndpointUnhealthy(string identityId, PeerCandidateEndpoint endpoint)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId)) return;
        lock (_gate)
        {
            if (!TryGetEndpointLocked(identityId, endpoint, out var existing)) return;
            existing.FailureScore = Math.Min(9, existing.FailureScore + 1);
            existing.IsConfirmed = false;
            _discoverySnapshotDirty = true;
        }
    }

    public KnownPeerCache Export()
    {
        lock (_gate)
        {
            PruneLocked(DateTimeOffset.UtcNow);
            return new KnownPeerCache
            {
                Peers = _peers.Values
                    .OrderByDescending(peer => peer.LastSeenUtc)
                    .Select(peer => new KnownPeerIdentityRecord
                    {
                        IdentityId = peer.IdentityId,
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
                            FailureScore = endpoint.FailureScore
                        }).ToList()
                    }).ToList()
            };
        }
    }

    public void Load(KnownPeerCache? cache)
    {
        if (cache is null || cache.SchemaVersion != 4) return;
        lock (_gate)
        {
            _peers.Clear();
            foreach (var record in cache.Peers ?? [])
            {
                if (!Guid.TryParse(record.IdentityId, out var identity)) continue;
                var identityId = identity.ToString("D");
                var peer = new PeerRouteState(identityId)
                {
                    PlayerName = record.PlayerName?.Trim() ?? ""
                };

                foreach (var stored in record.Endpoints ?? [])
                {
                    var address = ParseAddress(stored.Address);
                    if (address is null) continue;
                    var endpoint = new PeerCandidateEndpoint
                    {
                        Address = address.ToString(),
                        ProviderId = stored.ProviderId?.Trim() ?? "",
                        InterfaceId = stored.InterfaceId?.Trim() ?? "",
                        AddressFamily = address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                        NetworkType = VirtualNetworkService.NormalizeNetworkType(stored.NetworkType),
                        LastSeenUtc = stored.LastSeenUtc,
                        LastSuccessUtc = stored.LastSuccessUtc,
                        IsObserved = stored.IsObserved,
                        IsConfirmed = stored.IsConfirmed || stored.LastSuccessUtc != default,
                        FailureScore = Math.Clamp(stored.FailureScore, 0, 9)
                    };
                    peer.Endpoints[BuildEndpointKey(address, endpoint.ProviderId, endpoint.InterfaceId)] = endpoint;
                }

                if (peer.Endpoints.Count == 0) continue;
                peer.LastSeenUtc = peer.Endpoints.Values.Max(endpoint => endpoint.LastSeenUtc);
                _peers[identityId] = peer;
            }
            _discoverySnapshotDirty = true;
            PruneLocked(DateTimeOffset.UtcNow);
        }
    }

    public void Prune(DateTimeOffset now)
    {
        lock (_gate) PruneLocked(now);
    }

    private void PruneLocked(DateTimeOffset now)
    {
        var changed = false;
        foreach (var peer in _peers.Values.ToArray())
        {
            foreach (var pair in peer.Endpoints
                         .Where(pair => IsExpired(pair.Value, now))
                         .ToArray())
            {
                peer.Endpoints.Remove(pair.Key);
                changed = true;
            }
            if (peer.Endpoints.Count == 0)
            {
                _peers.Remove(peer.IdentityId);
                changed = true;
            }
        }
        if (changed) _discoverySnapshotDirty = true;
    }

    private void EnsureDiscoverySnapshotLocked()
    {
        if (!_discoverySnapshotDirty) return;
        var now = DateTimeOffset.UtcNow;
        _discoverySnapshot = _peers.Values
            .SelectMany(peer => peer.Endpoints.Values
                .Where(endpoint => !IsExpired(endpoint, now))
                .Select(endpoint => new PeerDiscoveryCandidate(peer.IdentityId, endpoint.Copy())))
            .OrderByDescending(candidate => candidate.Endpoint.IsConfirmed)
            .ThenByDescending(candidate => candidate.Endpoint.LastSuccessUtc)
            .ThenByDescending(candidate => candidate.Endpoint.LastSeenUtc)
            .ThenBy(candidate => candidate.IdentityId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Endpoint.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _discoverySnapshotDirty = false;
    }

    private static bool MatchesLocalEndpoint(
        PeerCandidateEndpoint candidate,
        NetworkEndpointInfo localEndpoint)
    {
        if (GetAddressFamily(candidate) != localEndpoint.AddressFamily) return false;
        if (!string.IsNullOrWhiteSpace(localEndpoint.ProviderId))
        {
            return string.Equals(candidate.ProviderId, localEndpoint.ProviderId, StringComparison.OrdinalIgnoreCase);
        }
        return localEndpoint.IsPhysical &&
               (string.IsNullOrWhiteSpace(candidate.ProviderId) ||
                string.Equals(candidate.NetworkType, "LAN", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExpired(PeerCandidateEndpoint endpoint, DateTimeOffset now)
    {
        var ttl = endpoint.IsConfirmed || endpoint.LastSuccessUtc != default
            ? ConfirmedEndpointTtl
            : endpoint.IsObserved
                ? ObservedEndpointTtl
                : CandidateEndpointTtl;
        return now - endpoint.LastSeenUtc > ttl;
    }

    private static bool IsAllowedProvider(PeerCandidateEndpoint endpoint, string preferredProviderId)
    {
        if (string.IsNullOrWhiteSpace(preferredProviderId))
        {
            return string.IsNullOrWhiteSpace(endpoint.ProviderId) ||
                   string.Equals(endpoint.NetworkType, "LAN", StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(endpoint.ProviderId, preferredProviderId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(endpoint.NetworkType, "LAN", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetEndpointLocked(
        string identityId,
        PeerCandidateEndpoint candidate,
        out PeerCandidateEndpoint endpoint)
    {
        endpoint = null!;
        if (!_peers.TryGetValue(identityId, out var peer) ||
            !IPAddress.TryParse(candidate.Address, out var address))
        {
            return false;
        }

        var key = BuildEndpointKey(address, candidate.ProviderId, candidate.InterfaceId);
        return peer.Endpoints.TryGetValue(key, out endpoint!);
    }

    private static string BuildEndpointKey(
        IPAddress address,
        string? providerId,
        string? interfaceId) =>
        string.Join("|",
            providerId?.Trim().ToLowerInvariant() ?? "",
            interfaceId?.Trim().ToLowerInvariant() ?? "",
            address.ToString().ToLowerInvariant());

    private static AddressFamily GetAddressFamily(PeerCandidateEndpoint endpoint) =>
        endpoint.AddressFamily.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;

    private static string NormalizeIdentity(string? identityId) =>
        Guid.TryParse(identityId, out var identity) ? identity.ToString("D") : "";

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : second?.Trim() ?? "";

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
