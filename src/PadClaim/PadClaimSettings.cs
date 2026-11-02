using System.Collections.Immutable;

namespace PadClaim;

/// <summary>What the player decided about a pad.</summary>
public enum DeviceVerdict
{
    /// <summary>No decision: the pad may claim like any other.</summary>
    None,

    /// <summary>The pad may never claim.</summary>
    Blocked,

    /// <summary>The pad skips the noise checks: the player has vouched for it.</summary>
    Trusted,
}

/// <summary>A decision about one pad, matched to it by <see cref="DeviceIdentity.Matches"/>.</summary>
/// <param name="Device">The pad the rule is about.</param>
/// <param name="Verdict">What the player decided about the pad.</param>
/// <param name="BlockedButtons">The pad's buttons that never count, by Godot's button index.</param>
public sealed record DeviceRule(DeviceIdentity Device, DeviceVerdict Verdict, ImmutableArray<int> BlockedButtons);

/// <summary>The player's decisions about pads, as the host keeps them between runs.</summary>
/// <param name="Rules">One rule per pad.</param>
public sealed record PadClaimSettings(ImmutableArray<DeviceRule> Rules)
{
    /// <summary>No rules: every pad is treated by the noise checks alone.</summary>
    public static PadClaimSettings Empty { get; } = new([]);
}
