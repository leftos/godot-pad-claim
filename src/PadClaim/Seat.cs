namespace PadClaim;

/// <summary>
/// The single seat: vacant or held by one pad with the time of its last activity. It decides claim attempts, gives the
/// seat back to a holder that disconnected and returns, and keeps a button held through the claim out of the game.
/// </summary>
/// <param name="tuning">The thresholds to measure against.</param>
internal sealed class Seat(ClaimTuning tuning)
{
    private readonly HashSet<int> heldThroughClaim = [];
    private DeviceTracker? holder;
    private TimeSpan lastActivity;
    private DeviceIdentity? awayHolder;
    private int awaySequence;

    /// <summary>Gets the holder's device index, or null when the seat is vacant.</summary>
    public int? Device => holder?.Device;

    /// <summary>Gets or sets whether a completed gesture may take the seat.</summary>
    public bool ClaimsOpen { get; set; } = true;

    /// <summary>Decides a completed gesture: the seat is taken when vacant or when its holder is idle, else refused.</summary>
    /// <param name="claimant">The pad that completed the gesture.</param>
    /// <param name="now">The time of the confirm release.</param>
    /// <param name="events">The events the attempt causes, appended in order.</param>
    public void Attempt(DeviceTracker claimant, TimeSpan now, List<ClaimEvent> events)
    {
        if (!ClaimsOpen)
        {
            Refuse(claimant, RefuseReason.ClaimsClosed, events);
        }
        else if (holder is null)
        {
            Take(claimant, now);
            events.Add(new SeatClaimed(claimant.Device));
        }
        else if (now - lastActivity >= tuning.SeatStealIdle)
        {
            Move(holder, claimant, now, events);
        }
        else
        {
            Refuse(claimant, RefuseReason.SeatBusy, events);
        }
    }

    /// <summary>Gives the seat to a pad unconditionally, at the host's request; a former holder becomes ready.</summary>
    /// <param name="claimant">The pad that takes the seat; not the holder.</param>
    /// <param name="now">The time of the request.</param>
    /// <param name="events">The events the claim causes, appended in order.</param>
    public void Force(DeviceTracker claimant, TimeSpan now, List<ClaimEvent> events)
    {
        if (holder is null)
        {
            Take(claimant, now);
            events.Add(new SeatClaimed(claimant.Device));
        }
        else
        {
            Move(holder, claimant, now, events);
        }
    }

    /// <summary>Takes a pad's blocking: a holder loses the seat.</summary>
    /// <param name="blocked">The pad the player blocked.</param>
    /// <param name="events">The events the blocking causes, appended in order.</param>
    public void Blocked(DeviceTracker blocked, List<ClaimEvent> events)
    {
        if (holder != blocked)
        {
            return;
        }

        Vacate();
        events.Add(new SeatLost(blocked.Device, SeatLossReason.Blocked));
    }

    /// <summary>Takes a button change: the holder's every press and release is activity, and a release frees its button.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="button">The button index.</param>
    /// <param name="pressed">Whether the button went down.</param>
    /// <param name="now">The time of the change.</param>
    public void NoteButton(int device, int button, bool pressed, TimeSpan now)
    {
        if (holder?.Device != device)
        {
            return;
        }

        lastActivity = now;
        if (!pressed)
        {
            heldThroughClaim.Remove(button);
        }
    }

    /// <summary>Takes an axis move of a pad that counts as activity.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="now">The time of the move.</param>
    public void NoteAxisActivity(int device, TimeSpan now)
    {
        if (holder?.Device == device)
        {
            lastActivity = now;
        }
    }

    /// <summary>
    /// Takes a pad's removal: a holder loses the seat, and its identity is remembered for its return together with the
    /// latest connect sequence number, so only a pad connecting after it left counts as the return.
    /// </summary>
    /// <param name="removed">The pad that disconnected.</param>
    /// <param name="latestSequence">The sequence number of the latest connect so far.</param>
    /// <param name="events">The events the removal causes, appended in order.</param>
    public void Removed(DeviceTracker removed, int latestSequence, List<ClaimEvent> events)
    {
        if (holder != removed)
        {
            return;
        }

        Vacate();
        awayHolder = removed.Identity;
        awaySequence = latestSequence;
        events.Add(new SeatLost(removed.Device, SeatLossReason.Disconnected));
    }

    /// <summary>Forgets a holder that left when the player blocks or forgets its identity, so its return is never given the seat.</summary>
    /// <param name="identity">The identity the player blocked or forgot.</param>
    public void ForgetAwayHolder(DeviceIdentity identity)
    {
        if (awayHolder?.Matches(identity) == true)
        {
            awayHolder = null;
        }
    }

    /// <summary>
    /// Gives the seat back to a pad that just settled when it connected after the holder left and is the only connected
    /// match for it.
    /// </summary>
    /// <param name="settled">The pad that just became ready.</param>
    /// <param name="connected">Every connected pad, the settled one included.</param>
    /// <param name="now">The time it settled.</param>
    /// <param name="events">The events the restore causes, appended in order.</param>
    public void TryRestore(DeviceTracker settled, IEnumerable<DeviceTracker> connected, TimeSpan now, List<ClaimEvent> events)
    {
        if (holder is not null || awayHolder is null || settled.Sequence <= awaySequence || !awayHolder.Matches(settled.Identity))
        {
            return;
        }

        DeviceIdentity remembered = awayHolder;
        if (connected.Count(tracker => remembered.Matches(tracker.Identity)) != 1)
        {
            return;
        }

        Take(settled, now);
        events.Add(new SeatRestored(settled.Device));
    }

    /// <summary>Vacates the seat at the host's request and forgets a holder that left.</summary>
    /// <param name="now">The time of the release.</param>
    /// <param name="events">The events the release causes, appended in order; none when the seat is vacant.</param>
    public void Release(TimeSpan now, List<ClaimEvent> events)
    {
        awayHolder = null;
        if (holder is null)
        {
            return;
        }

        DeviceTracker former = holder;
        former.LoseSeat(now);
        Vacate();
        events.Add(new SeatLost(former.Device, SeatLossReason.Released));
    }

    /// <summary>Tells whether a button reaches the game: the holder's, unless it was held through the claim.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="button">The button index.</param>
    /// <returns>True when the button is accepted.</returns>
    public bool AcceptsButton(int device, int button) => holder?.Device == device && !heldThroughClaim.Contains(button);

    /// <summary>Tells whether a pad's axes reach the game: only the holder's.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <returns>True when the pad holds the seat.</returns>
    public bool AcceptsAxes(int device) => holder?.Device == device;

    private static void Refuse(DeviceTracker claimant, RefuseReason reason, List<ClaimEvent> events)
    {
        claimant.RefuseSeat();
        events.Add(new ClaimRefused(claimant.Device, reason));
    }

    private void Move(DeviceTracker former, DeviceTracker claimant, TimeSpan now, List<ClaimEvent> events)
    {
        former.LoseSeat(now);
        Take(claimant, now);
        events.Add(new SeatMoved(former.Device, claimant.Device));
    }

    private void Take(DeviceTracker claimant, TimeSpan now)
    {
        claimant.TakeSeat();
        holder = claimant;
        lastActivity = now;
        awayHolder = null;
        heldThroughClaim.Clear();
        heldThroughClaim.UnionWith(claimant.ButtonsDown);
    }

    private void Vacate()
    {
        holder = null;
        heldThroughClaim.Clear();
    }
}
