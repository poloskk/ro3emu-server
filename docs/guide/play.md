# Play on your own PC

The full guide behind the README's three steps.

1. Download **`RO3Emu-win-x64.zip`** from the [latest release](https://github.com/aethai/ro3emu-server/releases/latest)
   and extract it anywhere.
2. Double-click **`RO3EmuPlay.exe`**, the launcher window (or `RO3Emu.exe`, the same in a console). Nothing to
   install: no .NET, Java or Node.js. If Windows says "Windows protected your PC" (RO3Emu is not signed), click
   *More info*, then *Run anyway*.
3. The first time, it finds your Ragnarok 3: Genesis game folder (or asks for the one with `ro3.exe`), prepares the
   game data it needs (about a minute), starts the server and the game. Next time it only starts the server and the
   game.
4. Log in with any account name and password: the account is created on first login. Keep the RO3Emu window open
   while you play; press **Q** there (or close it) to stop the server.

**The launcher window** (`RO3EmuPlay.exe`) runs the same start as `RO3Emu.exe` and shows it: the six steps (game,
game data, server, patch, fake players, play) with a progress bar, the fake players logging in, and the realm's
status (the server's tick rate and load, players online, uptime, your rates). **Play now** starts the game before
every fake player is in; **Play** starts it again after you closed it; **Stop server** (or closing the window) saves
the characters and stops the server. The gear opens the settings (fake players, EXP rate, auto timer, crystal drops;
used from the next start), the page icon the server's log. It needs the Microsoft Edge WebView2 Runtime, which
Windows 10 and 11 normally have.

Its art is your own game's: run the game once with `RO3EMU_DUMPTEX=IMG` in its environment (the client hook saves
the login screen's images into `<game>\ro3emu\dump`), then `tools/release/launcher-art.ps1 -Client <game>` and
rebuild `src/Ro3Emu.Play`: the login background, the logo and the window icon. Releases carry no game art; without
it the page draws its sky itself.

**Don't have the game?** RO3Emu.exe downloads it from the official Genesis update servers (about 24 GB) and checks every
file against the publisher's signature: it offers this when it finds no game folder, or run `RO3Emu.exe install`.
RO3Emu never hosts game files. Tested with the Genesis client releases of 7, 16, 21, 23 and 25 October 2026 (2026.10.25.1,
the current one); the 20 October release passes `RO3Emu.exe check` and the data export.

| Command | What it does |
|---|---|
| `RO3Emu.exe` | play on your own server (same as `RO3Emu.exe start`) |
| `RO3Emu.exe join <address>` | play on someone else's server (`host`, `host:port` or `http://host:port`) |
| `RO3Emu.exe host` | your server, open to friends (`--gm <account>` for GM commands, once per account: `--gm you --gm friend`) |
| `RO3Emu.exe install [folder]` / `update [folder]` | the game from the official servers, every file checked |
| `RO3Emu.exe restore` | undo the patch: the game talks to the official servers again |
| `RO3Emu.exe check` | does this game version fit this RO3Emu? (changes nothing) |
| `RO3Emu.exe diag` | makes `RO3Emu-diagnostics.zip` for a bug report |

**What it changes.** In the game folder: a few DLLs in `ro3_Data\Managed` (the originals are kept in `ro3emu-backup`)
and `ro3emu\loc_en.tsv` (English texts, built from your own game files). Next to RO3Emu.exe: `data\` (game data and
your characters), `logs\` and `RO3Emu.settings.json`.

**Rates.** `"ExpRate"` in `RO3Emu.settings.json` multiplies the EXP of monster kills (Base and Job): `1` is the
original game, `3` three times as much. Quest and reward EXP stay as the game sets them. `"BonusTimeDrain"` is how
fast field Bonus Time (the auto-battle timer) runs down: `0` never (the default), `0.25` four times slower, `1` the
original. `"CrystalDropChance"` is the chance an elite, mini-boss or MVP kill drops Sacred Relic Crystals (3-8, MVPs
20-40; `0` never). While it is on, an ordinary monster drops one now and then, as rarely as Arcane Ore (0.76% a kill). Player stalls show on every line of a town. Restart RO3Emu.exe after changing a setting.

> [!WARNING]
> Don't let the publisher's launcher repair a patched game folder: it puts the original DLLs back. Run RO3Emu.exe
> again and it patches the game again.
