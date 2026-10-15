---
name: sts2-version-audit
description: Check whether a Slay the Spire 2 mod still works on a new game version (a patch, or the beta branch) before players hit it - compile against the new game, find every member, patch target and reflection name that changed, and list changed game code the mod depends on. Use when the game updates or a player reports the mod broke on beta.
license: MIT
compatibility: Needs the .NET 9 SDK, the mod's source and a built copy of it, and both game versions' sts2.dll. Uses the sts2-inspect tool from the sts2-inspect-game skill (build it first; see that skill). Commands are shown for PowerShell or bash.
metadata:
  version: "0.0.1"
---

# Audit a mod against a new game version

A game update breaks mods in three ways, and the compiler only catches the first:

1. **Compile errors**: something the mod calls by name in code was renamed or removed.
2. **Runtime-only breaks**: the mod still compiles, but a *built* copy fails on the new game. A method's parameter
   or return type changed (`MissingMethodException`), a Harmony target became overloaded (the patch, or with
   `PatchAll()` the whole mod, fails to apply), or a name used through reflection disappeared.
3. **Behaviour changes**: everything resolves, but the game code the mod relies on now does something different.

This audit covers all three in a few minutes. Real result, game v0.107.1 → beta v0.111.0, on a co-op mod's
built 0.4.0: four breaks. Two compiled calls into members that changed (one of them also failed to compile), a
patch target that became overloaded, and a method called through reflection whose parameters changed. Only the
first would have shown up without the audit.

## Steps

### 0. Get both game versions' code

Copy `data_sts2_windows_x86_64/sts2.dll` (and `release_info.json`, for the version number) out of the install
**before** switching branches, into folders like `game/v0.107.1/` and `game/v0.111.0/`. Steam replaces files in
place when you change branch; keep the old copy or you can't compare. On Steam, betas are under the game's
Properties → Betas.

### 1. Compile against the new game

Point the mod's reference to the new `sts2.dll` and build. Usually the `.csproj` has an install-path property
(`-p:Sts2Dir=...`); otherwise copy the project and change the `HintPath`. Fix each error, preferring a fix that
works on *both* versions (see "One build for both versions").

### 2. Run the audit

Build the mod against the **old** game (that's the binary players have), then:

```
sts2-inspect audit <old/sts2.dll> <new/sts2.dll> <MyMod.dll built against old> <mod source folder>
```

It prints three sections:

- **`# 1` BROKEN references**: members the built mod calls that no longer exist with that exact signature. Each
  one throws the first time that code runs on the new game.
- **`# 2` name-based targets**: every `[HarmonyPatch(typeof(T), "Name")]`, `nameof(...)` patch target,
  `AccessTools.Method/Field/Property(typeof(T), "Name")` and `typeof(T).GetField("Name")` found in the source,
  compared across versions. `MISSING` and `CHANGED, NOW OVERLOADED` must be fixed. `CHANGED` with a new
  signature needs a look: a reflection `Invoke` with the old argument list will throw.
- **`# 3` changed game code** in the methods the mod calls or patches, with the first lines that differ, then a
  per-type count of other changed methods in those types.

Exit code 0 means sections 1 and 2 are clean, 3 means something needs fixing, 4 means the references are fine
but no source folder was given, so section 2 didn't run.

The source scan recognises `[HarmonyPatch(typeof(T), "Name")]`, `nameof(...)` targets, split class/method
`[HarmonyPatch]` attributes, `AccessTools.Method/Field/Property/PropertyGetter/PropertySetter(typeof(T), ...)`,
`AccessTools.Method("T:Name")` and `typeof(T).GetField/GetMethod/GetProperty("Name")`. It can't see: names built
at runtime, `Traverse.Create(x).Field("name")` (no type), `TargetMethods()` bodies, members of the game's nested
types, node paths and `%UniqueName` lookups. Search the source for those and check them by hand
(`sts2-inspect has` for members, the scene comparison below for nodes).

### 3. Read the behaviour changes

Go through section 3. Most changes are harmless (renumbered compiler-generated classes are already filtered
out). Look hard at:

- methods you patch (did the code you replace or wrap move?);
- methods whose results you rely on (new early returns, new guard checks, state moved to another object);
- new events or hooks that make a patch unnecessary.

Dig into one with `sts2-inspect ildiff <old> <new> 'CombatManager::LoseCombat'` (the pattern is a regex on
`Type::Method`; an async method's body is `'CombatManager\+<Foo>d__N::MoveNext'`), or read both versions with
`il`.

### 4. Check the scenes you touch

If the mod copies, positions or looks up game nodes, compare those scenes:

```
sts2-inspect pck extract <old SlayTheSpire2.pck> 'scenes/ui/(top_bar|confirm_button)' old-scenes/
sts2-inspect pck extract <new SlayTheSpire2.pck> 'scenes/ui/(top_bar|confirm_button)' new-scenes/
diff -r old-scenes new-scenes        # PowerShell: git diff --no-index old-scenes new-scenes
```

Extract into a folder outside your mod's repository (game files must not be committed or published).

Watch for renamed nodes, changed `unique_name_in_owner`, offsets and `pivot_offset`. (Like `sts2.dll`, save the
old `.pck` before switching branches, or list its sizes with `pck list` for a quick did-it-change check.)

### 5. Test in game

Statically clean is not tested. Play the specific features the audit flagged, and read the mod's log for
errors. For co-op mods, see the sts2-coop-testing skill.

## One build for both versions

Players on main and on beta can share one build if the mod tolerates both shapes:

- **Overloads**: patch with `TargetMethods()` returning every overload (see the sts2-inspect-game skill).
- **Changed signatures called by reflection**: look at `method.GetParameters()` and pass what this version
  wants.
- **Members that moved or changed type**: read them by name and accept either:
  ```csharp
  // v0.107: RunLobby.ConnectedPlayerIds; v0.111 beta: PlayerIds
  foreach (var name in new[] { "ConnectedPlayerIds", "PlayerIds" })
      if (lobby.GetType().GetProperty(name)?.GetValue(lobby) is IEnumerable<ulong> ids) return ids.ToHashSet();
  ```
- **Prove it**: build against each version and diff the game members each build references:
  `sts2-inspect refs build-old.dll > a.txt`, then `sts2-inspect refs build-new.dll > b.txt`, then compare the
  two files (`diff a.txt b.txt`, or `git diff --no-index a.txt b.txt`).
  Identical output means the old build resolves the same members on the new game.

Then you can ship one file instead of a separate "(Beta)" mod.
