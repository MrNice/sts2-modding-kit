# Changelog

## 0.0.1 (2026-10-15)

First draft.

- Skills: `sts2-inspect-game`, `sts2-version-audit`, `sts2-coop-mod-patterns`, `sts2-coop-testing`,
  `sts2-localize-zh`, `sts2-mod-release`.
- Tool: `sts2-inspect` 0.0.1 with `types`, `il`, `has`, `refs`, `typediff`, `ildiff`, `audit`, `pck list`,
  `pck extract`.
- Verified on game v0.107.1 and v0.111.0 (beta). Run against a co-op mod's 0.4.0 build, the audit reported the
  four breaks that build had on the beta: two compiled calls into changed members (one also a compile error),
  one patch target that became overloaded, and one reflection call whose parameters changed.
