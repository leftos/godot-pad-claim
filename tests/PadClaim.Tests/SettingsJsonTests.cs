namespace PadClaim.Tests;

/// <summary>
/// Pins the saved settings format: camelCase JSON at schemaVersion 1, and a refusal rather than a throw for
/// malformed input.
/// </summary>
public sealed class SettingsJsonTests
{
    /// <summary>Every field of every rule survives a write and a read.</summary>
    [Fact]
    public void RoundTripsEveryField()
    {
        PadClaimSettings settings = new([
            new DeviceRule(new DeviceIdentity(1118, 654, "Pad A", "S1"), DeviceVerdict.Blocked, [7, 9]),
            new DeviceRule(new DeviceIdentity(0, 0, "Pad B", null), DeviceVerdict.Trusted, []),
            new DeviceRule(new DeviceIdentity(1118, 767, "Pad C", null), DeviceVerdict.None, [0]),
        ]);

        string json = PadClaimSettingsJson.Write(settings);
        bool read = PadClaimSettingsJson.TryRead(json, out PadClaimSettings? actual, out string? error);

        Assert.True(read, error);
        Assert.NotNull(actual);
        Assert.Equal(settings.Rules.Length, actual.Rules.Length);
        for (int index = 0; index < settings.Rules.Length; index++)
        {
            DeviceRule expected = settings.Rules[index];
            DeviceRule got = actual.Rules[index];
            Assert.Equal(expected.Device, got.Device);
            Assert.Equal(expected.Verdict, got.Verdict);
            Assert.Equal<int>(expected.BlockedButtons, got.BlockedButtons);
        }
    }

    /// <summary>The document carries schemaVersion 1 and camelCase names.</summary>
    [Fact]
    public void WritesSchemaVersionOne()
    {
        PadClaimSettings settings = new([new DeviceRule(new DeviceIdentity(1118, 654, "Pad", null), DeviceVerdict.Blocked, [7])]);

        string json = PadClaimSettingsJson.Write(settings);

        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"rules\":[", json, StringComparison.Ordinal);
        Assert.Contains("\"vendorId\":1118", json, StringComparison.Ordinal);
        Assert.Contains("\"productId\":654", json, StringComparison.Ordinal);
        Assert.Contains("\"blockedButtons\":[7]", json, StringComparison.Ordinal);
        Assert.Contains("\"verdict\":\"Blocked\"", json, StringComparison.Ordinal);
    }

    /// <summary>A version this library cannot read is refused, and the error names it.</summary>
    [Fact]
    public void RejectsUnknownSchemaVersion()
    {
        bool read = PadClaimSettingsJson.TryRead("{\"schemaVersion\":2,\"rules\":[]}", out PadClaimSettings? settings, out string? error);

        Assert.False(read);
        Assert.Null(settings);
        Assert.NotNull(error);
        Assert.Contains("2", error, StringComparison.Ordinal);
    }

    /// <summary>Broken JSON is refused rather than thrown.</summary>
    [Fact]
    public void RejectsMalformedJson()
    {
        bool read = PadClaimSettingsJson.TryRead("{\"schemaVersion\":", out PadClaimSettings? settings, out string? error);

        Assert.False(read);
        Assert.Null(settings);
        Assert.NotNull(error);
    }

    /// <summary>A document without a rules array is refused.</summary>
    [Fact]
    public void RejectsMissingRules()
    {
        bool read = PadClaimSettingsJson.TryRead("{\"schemaVersion\":1}", out PadClaimSettings? settings, out _);

        Assert.False(read);
        Assert.Null(settings);
    }

    /// <summary>A rule written without blockedButtons reads as a rule that blocks no button.</summary>
    [Fact]
    public void MissingBlockedButtonsReadsEmpty()
    {
        const string json = """
            {"schemaVersion":1,"rules":[{"device":{"vendorId":1118,"productId":654,"name":"Pad","serial":null},"verdict":"Blocked"}]}
            """;

        bool read = PadClaimSettingsJson.TryRead(json, out PadClaimSettings? settings, out string? error);

        Assert.True(read, error);
        Assert.NotNull(settings);
        DeviceRule rule = Assert.Single(settings.Rules);
        Assert.True(rule.BlockedButtons.IsEmpty);
    }

    /// <summary>Blocked buttons read back distinct and in ascending order.</summary>
    [Fact]
    public void ReadDeduplicatesAndSortsBlockedButtons()
    {
        const string json = """
            {"schemaVersion":1,"rules":[{"device":{"vendorId":1118,"productId":654,"name":"Pad"},"verdict":"None","blockedButtons":[9,7,9]}]}
            """;

        bool read = PadClaimSettingsJson.TryRead(json, out PadClaimSettings? settings, out string? error);

        Assert.True(read, error);
        Assert.NotNull(settings);
        Assert.Equal<int>([7, 9], Assert.Single(settings.Rules).BlockedButtons);
    }

    /// <summary>A null entry in the rules array is refused rather than thrown.</summary>
    [Fact]
    public void RejectsNullRule()
    {
        bool read = PadClaimSettingsJson.TryRead("{\"schemaVersion\":1,\"rules\":[null]}", out PadClaimSettings? settings, out string? error);

        Assert.False(read);
        Assert.Null(settings);
        Assert.NotNull(error);
    }

    /// <summary>A device with a missing or null name is refused: the name is what matches a pad without ids.</summary>
    [Theory]
    [InlineData("{\"vendorId\":1118,\"productId\":654,\"serial\":null}")]
    [InlineData("{\"vendorId\":1118,\"productId\":654,\"name\":null,\"serial\":null}")]
    public void RejectsDeviceWithoutName(string device)
    {
        string json = $"{{\"schemaVersion\":1,\"rules\":[{{\"device\":{device},\"verdict\":\"Blocked\"}}]}}";

        bool read = PadClaimSettingsJson.TryRead(json, out PadClaimSettings? settings, out string? error);

        Assert.False(read);
        Assert.Null(settings);
        Assert.NotNull(error);
    }

    /// <summary>A verdict that is a number, or a name the enum does not have, is refused.</summary>
    [Theory]
    [InlineData("7")]
    [InlineData("\"Maybe\"")]
    public void RejectsUndefinedVerdict(string verdict)
    {
        string json = $"{{\"schemaVersion\":1,\"rules\":[{{\"device\":{{\"vendorId\":1,\"productId\":2,\"name\":\"Pad\"}},\"verdict\":{verdict}}}]}}";

        bool read = PadClaimSettingsJson.TryRead(json, out PadClaimSettings? settings, out string? error);

        Assert.False(read);
        Assert.Null(settings);
        Assert.NotNull(error);
    }

    /// <summary>An empty saved serial reads as no serial, as <see cref="DeviceIdentity.FromJoyInfo"/> reads a live pad's.</summary>
    [Fact]
    public void EmptySerialReadsAsNull()
    {
        const string json = """
            {"schemaVersion":1,"rules":[{"device":{"vendorId":1118,"productId":654,"name":"Pad","serial":""},"verdict":"Blocked"}]}
            """;

        bool read = PadClaimSettingsJson.TryRead(json, out PadClaimSettings? settings, out string? error);

        Assert.True(read, error);
        Assert.NotNull(settings);
        Assert.Null(Assert.Single(settings.Rules).Device.Serial);
    }

    /// <summary>Writing settings whose rules array was never initialised is a caller bug and throws.</summary>
    [Fact]
    public void WriteRejectsDefaultRules()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() => PadClaimSettingsJson.Write(new PadClaimSettings(default)));

        Assert.Contains("Rules", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Writing a rule with no device is a caller bug and throws, rather than writing what the reader refuses.</summary>
    [Fact]
    public void WriteRejectsNullDevice()
    {
        PadClaimSettings settings = new([new DeviceRule(null!, DeviceVerdict.Blocked, [])]);

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => PadClaimSettingsJson.Write(settings));

        Assert.Contains("device", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The empty settings write as an empty rules array and read back empty.</summary>
    [Fact]
    public void EmptyIsEmpty()
    {
        Assert.Empty(PadClaimSettings.Empty.Rules);

        string json = PadClaimSettingsJson.Write(PadClaimSettings.Empty);
        bool read = PadClaimSettingsJson.TryRead(json, out PadClaimSettings? settings, out string? error);

        Assert.True(read, error);
        Assert.NotNull(settings);
        Assert.Empty(settings.Rules);
    }
}
