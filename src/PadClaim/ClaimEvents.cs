using System.Collections.Immutable;

namespace PadClaim;

/// <summary>Where a connected pad stands in the claim process.</summary>
public enum DeviceStatus
{
    /// <summary>Waiting out the quiet window after connecting.</summary>
    Settling,

    /// <summary>Still for the quiet window: the pad may start a claim.</summary>
    Ready,

    /// <summary>A first button was released with a human hold; a different second button confirms the claim.</summary>
    AwaitingConfirm,

    /// <summary>The pad holds the seat.</summary>
    Claimed,

    /// <summary>The pad behaved like a self-reporting device and is never offered unless trusted.</summary>
    Suspect,

    /// <summary>The player blocked the pad.</summary>
    Blocked,

    /// <summary>The engine has no gamepad layout for the device, and the host requires one.</summary>
    NotAGamepad,
}

/// <summary>Why a pad is suspect; several reasons may hold at once.</summary>
[Flags]
public enum SuspectReason
{
    /// <summary>The pad is not suspect.</summary>
    None = 0,

    /// <summary>A button was down at the end of the quiet window, or held past the stuck limit.</summary>
    StuckButton = 1,

    /// <summary>A button pressed itself at a near-constant period.</summary>
    RepeatingButton = 2,

    /// <summary>A button was released faster than a human can press.</summary>
    SubHumanToggle = 4,

    /// <summary>An axis kept moving through the quiet window.</summary>
    JitteryAxis = 8,

    /// <summary>The pad never stood still for the quiet window.</summary>
    NeverSettled = 16,
}

/// <summary>Why a started claim ended without a claim attempt.</summary>
public enum CancelReason
{
    /// <summary>No second button confirmed within the confirm window.</summary>
    Timeout,

    /// <summary>The pad disconnected.</summary>
    Disconnected,

    /// <summary>The pad became suspect.</summary>
    DeviceSuspect,
}

/// <summary>Why a completed gesture did not take the seat.</summary>
public enum RefuseReason
{
    /// <summary>Another pad holds the seat.</summary>
    SeatBusy,

    /// <summary>The host closed claiming.</summary>
    ClaimsClosed,
}

/// <summary>Why the seat holder lost the seat.</summary>
public enum SeatLossReason
{
    /// <summary>The pad disconnected.</summary>
    Disconnected,

    /// <summary>The host released the seat.</summary>
    Released,

    /// <summary>The player blocked the pad.</summary>
    Blocked,
}

/// <summary>Where an axis rested during the quiet window.</summary>
/// <param name="Centre">The axis's value at rest.</param>
/// <param name="Spread">The largest distance from the centre the axis reached while still.</param>
/// <param name="Known">Whether the engine sent a value for the axis; until it does, the value read at connect may be wrong.</param>
public sealed record AxisRest(float Centre, float Spread, bool Known);

/// <summary>What the claimer knows about one connected pad.</summary>
/// <param name="Device">The engine's device index for this connection.</param>
/// <param name="Identity">Who the pad is.</param>
/// <param name="Status">Where the pad stands in the claim process.</param>
/// <param name="Reasons">Why the pad is suspect, or none.</param>
/// <param name="SuspectButtons">The buttons that made the pad suspect.</param>
/// <param name="Trusted">Whether the player vouched for the pad.</param>
/// <param name="Unlisted">Whether the pad sent input without ever being seen to connect.</param>
public sealed record DeviceSnapshot(
    int Device,
    DeviceIdentity Identity,
    DeviceStatus Status,
    SuspectReason Reasons,
    ImmutableArray<int> SuspectButtons,
    bool Trusted,
    bool Unlisted
);

/// <summary>Something the claimer decided, returned to the host in the order it happened.</summary>
public abstract record ClaimEvent;

/// <summary>A pad connected.</summary>
/// <param name="Snapshot">The pad as it connected.</param>
public sealed record DeviceAdded(DeviceSnapshot Snapshot) : ClaimEvent;

/// <summary>A pad's status changed outside the claim gesture itself.</summary>
/// <param name="Snapshot">The pad after the change.</param>
public sealed record DeviceChanged(DeviceSnapshot Snapshot) : ClaimEvent;

/// <summary>A pad disconnected.</summary>
/// <param name="Device">The pad's device index.</param>
public sealed record DeviceRemoved(int Device) : ClaimEvent;

/// <summary>A pad released a first button with a human hold and now waits for a different second button.</summary>
/// <param name="Device">The pad's device index.</param>
/// <param name="FirstButton">The button that started the claim.</param>
public sealed record ClaimStarted(int Device, int FirstButton) : ClaimEvent;

/// <summary>A started claim ended without a claim attempt.</summary>
/// <param name="Device">The pad's device index.</param>
/// <param name="Reason">Why the claim ended.</param>
public sealed record ClaimCancelled(int Device, CancelReason Reason) : ClaimEvent;

/// <summary>A pad took the vacant seat.</summary>
/// <param name="Device">The pad's device index.</param>
public sealed record SeatClaimed(int Device) : ClaimEvent;

/// <summary>A pad took the seat from a holder that had been idle for the steal time; the former holder is ready again.</summary>
/// <param name="From">The former holder's device index.</param>
/// <param name="To">The new holder's device index.</param>
public sealed record SeatMoved(int From, int To) : ClaimEvent;

/// <summary>The seat holder that disconnected came back and, once settled, got the vacant seat back by itself.</summary>
/// <param name="Device">The returning pad's new device index.</param>
public sealed record SeatRestored(int Device) : ClaimEvent;

/// <summary>A pad completed the gesture but did not get the seat.</summary>
/// <param name="Device">The pad's device index.</param>
/// <param name="Reason">Why the seat was refused.</param>
public sealed record ClaimRefused(int Device, RefuseReason Reason) : ClaimEvent;

/// <summary>The seat holder lost the seat, which is now vacant.</summary>
/// <param name="Device">The former holder's device index.</param>
/// <param name="Reason">Why the seat was lost.</param>
public sealed record SeatLost(int Device, SeatLossReason Reason) : ClaimEvent;

/// <summary>The player's decisions about pads changed; the host saves them.</summary>
/// <param name="Settings">The decisions after the change.</param>
public sealed record SettingsChanged(PadClaimSettings Settings) : ClaimEvent;
