using System.Globalization;
using Godot;
using PadClaim;
using PadClaim.GodotInput;

namespace PadClaimDemo;

/// <summary>
/// Hosts a <see cref="GodotPadSource"/> and prints every connected pad, the seat holder and the events it raises.
/// </summary>
/// <remarks>
/// A developer demo, not a designed screen: one label in a scroll container, the default theme, and the claimer's own
/// records as the text. In a debug build an unlisted device is admitted, so a pad injected by godot-mcp can claim; in a
/// release build it is ignored.
/// </remarks>
public partial class Main : Control
{
    private const int ShownEvents = 10;

    private readonly PadClaimer claimer = new(
        PadClaimSettings.Empty,
        new ClaimOptions(RequireKnownGamepad: true, UnlistedDevices: OS.IsDebugBuild() ? UnlistedDevicePolicy.Accept : UnlistedDevicePolicy.Ignore)
    );

    private readonly List<string> events = [];

    private Label? status;
    private GodotPadSource? source;

    /// <inheritdoc/>
    public override void _Ready()
    {
        ScrollContainer scroll = new() { Name = "Scroll" };
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        AddChild(scroll);

        status = new Label
        {
            Name = "Status",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        scroll.AddChild(status);

        source = new GodotPadSource(claimer);
        source.Raised += OnRaised;
        source.Start();
        Refresh();
    }

    /// <inheritdoc/>
    public override void _Input(InputEvent @event)
    {
        if (source?.Feed(@event) == true)
        {
            GetViewport().SetInputAsHandled();
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta) => source?.Tick();

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (source is null)
        {
            return;
        }

        source.Raised -= OnRaised;
        source.Dispose();
        source = null;
    }

    private static string Line(DeviceSnapshot device, int? seat)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{device.Device}: {device.Identity.Name} - {device.Status}");
        if (device.Reasons != SuspectReason.None)
        {
            line += string.Create(CultureInfo.InvariantCulture, $" - {device.Reasons}");
        }

        if (!device.SuspectButtons.IsDefaultOrEmpty)
        {
            IEnumerable<string> buttons = device.SuspectButtons.Select(button => button.ToString(CultureInfo.InvariantCulture));
            line += $" - buttons {string.Join(", ", buttons)}";
        }

        return seat == device.Device ? line + " (seat)" : line;
    }

    private string EventLine(ClaimEvent claimEvent) =>
        claimEvent switch
        {
            // A snapshot's own ToString() prints its ImmutableArray field as a type name; the per-device line reads.
            DeviceAdded added => nameof(DeviceAdded) + ": " + Line(added.Snapshot, claimer.SeatDevice),
            DeviceChanged changed => nameof(DeviceChanged) + ": " + Line(changed.Snapshot, claimer.SeatDevice),
            _ => claimEvent.ToString() ?? string.Empty,
        };

    private void OnRaised(ClaimEvent claimEvent)
    {
        events.Insert(0, EventLine(claimEvent));
        if (events.Count > ShownEvents)
        {
            events.RemoveAt(events.Count - 1);
        }

        Refresh();
    }

    private void Refresh() => status?.Text = Description();

    private string Description()
    {
        List<string> lines = [claimer.SeatDevice is int seat ? string.Create(CultureInfo.InvariantCulture, $"Seat: device {seat}") : "Seat: open"];
        lines.AddRange(claimer.Devices.Select(device => Line(device, claimer.SeatDevice)));
        lines.Add(string.Empty);
        lines.Add("Recent events, newest first:");
        lines.AddRange(events);
        return string.Join('\n', lines);
    }
}
