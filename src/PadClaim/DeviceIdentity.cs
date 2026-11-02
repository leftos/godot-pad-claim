using System.Globalization;

namespace PadClaim;

/// <summary>Who a connected pad is, as far as the engine can tell.</summary>
/// <param name="VendorId">The USB vendor id, or 0 when the backend does not report one.</param>
/// <param name="ProductId">The USB product id, or 0 when the backend does not report one.</param>
/// <param name="Name">The backend's name for the pad, which differs between backends.</param>
/// <param name="Serial">The pad's serial number, or null when it reports none.</param>
public sealed record DeviceIdentity(int VendorId, int ProductId, string Name, string? Serial)
{
    /// <summary>Reads an identity from Godot's <c>Input.get_joy_info</c> dictionary.</summary>
    /// <param name="info">The keys Godot reports: raw_name, vendor_id, product_id, serial_number.</param>
    /// <param name="fallbackName">The name to use when the pad reports an empty one.</param>
    /// <returns>The identity the keys describe.</returns>
    public static DeviceIdentity FromJoyInfo(IReadOnlyDictionary<string, string> info, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(fallbackName);

        return new DeviceIdentity(
            ReadId(info, "vendor_id"),
            ReadId(info, "product_id"),
            ReadText(info, "raw_name") ?? fallbackName,
            ReadText(info, "serial_number")
        );
    }

    /// <summary>Tells whether this identity names the same pad as <paramref name="other"/>.</summary>
    /// <param name="other">The identity to compare with.</param>
    /// <returns>
    /// True when the ids decide it (both sides report a vendor and a product id) and they are equal, or, when
    /// either side does not report ids, the names are equal; and, when both sides carry a serial, it is equal too.
    /// </returns>
    public bool Matches(DeviceIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return NamesTheSameDevice(other) && SerialsAgree(other);
    }

    private static int ReadId(IReadOnlyDictionary<string, string> info, string key) =>
        info.TryGetValue(key, out string? text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static string? ReadText(IReadOnlyDictionary<string, string> info, string key) =>
        info.TryGetValue(key, out string? text) && !string.IsNullOrEmpty(text) ? text : null;

    private bool NamesTheSameDevice(DeviceIdentity other) =>
        VendorId != 0 && ProductId != 0 && other.VendorId != 0 && other.ProductId != 0
            ? VendorId == other.VendorId && ProductId == other.ProductId
            : string.Equals(Name, other.Name, StringComparison.Ordinal);

    private bool SerialsAgree(DeviceIdentity other) =>
        Serial is null || other.Serial is null || string.Equals(Serial, other.Serial, StringComparison.Ordinal);
}
