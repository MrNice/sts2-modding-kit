# sts2-modding-kit

Agent skills and a small inspection tool for making **Slay the Spire 2** mods, learned while building and
shipping [Ring Draft](https://github.com/MrNice/RingDraft), a co-op mod.

The skills follow the open [Agent Skills](https://agentskills.io) format (`SKILL.md` folders), so they work with
Claude Code, Codex CLI, Cursor, Copilot, Gemini CLI and other agents that read it. They're also plain Markdown,
readable by people.

> Fan-made. Not affiliated with or endorsed by Mega Crit. Nothing here contains game files; the tool reads your
> own installed copy.

## Skills

| Skill | Use it to |
|---|---|
| [`sts2-inspect-game`](skills/sts2-inspect-game/SKILL.md) | look inside the game: list members, read IL, check hook targets, pull scenes and text from the pack. Includes the `sts2-inspect` tool and the [patching pitfalls](skills/sts2-inspect-game/references/patching-pitfalls.md) |
| [`sts2-version-audit`](skills/sts2-version-audit/SKILL.md) | check a mod against a new game version or the beta branch before players hit the breaks |
| [`sts2-coop-mod-patterns`](skills/sts2-coop-mod-patterns/SKILL.md) | mod skeleton, messages between players, deterministic decisions, host-only lobby options, native-looking UI, custom run endings |
| [`sts2-coop-testing`](skills/sts2-coop-testing/SKILL.md) | test a co-op mod on one PC with two offline game copies |
| [`sts2-localize-zh`](skills/sts2-localize-zh/SKILL.md) | add Simplified Chinese using the game's own terms; Chinese store pages and change notes |
| [`sts2-mod-release`](skills/sts2-mod-release/SKILL.md) | version, change notes, zip, Steam Workshop (Mega Crit's ModUploader) and Nexus Mods |

## Install

Copy or symlink the folders under `skills/` to where your agent looks for skills, for example:

| Agent | Project skills | Personal skills |
|---|---|---|
| Claude Code | `.claude/skills/` | `~/.claude/skills/` |
| Codex CLI | `.agents/skills/` | `~/.agents/skills/` |

Other agents: see their documentation for the skills folder. Each skill folder is self-contained except that
the audit, localization and inspection skills share the tool in `skills/sts2-inspect-game/scripts/`.

## The tool

```
dotnet build skills/sts2-inspect-game/scripts/Sts2Inspect -c Release -o tools-out
dotnet tools-out/sts2-inspect.dll
```

Keep extracted game files (scenes, localization, IL dumps) outside this repository; don't commit or publish them.

Needs the .NET 9 SDK or later. No NuGet packages. Read-only. Run it with no arguments for usage; the skills
write `sts2-inspect` for `dotnet tools-out/sts2-inspect.dll`.

## Status

v0.0.1: first draft. Verified against game v0.107.1 (main) and v0.111.0 (beta), October 2026. The game is in
Early Access and changes often; member names in these skills will drift. Run `sts2-inspect has` before relying
on one, and please open an issue when something has moved.

## License

MIT (see [LICENSE](LICENSE)).
