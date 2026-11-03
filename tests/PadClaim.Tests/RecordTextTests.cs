using System.Collections.Immutable;

namespace PadClaim.Tests;

/// <summary>Pins the value equality and the printed text of the records that hold arrays of buttons or rules.</summary>
public sealed class RecordTextTests
{
    private static readonly DeviceIdentity Pad = new(0x045E, 0x028E, "Pad 0", null);

    /// <summary>Snapshots whose suspect buttons hold the same buttons in separately built arrays are equal.</summary>
    [Fact]
    public void SnapshotsWithEqualButtonsAreEqual()
    {
        DeviceSnapshot first = Snapshot([2]);
        DeviceSnapshot second = Snapshot([2]);

        Assert.True(first.Equals(second));
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    /// <summary>A rule with unset blocked buttons is not equal to one with none, and comparing them does not throw.</summary>
    [Fact]
    public void DefaultAndEmptyButtonsDiffer()
    {
        DeviceRule unset = new(Pad, DeviceVerdict.Blocked, default);
        DeviceRule empty = new(Pad, DeviceVerdict.Blocked, []);

        Assert.False(unset.Equals(empty));
        Assert.False(unset == empty);
    }

    /// <summary>A rule prints its blocked buttons as their values.</summary>
    [Fact]
    public void DeviceRulePrintsItsButtons()
    {
        DeviceRule rule = new(Pad, DeviceVerdict.Blocked, [3, 5]);

        Assert.Contains("BlockedButtons = [3, 5]", rule.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A snapshot prints its suspect buttons as their values, and an event holding it prints the same text.</summary>
    [Fact]
    public void SnapshotPrintsItsButtons()
    {
        DeviceSnapshot snapshot = Snapshot([2]);

        Assert.Contains("SuspectButtons = [2]", snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("SuspectButtons = [2]", new DeviceAdded(snapshot).ToString(), StringComparison.Ordinal);
    }

    /// <summary>Settings print their rules as records, not as the array's type name.</summary>
    [Fact]
    public void SettingsPrintsItsRules()
    {
        PadClaimSettings settings = new([new DeviceRule(Pad, DeviceVerdict.None, [3])]);

        Assert.Contains("Rules = [DeviceRule {", settings.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Records print an array that was never initialised as default rather than as an empty one.</summary>
    [Fact]
    public void DefaultButtonsPrintAsDefault()
    {
        DeviceRule rule = new(Pad, DeviceVerdict.Blocked, default);
        DeviceSnapshot snapshot = new(0, Pad, DeviceStatus.Ready, SuspectReason.None, default, Trusted: false, Unlisted: false);
        PadClaimSettings settings = new(default);

        Assert.Contains("BlockedButtons = default", rule.ToString(), StringComparison.Ordinal);
        Assert.Contains("SuspectButtons = default", snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("Rules = default", settings.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Snapshots that differ only in their suspect buttons are not equal.</summary>
    [Fact]
    public void SnapshotsWithDifferentButtonsDiffer()
    {
        DeviceSnapshot first = Snapshot([2]);
        DeviceSnapshot second = Snapshot([3]);

        Assert.False(first.Equals(second));
        Assert.False(first == second);
    }

    /// <summary>Settings whose rules array was never initialised are not equal to ones with no rules, and comparing them does not throw.</summary>
    [Fact]
    public void DefaultAndEmptyRulesDiffer()
    {
        PadClaimSettings unset = new(default);
        PadClaimSettings empty = new([]);

        Assert.False(unset.Equals(empty));
        Assert.False(unset == empty);
    }

    private static DeviceSnapshot Snapshot(ImmutableArray<int> suspectButtons) =>
        new(0, Pad, DeviceStatus.Suspect, SuspectReason.StuckButton, suspectButtons, Trusted: false, Unlisted: false);
}
