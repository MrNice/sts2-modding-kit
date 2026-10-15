# Harmony patching pitfalls in Slay the Spire 2

Collected from shipping a co-op mod against game v0.107.1 and the v0.111.0 beta. Each one cost real time.

## A patch (or the whole mod) doesn't apply

`Harmony.PatchAll()` stops at the first patch class it can't apply and throws, so every patch after it is
skipped and the mod is effectively off. Causes, most common first:

1. **Ambiguous name.** `[HarmonyPatch(typeof(T), "Foo")]` when `T` has two `Foo` overloads. Games add overloads in
   updates (v0.111.0 added `CombatManager.EndCombatInternal(CombatTurnState)` next to `EndCombatInternal()`).
   Fix: `[HarmonyPatch(typeof(T), "Foo", new[] { typeof(Bar) })]`, or, to cover every overload across versions:
   ```csharp
   [HarmonyPatch]
   static class MyPatch
   {
       static IEnumerable<MethodBase> TargetMethods() =>
           AccessTools.GetDeclaredMethods(typeof(T)).Where(m => m.Name == "Foo");
       static bool Prefix(ref Task __result) { ... }
   }
   ```
2. **Renamed or removed member.** The name you patched is gone. `sts2-inspect has` tells you before players do.
3. **Wrong parameter name in the patch signature.** Use `__0`, `__1` (positional) instead of the game's
   parameter names, which can change between versions.

Better still, don't use `PatchAll()`: patch each class on its own, so one broken target only loses that one
feature, and log the count so a partial failure shows up:

```csharp
var harmony = new Harmony("you.yourmod");
int ok = 0, failed = 0;
foreach (var type in typeof(ModEntry).Assembly.GetTypes())
{
    if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
    try { harmony.CreateClassProcessor(type).Patch(); ok++; }
    catch (Exception ex) { failed++; Log.Error($"Patch {type.Name} failed", ex); }
}
Log.Info($"Harmony: {ok} patch classes applied, {failed} failed");
```

## The patch applies but never runs

- **Inlining.** The JIT can inline small methods (one-line getters like `IsVictoryRoom`) into callers compiled
  after your patch. Harmony patches the method body, not the copies inlined elsewhere. Patch something bigger
  (the caller), or change the state the small method reads, or postfix the screen that consumes the value.
- **A postfix on an async method runs too early.** `async Task Foo()` returns at its first `await`, so a postfix
  runs then, not when the work finishes. Wrap the returned task instead: in the postfix, replace `__result` with
  your own `async` method that awaits the original task and then does your work.
- **A different overload is called.** Find the callers in a full IL dump (search for `T::Foo`; calls show up as
  both `call` and `callvirt`) and see which signature they use.

## Reflection that breaks quietly

`AccessTools.Field(typeof(T), "_name")`, `AccessTools.Method(...)`, `Traverse.Field(...)` and
`type.GetField("_name")` compile fine and fail at runtime (null, or an exception you catch and log). Keep every
name-based access in a small number of files, and run the version audit on each game update.

Compiled calls can break too: if the game changes a method's parameter or return type (v0.111.0 changed
`StartRunLobby.Players` from `List<LobbyPlayer>` to `List<StartRunLobbyPlayer>`), a mod built against the old
game throws `MissingMethodException` the first time that line runs. To make one build work on both versions,
read such members by name (`GetProperty("Players")?.GetValue(x) as ICollection`) and handle both shapes.

## Godot node gotchas

- **`%UniqueName` lookups fail on duplicated nodes.** `Node.Duplicate()` copies children but unique names resolve
  through the node's *owner*. After duplicating, set every descendant's `Owner` to the copy and re-enable
  `UniqueNameInOwner` on the named ones, before adding it to the tree.
- **Buttons that position themselves.** `NConfirmButton` stores an offset from the screen's bottom-right corner
  in `_ready` (`_posOffset`) and slides between computed show/hide positions; setting `Position` yourself gets
  overwritten. Set `_posOffset` and call `OnWindowChange()`.
- **Hover animations set absolute scale** (`NConfirmButton` sets `Scale` to 1.05, then back to 1). A button you
  scaled to 0.6 jumps to full size on hover. Put it inside a scaled parent `Control` instead of scaling the button.
- **Children scale with their parent.** A label added as a child of a pulsing button pulses too. Add it as a
  sibling and copy the button's position each frame.

## Things that look like bugs but aren't

- **Co-op runs the same simulation on every client.** Game state is deterministic across players; a decision
  made at a deterministic point (end of a turn, end of a fight) comes out the same everywhere without messages.
- **Dead players are revived at the end of a fight** (`Player.ReviveBeforeCombatEnd` in the fight-end code) if
  any player survived. Mods that care about deaths must decide before that.
- **`RunState.IsGameOver` means every player is dead**, not "the run ended".
