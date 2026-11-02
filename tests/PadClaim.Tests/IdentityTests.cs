namespace PadClaim.Tests;

/// <summary>
/// Pins the identity-matching decision: the ids when both sides know them, the name otherwise, and the serial
/// only when both sides carry one.
/// </summary>
public sealed class IdentityTests
{
    /// <summary>A pad keeps its rule when the backend renames it, as long as the ids agree.</summary>
    [Fact]
    public void DeviceWithoutSerialMatchesOnTheRest()
    {
        DeviceIdentity blocked = new(0x045E, 0x028E, "Xbox 360 Controller", null);
        DeviceIdentity connected = new(0x045E, 0x028E, "XInput Controller", "ABC");

        Assert.True(blocked.Matches(connected));
    }

    /// <summary>Two pads with the same ids but different serials are different pads.</summary>
    [Fact]
    public void DifferentSerialsDoNotMatch()
    {
        DeviceIdentity first = new(0x045E, 0x028E, "Xbox 360 Controller", "A");
        DeviceIdentity second = new(0x045E, 0x028E, "Xbox 360 Controller", "B");

        Assert.False(first.Matches(second));
    }

    /// <summary>A different product id is a different pad, whatever the name says.</summary>
    [Fact]
    public void DifferentProductDoesNotMatch()
    {
        DeviceIdentity first = new(0x045E, 0x028E, "Xbox 360 Controller", null);
        DeviceIdentity second = new(0x045E, 0x02FF, "Xbox 360 Controller", null);

        Assert.False(first.Matches(second));
    }

    /// <summary>Without ids on either side the name decides.</summary>
    [Fact]
    public void UnknownIdsMatchOnName()
    {
        DeviceIdentity panel = new(0, 0, "Panel", null);

        Assert.True(panel.Matches(new DeviceIdentity(0, 0, "Panel", null)));
        Assert.False(panel.Matches(new DeviceIdentity(0, 0, "Other", null)));
    }

    /// <summary>When one side's ids are unknown, the name decides for both.</summary>
    [Fact]
    public void OneSideUnknownIdsMatchOnName()
    {
        DeviceIdentity known = new(0x045E, 0x028E, "Pad", null);
        DeviceIdentity unknown = new(0, 0, "Pad", null);

        Assert.True(known.Matches(unknown));
    }

    /// <summary>Every key the adapter hands over lands in its own field.</summary>
    [Fact]
    public void FromJoyInfoReadsAllFields()
    {
        Dictionary<string, string> info = new()
        {
            ["raw_name"] = "Xbox 360 Controller",
            ["vendor_id"] = "1118",
            ["product_id"] = "654",
            ["serial_number"] = "S1",
        };

        var identity = DeviceIdentity.FromJoyInfo(info, "fallback");

        Assert.Equal(new DeviceIdentity(1118, 654, "Xbox 360 Controller", "S1"), identity);
    }

    /// <summary>A missing key is a zero id or a null serial; an unparsable id is zero too.</summary>
    [Fact]
    public void FromJoyInfoReadsMissingKeysAsZeroAndNull()
    {
        var empty = DeviceIdentity.FromJoyInfo(new Dictionary<string, string>(), "Joypad 3");
        Assert.Equal(new DeviceIdentity(0, 0, "Joypad 3", null), empty);

        Dictionary<string, string> junk = new() { ["vendor_id"] = "x", ["serial_number"] = "" };
        var parsed = DeviceIdentity.FromJoyInfo(junk, "Joypad 4");
        Assert.Equal(0, parsed.VendorId);
        Assert.Null(parsed.Serial);
    }
}
