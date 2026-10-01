namespace STS2PGN.Mod.Recording;

/// <summary>
/// A terminal reward/treasure phase ends at committed map travel, not at opening
/// the map. Room identity prevents a deferred departure leaking across rooms.
/// This holds no game state and never changes the live reward set.
/// </summary>
internal sealed class RewardDeparture
{
    private object? _room;

    public void Offered(object room) => _room = room;

    public void Entered(object room)
    {
        // A restored, pre-finished combat can offer rewards during room entry.
        if (!ReferenceEquals(_room, room))
        {
            Clear();
        }
    }

    public bool CanDefer(object? room, bool combatActive, bool resumesParent) =>
        room is not null && ReferenceEquals(_room, room) && !combatActive && !resumesParent;

    public bool CommitMapTravel(object? room, bool combatActive, bool resumesParent)
    {
        bool finalize = CanDefer(room, combatActive, resumesParent);
        Clear();
        return finalize;
    }

    public void Clear() => _room = null;
}
