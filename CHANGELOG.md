# Changelog

## 0.1.0 (unreleased)

- New: `bridge/` with **sts2-mcp** 0.1.0 (MCP server, installs as a .NET tool) and the **Sts2Bridge** dev mod.
  Tools: `doctor`, `launch`, `stop`, `instances`, `state`, `screen`, `buttons`, `press`, `play_card`, `end_turn`,
  `console`, `events`, `wait_for`, `screenshot`, `tree`, `node`, `tail_log`, `inspect`, `relaunch`; command line
  `sts2-mcp launch | call | stop | doctor | setup`. The bridge is built on the
  user's machine against their installed game; it compiles unchanged against v0.107.1 and the v0.111.0 beta.
  Tried live on v0.111.0 (beta) with two copies: lobby, character select, run start, console (`room Shop`,
  `gold`, `room Monster`, `win`) synchronized across both copies, card play with a target, end turn, events,
  screenshots, stop and cleanup.
- Plans: `plan` runs a list of steps (press, play, end_turn, console, wait, expect, screenshot, speed, handoff) in
  one call; actions wait until they can happen, the first failure stops the plan with a look and a screenshot.
  `look` (compact view), `speed` (instant/fast/normal on every copy), `start_run`, `goto`, and `launch(mode)` for
  the three ways of working: check, stage (hand-off with an in-game banner), watch. The server sends agents usage
  instructions when they connect.
- New skill `sts2-playtest`: how an agent should play-test with these tools.
- `bridge/tools/sts2q.py`: send one command through the bridge's file queue (no MCP or network needed).
- sts2-coop-testing skill: the developer console (full command set with mods loaded, co-op synchronization) and
  agent-driven testing.
- sts2-inspect: `Program.Run(args, out, err)` so other tools can run it in-process. Output unchanged.

## 0.0.1 (2026-10-15)

First draft.

- Skills: `sts2-inspect-game`, `sts2-version-audit`, `sts2-coop-mod-patterns`, `sts2-coop-testing`,
  `sts2-localize-zh`, `sts2-mod-release`.
- Tool: `sts2-inspect` 0.0.1 with `types`, `il`, `has`, `refs`, `typediff`, `ildiff`, `audit`, `pck list`,
  `pck extract`.
- Verified on game v0.107.1 and v0.111.0 (beta). Run against a co-op mod's 0.4.0 build, the audit reported the
  four breaks that build had on the beta: two compiled calls into changed members (one also a compile error),
  one patch target that became overloaded, and one reflection call whose parameters changed.
