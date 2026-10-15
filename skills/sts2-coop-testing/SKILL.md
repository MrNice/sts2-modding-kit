---
name: sts2-coop-testing
description: Test a Slay the Spire 2 co-op mod on one PC with two local game copies and offline test profiles, no second Steam account - launch scripts, what to check, and how to read the logs. Use when you need to try a multiplayer feature, reproduce a co-op bug, or check a release before publishing.
license: MIT
compatibility: Windows with a Steam install of Slay the Spire 2. The launch flags are the game's built-in developer options (verified on v0.107.1 and v0.111.0).
metadata:
  version: "0.0.1"
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
