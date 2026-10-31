using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// A fake player in a real player's party steps out of a boss's area skill and stays out while it lasts, as a player
/// reads the ground marker. A boss's area cast (31100 / 31106 UseInSkillInfo of a skill that SupportKit.StrikesArea:
/// not single-target, with a radius) marks a circle: round the point it aims at when it names a target, else round the
/// boss (its own area, a fan or a rectangle ahead of it, read wide), its SkillConfig _kRange[0] (cm) across, as the
/// server's BossSkillSets takes it, for the cast's skillMs (in-game 30.10: area casts last 3-16 s and come without a
/// chant). Inside it, the bot walks out past its edge (<see cref="DodgeMargin"/>), and it fights on only outside it. A
/// population choice (inferred): the original server had only real players.
/// </summary>
public sealed partial class FakePlayer
{
    private const float MaxDangerRadius = 25, DodgeMargin = 2.5f;
    /// <summary>The longest a marked area is avoided after its cast begins (the longest boss channel seen, 16 s).</summary>
    private static readonly TimeSpan MaxDanger = TimeSpan.FromSeconds(17);
    private (Vec3 Center, float Radius, DateTime Until)? _danger;

    private void OnBossSkill(UseInSkillInfo cast)
    {
        // A tank stands in it and holds the boss (FakePlayer.Support.Tank).
        if (!InParty || Tank || cast.SrcID == _view.Self || !ctx.Support.StrikesArea(cast.SkillID)) return;
        var boss = _view.Monsters().FirstOrDefault(m => (long)m.Uuid == cast.SrcID);
        if (boss == null || !ctx.Support.IsBoss((long)boss.EntityTemplateID)) return;
        var center = cast.TargetId != 0 && cast.TargetPos != null ? cast.TargetPos : boss.MovementInfo.Pos;
        var lasts = TimeSpan.FromMilliseconds(Math.Clamp(cast.ChantMs + cast.SkillMs, 1000, (int)MaxDanger.TotalMilliseconds));
        lock (_partyLock) _danger = (center.Clone(), Math.Min(ctx.Support.AreaRadius(cast.SkillID), MaxDangerRadius), DateTime.UtcNow + lasts);
    }

    /// <summary>Whether <paramref name="point"/> lies in a marked area that still lasts (a bot does not walk back in).</summary>
    private bool InDanger(Vec3 point)
    {
        (Vec3 Center, float Radius, DateTime Until)? danger;
        lock (_partyLock) danger = _danger;
        if (danger is not { } d || DateTime.UtcNow >= d.Until) return false;
        float dx = point.X - d.Center.X, dz = point.Z - d.Center.Z;
        return dx * dx + dz * dz <= (d.Radius + 0.5f) * (d.Radius + 0.5f);
    }

    /// <summary>Out of a marked area this bot stands in; true when it moved.</summary>
    private async Task<bool> DodgeAsync(CancellationToken ct)
    {
        (Vec3 Center, float Radius, DateTime Until)? danger;
        lock (_partyLock) danger = _danger;
        if (danger is not { } d || DateTime.UtcNow >= d.Until) return false;
        SyncPosition();
        float dx = _pos.X - d.Center.X, dz = _pos.Z - d.Center.Z, length = MathF.Sqrt(dx * dx + dz * dz);
        if (length > d.Radius + 0.5f) return false;
        if (length < 0.1f) (dx, dz, length) = (1, 0, 1); // on the spot itself: any way out
        float out_ = d.Radius + DodgeMargin;
        var away = new MapPoint(d.Center.X + dx / length * out_, _pos.Y, d.Center.Z + dz / length * out_, 0);
        // Straight out when the ground allows it, else anywhere just beyond the edge.
        if ((Near(away, 0, 2) ?? Near(new MapPoint(d.Center.X, _pos.Y, d.Center.Z, 0), out_, out_ + 2)) is not { } to) return false;
        return await GoAsync(to, ct); // no way out: it fights on rather than trying again at once
    }
}
