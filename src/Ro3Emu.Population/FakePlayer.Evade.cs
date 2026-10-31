using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// Staying alive near a boss (a population choice, inferred): a mini-boss or MVP (SupportKit.IsBoss) that comes within
/// <see cref="EvadeRange"/> sends a fake player running the other way; alone it fights one only with its skills, from
/// <see cref="BossRange"/>, never toe to toe. In a real player's party it stands and fights, and runs only when its own
/// life is below <see cref="PartyEvadeLife"/>.
/// </summary>
public sealed partial class FakePlayer
{
    private const float EvadeRange = 7, BossRange = 9, FleeDistance = 12;
    private const double PartyEvadeLife = 0.3;

    /// <summary>The nearest boss within <see cref="EvadeRange"/>, or null.</summary>
    private EntityBase? Threat() => _view.Monsters()
        .Where(m => ctx.Support.IsBoss((long)m.EntityTemplateID) && Distance(m.MovementInfo.Pos) <= EvadeRange)
        .OrderBy(m => Distance(m.MovementInfo.Pos)).FirstOrDefault();

    /// <summary>Runs from a boss that is too close: true when it ran.</summary>
    private async Task<bool> EvadeAsync(CancellationToken ct)
    {
        // In a party a tank never runs: it holds the boss and drinks (FakePlayer.Support.Tank).
        if (InParty && (Tank || OwnLife() >= PartyEvadeLife)) return false;
        SyncPosition();
        if (Threat() is not { } boss) return false;
        var at = boss.MovementInfo.Pos;
        float dx = _pos.X - at.X, dz = _pos.Z - at.Z, length = MathF.Max(0.1f, MathF.Sqrt(dx * dx + dz * dz));
        var away = new MapPoint(_pos.X + dx / length * FleeDistance, _pos.Y, _pos.Z + dz / length * FleeDistance, 0);
        // Straight away when the ground allows it, else anywhere at the same distance.
        if ((Near(away, 0, 3) ?? Near(new MapPoint(_pos.X, _pos.Y, _pos.Z, 0), FleeDistance - 2, FleeDistance + 2)) is not { } to)
            return false;
        if (InParty) PopulationContext.Log($"{entry.Name}: runs from boss {boss.EntityTemplateID} at {OwnLife():P0} life");
        return await GoAsync(to, ct); // no way away: it fights on rather than trying again at once
    }
}
