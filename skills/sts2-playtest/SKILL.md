---
name: sts2-playtest
description: Play-test a Slay the Spire 2 mod through the sts2-mcp tools - launch local test copies (solo or co-op), get to the thing under test fast, act in plans instead of single clicks, check results, and hand the visual moments to the person. Use whenever someone asks to test, check, try, reproduce, or show something in their StS2 mod and the sts2 MCP tools are available.
license: MIT
compatibility: Needs the sts2-mcp MCP server (dotnet tool install -g sts2-mcp; sts2-mcp setup) and Slay the Spire 2 on Windows. Verified on game v0.111.0 (beta).
metadata:
  version: "0.1.0"
---

# Play-test a Slay the Spire 2 mod

## 1. Pick how to work from what was asked

| They said something like | Mode | Speed | Who does the last step |
|---|---|---|---|
| "check / make sure / does X work / reproduce / why does" | **check** | instant | you |
| "show me / I want to see / how does it look / the animation, the effect" | **stage** | instant, then normal for the moment that matters | them |
| "walk me through / let me watch / play a turn so I can see" | **watch** | normal | you, in small steps |

If it's unclear, ask one short question: "Do you want me to check it, or set it up so you can see it?"

## 2. Be fast: plans, not clicks

Every round trip through you costs seconds; the game takes milliseconds. So:

- **Use `plan` for everything you can predict.** Its steps wait on their own for buttons to appear, cards to be
  playable and turns to come round. Don't `look` between steps whose outcome you know.
- **End a plan with `expect`** for what should be true, instead of looking and checking yourself.
- **`look` once per decision**, when you need to choose (which card, which reward). It's compact; use `state` only
  when you need full card lists.
- **Set up with the console, not by playing**: `goto` a room, `console` for gold, cards, relics, `godmode`, `win`.
- A failed step stops the plan and returns where and why, a look and a screenshot: read it and re-plan from there.

## 3. The session

```
launch(players=2, mods=["<build output folder>"], mode="check")    # 30-90 s; mode sets the speed
start_run(["IRONCLAD","SILENT"])                                    # lobby → run, both copies
goto("shop")  /  goto("monster")  /  goto("fight <ENCOUNTER_ID>")   # waits until ready (your turn in a fight)
plan([...])                                                         # the test
look()  /  tail_log("ringdraft_log.txt")                            # results
stop()                                                              # always: closes copies, cleans the mods folder
```

The mods folder is the one holding the built `<Id>.dll` and `<Id>.json` (for Ring Draft: `workshop/content`). Solo:
`launch(players=1)` opens the main menu; `look` to see its buttons, then press through to a run in one plan.

## 4. Plan steps

```json
[
  {"console": "gold 300"},
  {"press": "Proceed"},
  {"play": "Bash", "target": "Cultist"},
  {"play": 0, "target": 0},
  {"end_turn": true},
  {"wait": "my_turn"},
  {"expect": {"me.gold": ">=300", "enemies.0.hp": "<40", "hand.count": 4, "screen": "~Combat"}},
  {"screenshot": "after Bash"}
]
```

| Step | Notes |
|---|---|
| `press` | button text ("Proceed", "Confirm"), node name ("ConfirmButton", "IRONCLAD_button") or an id from `buttons` |
| `play` | card name or hand index; `target` enemy index or name; `ally` player index for ally cards |
| `end_turn` | follow with `{"wait": "my_turn"}` (in co-op the round moves once every player ended) |
| `console` | any dev console command; fails the step if the game says it failed |
| `wait` | `my_turn`, `turn_started`, `turn_ended`, `combat_won`, `fight_over`, `room`, `screen`, `run_started`, `idle` (screen stopped changing), seconds, or `{"screen": "Merchant"}` |
| `expect` | paths into `look`: `me.gold`, `me.hp`, `me.energy`, `others.0.gold`, `enemies.0.hp`, `hand.count`, `hand.Strike`, `screen`, `room`; values: number, text, or `>=` `<=` `>` `<` `!=` `~` (contains) `!~`. Retried for 5 s |
| `look`, `screenshot`, `speed`, `banner` | include a look / a picture; change speed (every copy unless `player`); show text in the window |
| `handoff` | see below |

Every step takes `"player"` (`"client"`, `"1002"`), `"timeout"` (seconds) and `"optional": true`.

**Co-op**: run console commands once (they're synchronized). Presses and plays act as the player whose copy runs
them, so give each player's actions that copy: `{"play": "Neutralize", "target": 0, "player": "client"}`. Check sync
with `state(player="all")` or an `expect` per copy.

## 5. Stage: hand the moment to the person

1. Plan everything up to the step before the one that matters, at instant speed.
2. **Say in chat what's coming** ("I'll stop at the Cultist with Bash in hand; play it on the Cultist in the host
   window"), because the next call waits for them.
3. Run a plan ending in a hand-off:

```
goto("monster")
plan([ {"console": "card BASH"},
       {"handoff": "Play Bash on the enemy (host window)", "frames": 3, "timeout": 120} ])
```

The hand-off switches every copy to normal speed, shows the message as a banner in that window, waits for `until`
(an event type, `{"screen": ...}`, checks like `expect`, or by default any change on screen), lets it play out
(`settle` seconds, default 2; `frames` screenshots across it), clears the banner and restores the speed. If it times
out, tell them and offer to wait again.

Afterwards, report what you can still check: the log, numbers, and the frames if you captured them.

## 6. When something goes wrong

| Symptom | Do |
|---|---|
| launch notes it abandoned a saved co-op run | normal: every session ends mid-run and the game saves it in the test profile. Pass `keep_saved_run` to keep it (e.g. to test save/continue) |
| a client never joined | `relaunch(player="client")` |
| a step "waited 10s" | look / screenshot: wrong screen, a popup (press its button), or a different button text |
| the game asked to enable mods | stop, launch again (launch pre-enables mods for the test profiles) |
| something threw | `tail_log()` (that copy's game log) and your mod's own log |

Always `stop` when done; it closes the copies and restores the game's mods folder.
