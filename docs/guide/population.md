# Fake players (`Ro3Emu.Population`)

A server for one person feels empty. `Ro3Emu.Population` fills it with fake players: protocol bots
(`src/Ro3Emu.Bot`) that log in exactly like the client, so the server, other players and the game client see them
as ordinary players. Nothing in the server changes for them.

With RO3Emu.exe they start and stop with the server: run `RO3Emu.exe --bots 3500` once and every later
double-click of RO3Emu.exe brings them back (the count is kept in `RO3Emu.settings.json`; `--bots 0` turns them off).
Their output goes to `logs\population-*.log`. The release zip carries them in `population\`.

By hand, next to a running server:

```powershell
scripts\start.cmd                          # the server (and your game), as usual
scripts\population.cmd --bots 40           # in a second window, on the same PC; Ctrl+C stops them
```

| Option | Default | |
|---|---|---|
| `--bots` | 30 | how many are online |
| `--server` | `http://127.0.0.1:5380` | the server's HTTP address (it must be on this PC: see GM below) |
| `--data` | the repository's `data\` | the exported game data (maps and nav grids, jobs) |
| `--towns` | every town | town map ids, comma separated, e.g. `10100` for Prontera only |
| `--hunters` | 0.25 | share of players that hunt in the fields next to their town |
| `--vendors` | 0.15 | share of players that run a stall |
| `--active` | no limit (400 above 800 players) | at most this many townies and hunters move about; the others idle |
| `--max-vendors` | 80 | at most this many vendors (stalls keep `StallsIntervalRadius` apart) |
| `--vend-quality` | the two highest | `ItemConfig._iQuality` values vendors sell, comma separated |
| `--chat` | 0.4 | map chat lines per player per minute |
| `--logins` | 8 | logins at the same time while they come online |
| `--step-ms` | 200 (400 above 800 players) | time between two walk steps (the client sends one every 100 ms) |
| `--max-busy` | 0.6 | server game loop busy share above which no more of them log in |
| `--roster` | `data\population\roster.json` | who they are |
| `--guild-every` | 600 | seconds between a player's guild steps (founding, applying) |

## Who they are

The roster file keeps each fake player's account (`pop0001`, ...), name, sex, home town and persona, so the same
players come back on every run. Raising `--bots` adds players; lowering it leaves the extra ones offline. Delete the
file to start over with new names (the old roles stay in the database under their accounts).

A new role gets a random job (any `JobConfig` row below the root the sex may take) and a level to go with its rank,
through the GM commands `!job` and `!lvl`. This is a population choice, not original behavior.

## What they do

- **Townies** walk between the town's NPCs, Kafras and portals, stop, look around, and sometimes say something.
- **Idlers** stand at one spot for minutes, the AFK look of a busy town.
- **Hunters** take the world map to a field the town's portals lead to, fight the nearest monsters with their job's
  normal attack (`JobConfig._kSkillNormalID`) for 8 to 20 minutes, then rest in town (`!hp` refills them).
- **Vendors** open a player stall in the stall map (`PublicParamConfig.StallsMapID`, Prontera) and stand next to it.
  They sell items a player stall accepts (`TradeConfig._iTrade == 2`) of the chosen qualities; by default the two
  highest that have such items, 5 (orange) and 6 (red, which the game's texts call legendary): headgear such as
  Angel Wing and Crown, red and orange enchant scrolls, star cores, gems. The startup line lists how many stall items
  each quality has. Prices are random steps of the item's own ladder (`_iBasePrice`, `_kStableRange`), the same ones a
  real seller can pick. When half the stock has sold they put up new goods. The goods and the opening fee
  (`StallsExpenses`) come from GM `!item`; opening, stocking, buying and settlement are the ordinary stall protocol,
  so what you buy is a real item in your mail and the vendor's proceeds go to its mail. A stall stays open while its
  vendor is offline, as any player's does.
  Vendors also keep the **trade exchange** stocked: its goods (`TradeConfig._iTrade == 1`: cards, gold and red
  equipment, materials) are shared out among the vendors by item id, a handful each, and a vendor lists its share
  (32613, six places) at a price in the item's range, the goods and the Minted Coin listing fee from `!kit`. A sold or
  expired listing is replaced within the hour. With `Ro3Emu:CrystalGear` red gear is listed for crystals, as at the
  stalls; the exchange refuses red gear priced in Zeny then.

Paths come from the map's nav grid (A* over 1 m cells, cut to the corners the client's direct-walk check needs), so
every walk passes the server's walk check. Each player takes the corners a little off the shortest line, stops on the
way now and then, walks at its own pace (never above the client's) and steps off a map's born point on arrival, so a
crowd bound for one spot does not walk it in single file.

## Guilds

Every player is planned once (`GuildPlan`, kept in the roster): about 60 % belong to fake guilds of 12 to 35 members,
8 % are free agents, the rest and the vendors stay guildless. Everything goes through the client's guild messages:

- A guild's first member founds it (32210, base level 21 by FunctionConfig 13, raised by `!lvl` if needed) and turns on
  auto-accept (32230); its members apply (32223) and are in at once.
- A **free agent** is strong (raised to base level 70-90) and applies to up to three real players' guilds with room
  (32211 recommendations whose leader is not a fake player). Their leader decides as with any applicant.
- A real player's **invitation** (32238) is accepted at once by anyone but a fake guild's founder, who leaves its fake
  guild for it (32234); the recruit is raised to a strong level too.

## Parties and friends

- A real player's **party invitation** (42005) is accepted after a second or two (42006) by any fake player that is in
  no party and not in the siege. It then goes where that player goes: to the player's map when it is on another
  (30012; an instance it cannot enter, it waits outside), else beside the player, fighting the monster the party
  leader has locked (30111 `TeamTargetAndEnemy.LeaderTargetId`, turning to a new mark at once), else the monsters
  within 12 m of it. It follows the party's cards (42048, 42050) and positions (42051), and leaves (42001) when that player leaves, has
  been offline for five minutes, or kicks it. A hunter's trip, an idler's wait or a vendor's watch ends as soon as it
  joins, so it follows at once (a vendor's stall stays open while it is away).
- **Support classes** in that party look after the player (`FakePlayer.Support`, `SupportKit`). On first need they
  learn the support skills their job's skill tree teaches, with the skills those require, through the client's own
  31151 request (at the cap, else at half, else at level 1, as far as their skill points go). Then, within 6 m of the
  player, they keep its buffs up (recast every 50-80 s); and wherever they are, they heal the party member lowest on life below 90 % within the heal's reach (themselves and other fake players too, between their own blows) and revive a member who dies: Acolyte
  line Heal, Blessing, Increase AGI, Angelus; Priest Magnificat, Gloria, Impositio Manus, Suffragium, Kyrie Eleison,
  Resurrection; High Priest Assumptio; Archbishop Highness Heal, Sacred Lily; Merchant line Crazy Uproar, Adrenaline
  Rush, Power Thrust; Sniper Wind Walker; Crusader line Heal, Providence, Devotion, Battle Chant. A support class stays
  by the player instead of chasing monsters, and nobody fights on while the player lies dead.
- **Every class fights with its skills** in such a party: it learns its own buffs (`SupportKit.Buffs`: self-cast
  skills without damage such as Aura Blade, True Sight, Maximize Power, Mystical Amplification; never stealth, sitting
  or Berserk) and all its job's damaging skills (`SupportKit.Attacks`: aimed at a monster, around itself or at a
  monster's ground; the latest job's first), with the skill points it has. Before each action it keeps its buffs up,
  then casts any attack skill that is ready (its `_iCD`); only then a normal attack, and a class that fights with
  spells (most of its attack skills magic: the mage line) never swings, it waits for its next spell. A skill it cannot
  use there (weapon, mount, range, wall) rests half a minute; out of SP it is topped up (GM `!hp` in its own scope, at
  most every 30 s). Below half its life it drinks its level's HP potion (`GearPlan.Potion`: `_iUseEffect` RecoverFixed
  HP, through 31323 under the server's cooldowns), the potions from `!kit` when it runs low.
- **Skills the client would offer**: a bot casts only what `_iSkillCondition` (LC_ConditionList) allows it: its weapon,
  a mount ridden or not (`SupportKit.Usable`). A riding Ranger fights with Warg Hunt and Warg Bite and its normal
  attack, which riding turns into Warg Strike (the auto-warg build, AGI and LUK); its bow skills wait. A Mechanic in its
  Madogear fights with the Madogear line. A skill branch that excludes another (a `_kPreSkillRequired` level of -1: the
  Mechanic's Madogear and Axe lines) is learned one or the other, never neither. Weak fillers are measured against the
  strongest attack it can cast often (a cooldown of 15 s at most), not against a long ultimate.
- **Bosses**: a mini-boss or MVP (`_iNPCSubType` 3, 4 or 6) within 7 m sends a fake player on its own running 12 m the
  other way; it fights one only with its skills from 9 m, never toe to toe, and a hunter on its own leaves bosses
  alone. In a real player's party it fights a boss beside the player and runs only below 30 % of its life, drinking a
  potion once away. Ranged classes (their normal attack, or a caster's shortest aimed spell, reaches 5 m or more: archers,
  mages, healers) keep 3-5 m inside their reach (a bow 13-15 m out) and back off when the boss comes closer; attacks that
  do not reach from there wait. **Tanks** (JobConfig `_iDungeonJobPositionID` 101: Swordman, Crusader, Paladin, Royal
  Guard) never dodge or run in a party: they stand in its area skills, drink, and cast Provoke whenever it is ready. A
  Provoke or Taunt (`_kScriptLogic`) holds a monster on its caster for the skill's `_iCD` against the other players'
  hits (`MonsterService.Provoke`, inferred), for a real player's tank too.
- A bot's **life follows its new maximum**: when its party card's maximum HP jumps (it takes the player's strength) it
  is topped up (GM `!hp` in its own scope); before, a bot read itself at 2 % and ran from every boss.
- When the party leader starts a **dungeon**, its fake players are always ready (42033 → 30406 agree, also on the
  42046 reminder); the server then moves them in with the leader.
- A real player's **friend request** (FriendService push `onFriendApply`) is accepted after a few seconds; requests
  sent while the fake player was offline are accepted a minute or two after it logs in (`fetchApplicants`).

## Growth

They grow while they play (inferred population choice): a base level every 20 minutes for hunters, 45 for townies,
60 for the others, given two at a time by `!lvl`, up to the game's cap (`BaseLvLimitConfig`). Once the level allows
the next job of their line (`PublicParamConfig.TransformJobLevelLimit`) they take it with `!job`. Time offline counts at
a third, at most ten levels' worth per login.


**Stat builds** (`StatBuild`, inferred population choice): a few minutes after logging in and after every level gained,
a fake player spends its free stat points through the client's 31500 on its class line's classic build: Knight
STR/AGI/VIT, Crusader VIT/STR, Wizard INT/DEX, Sage INT/DEX/AGI, Hunter DEX/AGI, Bard and Dancer DEX/AGI/INT, Priest
INT/VIT/DEX, Monk STR/AGI, Assassin AGI/STR/LUK, Rogue STR/AGI/DEX, Blacksmith STR/DEX. Each point goes to the weighted
stat furthest behind its share, at BasePointConfig's cost of each value; nothing spent before is taken back.

**Class items** (`FakePlayer.Mount`, inferred population choice): a class with Special Class Items (CartRemouldTable,
the Profession panel) carries them as a player does. It learns the item's `_kUnlockSkillID` (Peco Peco Riding, Falcon
Mastery, Pushcart) on its own (its whole kit waits for a party), leases one item per slot that its level reaches with 32721 (the latest: a
Rune Knight's dragon over its Peco Peco; the plain or the armored Peco Peco at random) and rides its battle mount with
32745 tp OnBattle. It mounts again every few minutes, as death, a map that forbids mounts or a job change take it off.

**Gear and talents** (`GearPlan`, `FakePlayer.Gear`, inferred population choice): a fake player wears its job's gear
for its level: per part the client's own equipment (ItemConfig `_iType` 5) its job may wear (`_kJobNeed`), red
(quality 6) for a guild leader and gold (5) for the others, at the highest `_iLevelNeed` it reaches; a two- or
one-handed weapon (with an off-hand) per role. Gold and red pieces are appraised first (31401; the appraisal function
opens with quest 10103020, delivered once by a slow background `!quest done`). GM `!kit` grants the set in the bot's
own scope (never the realm's) and names the pieces, which it wears with 31400; a new set comes when its level reaches the next tier. Every 40-80 minutes it grows: the
least strengthened grid +2 (31404, StrengthenTable's cost, never past its base level) and its next two talent levels
(83550, SeasonTalentEffectConfig's cost, plain stat nodes in tree order from the start points), the materials granted
by one `!kit` first. The talent panel opens with its two quests (`!quest done`, once, in the bot's own scope).
What it wears, each grid's level and each talent's level are kept in the roster. The first gear-up of a realm starts
spread over 45 minutes, and at most four bots gear up or grow at once across the realm; a bot in a real player's party goes at once.

## The siege (War of Emperium)

The game's War of Emperium is the weekly guild siege (DailyActivityConfig 115: Saturday 21:30-22:30 in the current
client; see [territory wars](../systems/territory-wars.md)). Its pairings come from a season of territory battles,
long over on a local server, so the population pairs guilds itself with the `!siege seed` test aid: every minute it
reads the siege panel (44402) and gives each city that misses a side a fake guild; the royal city takes a real
player's guild as attacker when none is paired. A finished siege leaves its winner owning the city and its loser as
the next attacker. While the window is open, every online member of a paired guild enters the siege (30400) for its
city: attackers go for the crystal (NPC 4900009) and hit defenders on the way, defenders hold the crystal and hit
attackers, and the dead revive (31013). They leave (30402) when their city's fight is over. `!siege open` (a GM test
aid) opens a one-hour window at once. A dropped player logs in again after a pause that grows to two minutes.

## Rival raids on the MVP maps

With the server's `MvpMapsPk` (on by default) every field map with an MVP (`MVPConfig._iMapID`) is a PK map:
players fight each other there unless they share a party or a guild (`FieldPk`). The client tells friend from
enemy by camp (`LC_CampRelationUtils.IsEnemyByUnit` on the map's `campRelationshipConfig` row 1), so the server
shows each viewer a player it may fight in camp 2 and everyone else in their own camp, and sends the bases again
when a party changes. Neither the rule nor the raids are original content: the original server had only real
players.

Every minute `MvpRaidCoordinator` reads the live MVPs (loopback `/ro3emu/mvps`) and where the real players are
(`/ro3emu/online`). It keeps `--mvp-raids` raids (3; 0 turns them off) of `--mvp-raid-size` fake players (8, level
40 and up, never vendors) going for an alive MVP: first one on a map a real player is on (up to two raids there),
else any other (one raid each). The leader creates a raid party (42001, `TeamType.Forty`) and invites the others,
who follow it as they would a real player's lead; it walks to where the MVP stands and fights it. On those maps a
raid leader fights an enemy player within 9 m of itself, and a party member (of a raid or of a real player) one
within 9 m of the player it follows, before any monster; there every fake player is shown up to 30 players instead
of one. Lone hunters there fight monsters only. A raid's players are boosted
like a real player's party (`FakePlayer.Boost`), and with the server's `RivalRaidsMatchPlayers` (on) they fight with
at least the combat attributes of a real player on their map, so a rival raid is a match for that player's party.
A raid breaks up after 30-45 minutes, or when its leader or all but one of its players go offline, and its players
go back home.

Towns are the Kafra destinations (`MapLinkData.Capra`) with Prontera's `MapConfig._iSceneType` (inferred: the client
does not name a "town" type).

## Thousands of them

A big roster comes online a few at a time (`--logins`), and only while the server keeps up: every few seconds the
runner reads the game loop's busy time from `/ro3emu/metrics` and holds the next logins while the loop is busier
than `--max-busy`. Players already online stay, so on a slower PC the population settles at what the server can carry;
the log says when logins are held and the minute status line shows the loop's load.

Three choices keep a crowd cheap. Fake players ask for the smallest player view the client offers (30005/30007
`ViewMaxCount` 1), so the server does not stream every crowd's movement to bots that never look at it; you still see
up to 149 of them. They step every 200 ms instead of 100 ms. And towns split into lines on their own once a line is
full (`MapConfig._iRoleAlert` / `_iRoleCntLimit`), like the original.

## GM

Job, level, HP and vendor goods go through the loopback GM tool (`POST /ro3emu/gm`, [GM access](gm-access.md)), which answers
only on the server's machine. With a `GmAccounts` list the `pop....` accounts must be on it, or they stay novices.
