using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadClaim;

/// <summary>Reads and writes <see cref="PadClaimSettings"/> as the JSON the host keeps between runs.</summary>
public static class PadClaimSettingsJson
{
    /// <summary>The document version this library writes, and the only one it reads.</summary>
    private const int CurrentSchemaVersion = 1;

    /// <summary>Writes the settings as a schemaVersion 1 JSON document.</summary>
    /// <param name="settings">The settings to write.</param>
    /// <returns>The JSON text.</returns>
    public static string Write(PadClaimSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Rules.IsDefault)
        {
            throw new ArgumentException("The settings' Rules array is uninitialised (default); use an empty array for no rules.", nameof(settings));
        }

        if (settings.Rules.Any(rule => rule?.Device is null))
        {
            throw new ArgumentException("A rule in the settings is null or has a null device; every rule must name its device.", nameof(settings));
        }

        SettingsDocument document = new(CurrentSchemaVersion, [.. settings.Rules.Select(ToDocument)]);
        return JsonSerializer.Serialize(document, PadClaimJsonContext.Default.SettingsDocument);
    }

    /// <summary>Reads settings from JSON, refusing rather than throwing on anything it cannot read.</summary>
    /// <param name="json">The JSON text to read.</param>
    /// <param name="settings">The settings read, or null when the text is refused.</param>
    /// <param name="error">One line saying why the text was refused, or null when it was read.</param>
    /// <returns>True when the text was read into <paramref name="settings"/>.</returns>
    public static bool TryRead(string json, [NotNullWhen(true)] out PadClaimSettings? settings, out string? error)
    {
        settings = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The settings JSON is empty.";
            return false;
        }

        SettingsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, PadClaimJsonContext.Default.SettingsDocument);
        }
        catch (JsonException exception)
        {
            error = $"The settings JSON is malformed: {exception.Message.ReplaceLineEndings(" ")}";
            return false;
        }

        if (document is null)
        {
            error = "The settings JSON holds no document.";
            return false;
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            error = $"Unsupported schemaVersion {document.SchemaVersion}; this library reads version {CurrentSchemaVersion}.";
            return false;
        }

        if (document.Rules is null)
        {
            error = "The settings JSON has no rules array.";
            return false;
        }

        if (document.Rules.Any(rule => rule is null))
        {
            error = "The settings JSON has a null rule.";
            return false;
        }

        if (document.Rules.Any(rule => rule.Device is null))
        {
            error = "A rule in the settings JSON has no device.";
            return false;
        }

        if (document.Rules.Any(rule => rule.Device.Name is null))
        {
            error = "A device in the settings JSON has no name.";
            return false;
        }

        settings = new PadClaimSettings([.. document.Rules.Select(FromDocument)]);
        error = null;
        return true;
    }

    private static RuleDocument ToDocument(DeviceRule rule)
    {
        List<int> blocked = rule.BlockedButtons.IsDefaultOrEmpty ? [] : [.. rule.BlockedButtons];
        return new RuleDocument(rule.Device, rule.Verdict, blocked);
    }

    private static DeviceRule FromDocument(RuleDocument rule)
    {
        ImmutableArray<int> blocked = rule.BlockedButtons is null ? [] : [.. rule.BlockedButtons];
        DeviceIdentity device = string.IsNullOrEmpty(rule.Device.Serial) ? rule.Device with { Serial = null } : rule.Device;
        return new DeviceRule(device, rule.Verdict, blocked);
    }
}

/// <summary>One saved settings document: the version, then the rules.</summary>
internal sealed record SettingsDocument(int SchemaVersion, List<RuleDocument>? Rules);

/// <summary>One saved rule. Everything may be absent, so the reader can refuse or default it.</summary>
internal sealed record RuleDocument(
    DeviceIdentity Device,
    [property: JsonConverter(typeof(VerdictNameConverter))] DeviceVerdict Verdict,
    List<int>? BlockedButtons
);

/// <summary>Reads a verdict by its name only: a number, even one the enum defines, is refused.</summary>
internal sealed class VerdictNameConverter() : JsonStringEnumConverter<DeviceVerdict>(namingPolicy: null, allowIntegerValues: false);

/// <summary>The source-generated serializers for the settings document; nothing reflects at runtime.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SettingsDocument))]
internal sealed partial class PadClaimJsonContext : JsonSerializerContext { }
