using System.Collections.Immutable;
using System.Text;

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

/// <summary>
/// A decision about one pad, matched to it by <see cref="DeviceIdentity.Matches"/>.
/// Equality and the text compare and print the array's elements, in order.
/// </summary>
/// <param name="Device">The pad the rule is about.</param>
/// <param name="Verdict">What the player decided about the pad.</param>
/// <param name="BlockedButtons">The pad's buttons that never count, by Godot's button index.</param>
public sealed record DeviceRule(DeviceIdentity Device, DeviceVerdict Verdict, ImmutableArray<int> BlockedButtons)
{
    /// <summary>Tells whether another rule holds equal members, comparing the blocked buttons by their values in order.</summary>
    /// <param name="other">The rule to compare with.</param>
    /// <returns>True when every member is equal.</returns>
    public bool Equals(DeviceRule? other) =>
        ReferenceEquals(this, other)
        || (
            other is not null
            && EqualityComparer<DeviceIdentity>.Default.Equals(Device, other.Device)
            && Verdict == other.Verdict
            && ArrayValues.Same(BlockedButtons, other.BlockedButtons)
        );

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Device);
        hash.Add(Verdict);
        ArrayValues.AddTo(ref hash, BlockedButtons);
        return hash.ToHashCode();
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Device = ").Append(Device).Append(", Verdict = ").Append(Verdict).Append(", BlockedButtons = ");
        ArrayValues.Append(builder, BlockedButtons);
        return true;
    }
}

/// <summary>
/// The player's decisions about pads, as the host keeps them between runs.
/// Equality and the text compare and print the array's elements, in order.
/// </summary>
/// <param name="Rules">One rule per pad.</param>
public sealed record PadClaimSettings(ImmutableArray<DeviceRule> Rules)
{
    /// <summary>No rules: every pad is treated by the noise checks alone.</summary>
    public static PadClaimSettings Empty { get; } = new([]);

    /// <summary>Tells whether another settings holds equal rules, comparing them by their values in order.</summary>
    /// <param name="other">The settings to compare with.</param>
    /// <returns>True when the rules are equal.</returns>
    public bool Equals(PadClaimSettings? other) => ReferenceEquals(this, other) || (other is not null && ArrayValues.Same(Rules, other.Rules));

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        ArrayValues.AddTo(ref hash, Rules);
        return hash.ToHashCode();
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Rules = ");
        ArrayValues.Append(builder, Rules);
        return true;
    }
}
