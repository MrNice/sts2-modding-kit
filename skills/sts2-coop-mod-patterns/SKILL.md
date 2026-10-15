---
name: sts2-coop-mod-patterns
description: Proven patterns for Slay the Spire 2 mods, especially co-op ones - mod entry point, sending your own messages between players, deciding things the same way on every client, host-only lobby options, making new UI look native by reusing the game's nodes, and ending a run with your own victory or defeat screen. Use when designing or building a StS2 mod feature.
license: MIT
compatibility: Slay the Spire 2 mods (C# .NET 9, Godot 4.5 C#, Harmony). Verified against game v0.107.1 and the v0.111.0 beta.
metadata:
  version: "0.0.1"
---

# Slay the Spire 2 mod patterns

Each pattern here shipped in a published co-op mod. Member names are from game v0.107.1; check them against your
game version with `sts2-inspect has` (sts2-inspect-game skill) before relying on them.

## 1. Mod skeleton

A mod is a folder in `<game>/mods/<Id>/` with `<Id>.dll` and a manifest `<Id>.json`:

```json
{ "id": "MyMod", "name": "My Mod", "author": "you", "description": "...", "version": "0.1.0",
  "has_pck": false, "has_dll": true, "affects_gameplay": true }
```

The entry point is a static class marked `[ModInitializer(nameof(Init))]` (namespace
`MegaCrit.Sts2.Core.Modding`). In `Init`: load config, apply Harmony patches (one class at a time; see the
pitfalls reference in sts2-inspect-game), then add a long-lived controller node for per-frame work:

```csharp
NGame.Instance.CallDeferred(Node.MethodName.AddChild, new MyController()); // ProcessMode = Always in _Ready
```

Per-player files go in `OS.GetUserDataDir()` (`%APPDATA%/SlayTheSpire2`). Write your own log file there; players
can send it to you.

## 2. Co-op is the same simulation on every machine

Every client runs the full game logic (combat, rewards, RNG) for every player; inputs are synchronized. So:

- **Decide at points every client reaches in the same state**: `CombatManager.TurnEnded` (fires after each
  side's turn), the start of the fight-end code, room transitions. Every client computes the same answer with
  no messages and no lag.
- **Don't decide on frame timing.** "Two frames after X" differs between machines. If something must be decided
  between deterministic points, let the host decide and broadcast the result; clients apply it when it arrives.
- **Never mutate game state on one client only** (gold, deck, HP). That desyncs the run. Make the owning
  player's game perform the action through the game's normal flow (press the button, buy the item) so the game
  synchronizes it.

## 3. Your own messages between players

Add one message type to the game's co-op channel and put your protocol inside it:

```csharp
public struct MyModMessage : INetMessage, IPacketSerializable
{
    public string payload;                      // your own envelope, e.g. base64 of a versioned binary codec
    public bool ShouldBroadcast => true;
    public bool ShouldBuffer => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public void Serialize(PacketWriter w) => w.WriteString(payload ?? string.Empty);
    public void Deserialize(PacketReader r) => payload = r.ReadString();
}
```

- **Attach** when `RunManager.Instance.IsInProgress` and its `NetService.Type.IsMultiplayer()` and `IsConnected`:
  `net.RegisterMessageHandler<MyModMessage>(OnMessage)`; send with `net.SendMessage(new MyModMessage { ... })`.
  In the lobby, use the character select screen's `Lobby.NetService`. Unregister when the service changes.
- **Handlers can run off the main thread.** Queue messages (`ConcurrentQueue`) and handle them in `_Process`.
- **Version your envelope**: first byte = protocol version, reject unknown versions and trailing bytes. Players
  with different mod versions then ignore each other instead of misreading. Every message carries `From` and
  `To`; messages are broadcast, so non-recipients drop them. Check `From` against the transport's sender id.
- Players are identified by `Player.NetId`; the local player is `LocalContext.GetMe(state.Players)`; display
  names come from `PlatformUtil.GetPlayerName(net.Platform, id)`.

Keep the codec and rules in a plain .NET project with no game references, so they can be unit tested.

## 4. Host-only lobby options

The co-op lobby is `NCharacterSelectScreen` (postfix `InitializeMultiplayerAsHost` / `...AsClient`). Pattern:

- Only the host sees option controls; clients see the host's choice. The host broadcasts its pick, and
  re-sends it when someone joins (`StartRunLobby.Players` count changes).
- Gate the run start until the host has picked: prefix the private
  `StartRunLobby.BeginRunForAllPlayersIfAllReady` and return false until then.
- Remember the choice per run: in a prefix on `NCharacterSelectScreen.BeginRun(string seed, ...)` store
  `seed → option`. The run's `RunState.Rng.StringSeed` equals that seed, so a continued run finds its option.
  Have the host announce it again for the first seconds of every run, for clients that don't remember.

## 5. Make new UI look like the game made it

Reuse the game's nodes instead of drawing your own:

- **Duplicate** an existing node: `node.Duplicate((int)(DuplicateFlags.Groups | DuplicateFlags.Scripts |
  DuplicateFlags.UseInstantiation))`. Fix `%UniqueName` lookups by setting each descendant's `Owner` to the copy.
  Then call the original's own initializers through reflection so it animates natively (e.g.
  `NTopBarGold.Initialize(player)` gives you the real gold count-down for another player).
- **Recolour** with a small canvas_item shader rather than new art (keep luminance, swap hue). Character colours:
  `CharacterModel.NameColor`.
- **Text**: the game's title font is `res://themes/kreon_bold_glyph_space_two.tres`; gold text is
  `Color(0.937, 0.784, 0.318)` with outline `Color(0.098, 0.161, 0.188)`, outline size 12.
- Scale wrappers, positions and hover gotchas: see the pitfalls reference in sts2-inspect-game.

## 6. End the run your way

To end a run with your own result (e.g. a PvP winner) instead of the game's:

1. If a fight is running: `CombatManager.Instance.LoseCombat()` (marks the fight lost; the game processes it
   after the current action).
2. Stop music (`NRun.Instance.RunMusicController.StopMusic()`), optionally play
   `NAudioManager.Instance.PlayMusic("event:/temp/sfx/game_over")`.
3. `var save = RunManager.Instance.OnEnded(won); NRun.Instance.ShowGameOverScreen(save);`
4. Postfix `NGameOverScreen._Ready` to change the banner (`_banner.label.SetTextAutoSize(...)`) and quotes
   (`_deathQuote`, `_encounterQuote`). Reuse the game's own banner text where it fits
   (`LocString("game_over_screen", "BANNER.trueWin")`).
5. Don't let the run count if it shouldn't: prefix `RunManager.OnEnded` to skip progress, achievements and
   run history (still build the save with `ToSave(null)` and delete the run save), and prefix
   `NGameOverScreen.SaveBadgesToProgress`.
6. Make "game over" stick: postfix the `RunState.IsGameOver` and `RunManager.IsGameOver` getters to return true
   once you've ended it. Small getters can be inlined into some callers (see the pitfalls reference), so also
   handle the important path directly: prefix `RunManager.LocalPlayerDisconnected` so a player going back to
   the menu doesn't pull the others off their end screen with an error.

Note: if any player survives a fight, the game revives dead players at the end of it. Decide before that:
prefix `CombatManager.EndCombatInternal` (private; it has two overloads on the v0.111.0 beta, so patch every
overload with `TargetMethods()`).
