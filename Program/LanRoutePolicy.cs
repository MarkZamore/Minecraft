namespace Minecraft;

internal static class LanRoutePolicy
{
    public static LanRouteMode Observe(
        LanRouteState state,
        DateTimeOffset now,
        bool localSupportsDirectIpv4,
        bool hasConfirmedIpv4,
        bool hasConfirmedIpv6,
        TimeSpan stabilizationDelay)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.LastSeenUtc = now;
        state.ObservationCount++;
        if (state.Mode != LanRouteMode.Pending) return state.Mode;

        if (hasConfirmedIpv4)
        {
            state.Mode = LanRouteMode.Direct;
        }
        else if (!localSupportsDirectIpv4 &&
                 hasConfirmedIpv6 &&
                 now - state.FirstSeenUtc >= stabilizationDelay &&
                 state.ObservationCount >= 3)
        {
            state.Mode = LanRouteMode.Relay;
        }
        return state.Mode;
    }
}

internal sealed class LanRouteState(DateTimeOffset firstSeenUtc)
{
    public DateTimeOffset FirstSeenUtc { get; } = firstSeenUtc;
    public DateTimeOffset LastSeenUtc { get; set; } = firstSeenUtc;
    public int ObservationCount { get; set; }
    public LanRouteMode Mode { get; set; }
    public string RelayKey { get; set; } = "";
}

internal enum LanRouteMode
{
    Pending,
    Direct,
    Relay
}
