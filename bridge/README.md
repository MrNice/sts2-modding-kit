# sts2-mcp and Sts2Bridge

Test Slay the Spire 2 mods with an agent driving the game. Two parts:

- **Sts2Bridge**: a dev-only mod that runs inside each local test copy of the game. It reads game state, presses
  buttons the way a click does, takes screenshots and runs the game's developer console commands. It only switches
  on in a game started with `--force-steam=off` (offline test profiles) or `--sts2-bridge`.
- **sts2-mcp**: an MCP server (stdio) for Claude Code, Codex, Cursor and other agents. It launches test copies
  (one game, or a co-op host plus up to three joiners), talks to their bridges, and wraps the `sts2-inspect` tools.

Fan-made. Not affiliated with or endorsed by Mega Crit.

## Install

Needs the .NET SDK 9 or later (you already have it if you build mods) and Slay the Spire 2 from Steam.

```
dotnet tool install -g sts2-mcp
sts2-mcp setup
claude mcp add sts2 -- sts2-mcp
```

`setup` finds the game, builds the bridge mod against your installed game version and checks the test profiles.
Other agents: register a stdio MCP server with the command `sts2-mcp` and no arguments.

From a clone of this repository instead: `dotnet run --project bridge/Sts2Mcp -- setup`, and register
`dotnet run --project <repo>/bridge/Sts2Mcp` as the command (slower to start), or `dotnet pack` it and install
the package from `artifacts/` with `dotnet tool install -g sts2-mcp --add-source artifacts`.

If the game isn't in a Steam library the tool can find, set `STS2_GAME_DIR` to the folder with
`SlayTheSpire2.exe`.

## Three ways to test

Say what you want the way you'd say it to a person; the agent picks the mode (the `sts2-playtest` skill and the
server's own instructions teach it this).

| You want to | Say something like | What happens |
|---|---|---|
| **Check** that something works | "Check that Smith goes last at a rest site in co-op." "Reproduce the shop gold bug." | Instant speed. The agent does every step, then reports what it found: numbers, log errors, a screenshot or two. You don't need to watch. |
| **See** something yourself | "Set up a fight so I can see the Bash animation." "Show me the PvP tie screen." | The agent gets there at instant speed, switches to normal speed, and hands you the last action: a banner in the game window says what to do. It waits for you, then reports. |
| **Watch** it play | "Play a turn in co-op so I can watch both screens." | Normal speed; the agent acts in small steps you can follow. |

You can also set the speed yourself at any time: "go instant", "back to normal speed".

## What the agent gets

The everyday tools:

| Tool | What it does |
|---|---|
| `launch` / `stop` | start 1-4 test copies on offline profiles 1001-1004 with the bridge and your mods (`mode`: check, stage or watch); close them and clean up |
| `start_run` | from the co-op lobby: each copy picks a character, everyone readies up, the run starts |
| `goto` | jump to a shop, rest site, fight (`fight <ID>`), event or act, and wait until it's ready |
| `plan` | a list of steps run back to back: press, play, end_turn, console, wait, expect, screenshot, speed, handoff. Each action waits until it can happen; the first failure stops the plan and says why |
| `look` | a compact view: screen, my player, the others, hand, enemies and intents, buttons on screen |
| `speed` | instant, fast or normal, on every copy |
| `console` | the game's developer console: `gold 300`, `card BODY_SLAM`, `relic <ID>`, `godmode`, `win`, `help` ... |

And for digging in: `state` (full detail; `player: "all"` to compare copies), `screen`, `buttons`, `press`,
`play_card`, `end_turn`, `events`, `wait_for`, `screenshot`, `tree`, `node`, `tail_log`, `relaunch`, `instances`,
`doctor`, and `inspect` (`sts2-inspect` on the installed game).

Every tool that talks to a game copy takes `player`: omit it for the host, `client` for the first joiner, or a
client id such as `1002`.

## Why plans

An agent takes a few seconds per decision; the game reacts in milliseconds. Clicking one button per agent turn is
slow, so the agent sends what it can predict as one plan:

```json
[ {"console": "gold 300"},
  {"press": "Proceed"},
  {"play": "Bash", "target": "Cultist"},
  {"end_turn": true},
  {"wait": "my_turn"},
  {"expect": {"me.gold": ">=300", "enemies.0.hp": "<40"}} ]
```

Steps wait for the button, card or turn they need (like browser test tools do), so the plan only comes back to the
agent when it's done or when something didn't go as expected.

## Notes

- **Saved co-op runs.** Every session ends mid-run, and the game saves that run in the host's test profile; the next
  host would stop at Host from Save / Abandon Save. `launch` abandons that test run and hosts a new lobby (and says
  so); pass `keep_saved_run` to keep it, e.g. to test save and continue.

- **Co-op sync.** Run-changing console commands (gold, card, room, fight, ...) are synchronized by the game, so run
  each one in one copy, not in every copy. `press` and `play_card` act as that copy's player: act in the copy of
  the player who should act.
- **Blocked console commands**: `cloud` (deletes Steam Cloud saves), `sentry`, `leaderboard`, `achievement`,
  `open`, `getlogs`.
- **Security.** The bridge listens on 127.0.0.1 only, and every connection must first send the random token from
  its discovery file (`%APPDATA%/SlayTheSpire2/bridge/instances/<pid>.json`).
- **From a terminal.** `sts2-mcp launch 2 <modFolder>`, `sts2-mcp call state '{"detail":"full"}' client`,
  `sts2-mcp stop`: the same launcher and bridge commands without an agent.
- **Without MCP.** The bridge also reads requests from a folder (`bridge/queue/<clientId>/in` under the game's
  data folder), for agents that can reach your files but not your machine's network. `tools/sts2q.py` sends one
  command that way: `python3 sts2q.py --client 1002 state`.

## Protocol

One JSON object per line over TCP: `{"id":1,"cmd":"state","args":{...}}` → `{"id":1,"ok":true,"result":{...}}`
(or `"ok":false,"error":"..."`). The first request on a connection is `hello` with the token. Commands: `ping`,
`info`, `look`, `state`, `screen`, `buttons`, `press` (by `id` or `text`), `end_turn`, `play_card` (by `handIndex` or
`card` name), `console`, `speed`, `banner`, `events`, `tree`, `node`, `screenshot`. A failure with `"retry": true`
means "not yet" (plans retry those). See `Protocol/BridgeProtocol.cs` and `Sts2Bridge/src/Commands.cs`.
