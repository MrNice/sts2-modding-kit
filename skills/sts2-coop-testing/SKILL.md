---
name: sts2-coop-testing
description: Test a Slay the Spire 2 co-op mod on one PC with two local game copies and offline test profiles, no second Steam account - launch scripts, the developer console, letting an agent drive the test with sts2-mcp, what to check, and how to read the logs. Use when you need to try a multiplayer feature, reproduce a co-op bug, or check a release before publishing.
license: MIT
compatibility: Windows with a Steam install of Slay the Spire 2. The launch flags are the game's built-in developer options (verified on v0.107.1 and v0.111.0).
metadata:
  version: "0.1.0"
---

# Test a co-op mod on one PC

The game has developer launch options that skip Steam and connect two copies over local networking:

| Flag | Effect |
|---|---|
| `--force-steam=off` | run without Steam (offline profiles; no achievements or cloud saves touched) |
| `--clientId=1001` | which offline profile to use; give each copy its own (1001, 1002, ...) |
| `--fastmp=host` | open straight into hosting a co-op lobby on 127.0.0.1 |
| `--fastmp=join` | join the local host |

Profiles live in `%APPDATA%/SlayTheSpire2/default/<clientId>/`, separate from your real save. Keep test runs on
these profiles.

## Launch script (Windows)

Copy `scripts/test-coop.bat` and `scripts/test-join.bat` into your mod's project folder (they work relative to
where they are). Edit `GAME`, `MOD` and `BUILD` at the top of `test-coop.bat`, and `GAME` in `test-join.bat`.
`test-coop.bat` starts a host (1001), waits for it to open, then starts a client (1002). It copies your built mod into `mods/<MOD>/` first and deletes that folder when both games close.
Workshop subscriptions are installed elsewhere and aren't touched, but a manual install in `mods/<MOD>/` would be
removed. `scripts/test-join.bat [clientId]` starts one more client, for when a client crashed or you need a third
or fourth player (`test-join.bat 1003`).

Steps:

1. Build the mod.
2. Close any running copy of the game.
3. Run `test-coop.bat`, pick characters in both windows, start the run from the host.
4. If a copy asks to enable mods and restart, say no, close both, and run the script again.

## What to check for a co-op feature

- **Both sides.** Every feature has an owner side and a receiving side; test the action from each window.
- **Simultaneous actions.** Have both players act at the same moment (both open a reward, both click the same
  rest site option). Ordering bugs only show up then.
- **Host vs client.** Host-only logic (lobby options, announcements, decisions) needs a test where the *client*
  is the interesting player.
- **Leaving.** Close one copy mid-run, and mid-fight. Check the other copy's behaviour and log.
- **Normal mode and single player.** Confirm the mod stays out of the way when its feature is off.
- **Three or four players**: add copies with `test-join.bat 1003` (and `1004`). Not yet verified beyond two
  local copies; if the lobby refuses, test with real players.

Write the checklist down before testing; it's easy to stop after the happy path.

## The developer console

The game has a developer console (the `` ` `` key). With any mod loaded, or `full_console` on in the profile's
`settings.save`, it has the full command set; otherwise only a few harmless ones. Handy for getting to the thing
you're testing quickly:

| Command | Does |
|---|---|
| `room <type>` | jump to a room (`room shop`, `room restsite`; `help room` lists types) |
| `fight <ID>` / `event <ID>` / `act <n>` | jump to an encounter, event or act |
| `gold <n>`, `heal <n>`, `energy <n>`, `stars <n>` | resources |
| `card <ID> [pile]`, `remove_card <ID>`, `upgrade <hand-index>`, `relic [add\|remove] <ID>`, `potion <ID>` | deck and items (IDs in SCREAMING_SNAKE_CASE) |
| `godmode`, `win`, `kill <index\|all>`, `die`, `damage <n> [index]` | fight shortcuts |
| `help`, `help <cmd>` | the list, and each command's arguments |

In co-op, run-changing commands go through the game's synchronized action queue, so every copy applies them: run a
command in one window, not in each. Commands act as the player whose window ran them. Avoid `cloud` (deletes Steam
Cloud saves). Mods can add their own commands: the game registers `AbstractConsoleCmd` subclasses found in loaded
mods, which is a cheap way to give yourself test shortcuts (e.g. "force a tie").

## Let an agent drive the test (sts2-mcp)

`bridge/` in the sts2-modding-kit repository has an MCP server, `sts2-mcp`, and a dev-only bridge mod. An agent can
then launch the test copies itself, read each copy's state (hand, enemies and intents, deck, gold), press buttons,
play cards, run console commands, take screenshots, wait for events and read logs:

```
dotnet tool install -g sts2-mcp     # or build it from the repository
sts2-mcp setup                      # finds the game, builds the bridge for your game version
claude mcp add sts2 -- sts2-mcp     # or your agent's equivalent
```

Typical session: `launch` (players 2, mods = your build folder) → pick characters with `buttons`/`press` → start
the run → `console` `room shop` → act with `press`/`play_card` in the right copy → compare copies with `state`
(`player: "all"`) → `tail_log` → `stop`. `launch` does the same mods-folder copy and cleanup as `test-coop.bat`,
and turns mods on in the test profiles so the game doesn't stop to ask. The `sts2-playtest` skill covers how to
test with it quickly (plans, and the check / stage / watch ways of working). See `bridge/README.md`.

## Reading the logs

- Your mod's own log (write one; `OS.GetUserDataDir()` is `%APPDATA%/SlayTheSpire2`). Log every decision and
  every message sent and received, with the sender's name. Both copies write to the **same** file when they share
  a machine, so prefix each line with the local player id or name.
- The game's log: `%APPDATA%/SlayTheSpire2/logs/godot<timestamp>.log`, one per launch. Unhandled exceptions from
  your mod land here too.
- On startup, log how many Harmony patch classes applied and failed. A missing feature is often a patch that
  didn't apply.

Asking players for bug reports: request both logs from *every* player in the lobby, plus the game version
(`release_info.json`) and the mod version.

## Before a release

Run the full checklist on the branch(es) you claim to support. If you support main and beta, test on both; see
the sts2-version-audit skill for the static checks that come first.
