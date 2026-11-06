# Agent workflow

`AGENTS.md` is the shared project policy. `CLAUDE.md` imports it for Claude. Keep both in version control so new checkouts receive the same rules. Julian's personal defaults also live in `~/.codex/AGENTS.md`; project policy must not depend on that file being installed.

## Start, update, and hand off

Run these commands in PowerShell 7 from the checkout being edited. Use the real thread/session ID when available, or generate a unique descriptive ID. Replace the example values.

```powershell
./scripts/agents/Workspace.ps1 -Action Status
./scripts/agents/Workspace.ps1 -Action Claim -SessionId 'example-thread-id' -Owner 'Codex' -Task 'Describe the requested change' -Scope 'src/OWSData/path/to/file.cs' -Note 'Initial status inspected; existing changes belong to another session.'
./scripts/agents/Workspace.ps1 -Action Update -SessionId 'example-thread-id' -Note 'Implementation finished; targeted checks next.'
./scripts/agents/Workspace.ps1 -Action Complete -SessionId 'example-thread-id' -Note 'Changed X; checked Y; not compiled; remaining work Z.'
```

Use `Update -State blocked` or `Update -State paused` with a reason when appropriate. Only update your own record. Keep one writer per session ID. A new task gets a new ID; completed records remain as handoff history.

The register is **local and shared across this clone's Git worktrees**: the script resolves `git rev-parse --git-common-dir`, then uses `.agent-state/sessions` beside that `.git` directory. It is intentionally ignored by Git. Each session has its own JSON file, avoiding one frequently edited shared document. Other machines/clones need their own register and should use PRs/issues for durable cross-machine handoffs. Do not put credentials or private conversation transcripts in records.

Read active claims and Git status before editing. Scopes are advisory, not filesystem locks; coordinate overlaps or use another worktree. Refresh notes at milestones and before handing control back. An old timestamp means the record needs verification, not that its work can be deleted. The register does not automatically discover agents or prove process liveness.

Codex's app task list and Subagents UI provide separate runtime evidence. `active`, `idle`, and `notLoaded` are not interchangeable. The session's subagent list only covers that thread tree. Other tools, Claude sessions, and other app servers may not appear. Record the source and timestamp of any runtime observation.

## Worktree review

```powershell
./scripts/agents/Workspace.ps1 -Action Status -Json
```

This is a local, non-build check. It reports changes, branch/head, commits ahead/behind local `main`, patch equivalence, guidance presence, and known session claims. It does not fetch, modify branches, or delete worktrees. Errors remain visible and cause a nonzero exit. A normal checkout with a `.git` directory and local `main` is required.

Before cleanup, verify the owner is finished, check tracked **and untracked/ignored** files for valuable local state, and confirm integration against the intended target. Patch equivalence only identifies equivalent patches; it does not establish that a checkout is unused. Use `git worktree remove` only after those checks and appropriate authorization. Retain branches with unique work. Do not force-remove dirty worktrees or automatically prune on age.

## Searches and existing worktrees

`.gitignore`, `.rgignore`, and Serena exclude `.claude/worktrees` and `.agent-state` from ordinary searches. Do not use `rg --no-ignore` or `rg -uuu` across the root unless the task calls for ignored content. Audit a secondary checkout by targeting its path directly.

Existing branches created before the guidance was tracked do not automatically gain these files. For those checkouts, read the primary checkout's `AGENTS.md` and this workflow, and invoke its script with `-Repository '<secondary-checkout>'`. A local bootstrap copy of `AGENTS.md`/`CLAUDE.md` can point to the primary checkout until the shared files are integrated; do not overwrite existing guidance. New worktrees should be based on a commit containing the shared policy.

## Permissions

The project Codex configuration selects `ows-development`: workspace writes, explicit Git metadata writes, and network access. Writes outside the selected workspace still require approval. The `.codex`, `.agents`, and `.aws` protections inherited from `:workspace` remain. `approval_policy = 'on-request'` allows escalation when a task needs access beyond those boundaries.

Project settings apply only to trusted projects and can be overridden by the app's selected permission mode or managed requirements. Reload/start a session and inspect its effective permissions; a file edit alone does not change an already-running sandbox. Select the named profile in the permission control if the client exposes it. Do not claim Full access is confined to this repository.

Windows ACL inheritance and Git ownership checks are separate from the permission profile. This repo's `src` had inheritance disabled during the 2026-11-05 audit; the requested repair restores inheritance from the repo. Any repair backup belongs under the ignored `.agent-state/permissions` directory. Never remove sandbox-generated deny ACLs manually to bypass the current session policy.

The repair also added exact `safe.directory` entries for the 11 registered checkouts to Julian's Git configuration; it did not add a wildcard trust exception. Added paths are listed in `.agent-state/permissions/git-safe-directories-added.json`, and the original `src` ACL is saved in `src-before.sddl` in the same directory. Plain sandboxed Git status and source-file discovery succeeded after the repair.

Wider permissions do not authorize builds. Report static checks and anything left uncompiled or unvalidated, as required by `AGENTS.md`.

## Baseline audit: 2026-11-05

Compared with local `main` at `7ada56abd02089354bf7082f9a660111a99772e9`, without fetching. All ten secondary worktrees were clean under `git status --short`; ignored contents were not inventoried for deletion. Ownership is unconfirmed for all pre-existing secondary worktrees.

| Checkout | Behind / ahead main | Patch result | Disposition |
| --- | --- | --- | --- |
| `OWSFork-kairo/issue-58` | 13 / 1 | 1 unique commit; message says the prior fix was dropped | Retain; review owner and intended outcome |
| `OWSFork-kairo/issue-93` | 23 / 1 | 1 unique patch | Retain unfinished/unintegrated work |
| `OWSFork-kairo/issue-97` | 23 / 0 | Ancestor of main | Cleanup candidate after ownership/local-state review |
| `OWSFork-kairo/merge-queue-ci` | 42 / 1 | 1 equivalent patch | Cleanup candidate after ownership/local-state review |
| `OWSFork-pgss` | 0 / 1 | 1 unique patch | Retain unfinished/unintegrated work |
| `.claude/worktrees/bcrypt-passwords` | 42 / 1 | 1 equivalent patch | Cleanup candidate after ownership/local-state review |
| `.claude/worktrees/cd-hetzner-deploy` | 42 / 1 | 1 unique patch | Retain unfinished/unintegrated work |
| `.claude/worktrees/ci-hygiene` | 45 / 1 | 1 unique patch | Retain unfinished/unintegrated work |
| `.claude/worktrees/persistence-service-key` | 42 / 1 | 1 unique patch | Retain unfinished/unintegrated work |
| `.claude/worktrees/stress-bot-purge` | 6 / 4 | 4 unique patches | Retain unfinished/unintegrated work |

Main had pre-existing changes in `docs/hosting/thailand-demo.md`, `src/OWSCharacterPersistence/OWSCharacterPersistence.xml`, and `src/OWSInstanceLauncher/appsettings.json`, plus untracked map assets and nested Claude worktrees. This hygiene task preserves those changes.

The app-level recent-task snapshot showed this hygiene task active, the Ragna process discussion idle, and numerous historical OWS fix/review tasks unloaded. That snapshot was bounded and did not establish a mapping from every task to its worktree. No task was stopped or archived and no worktree was deleted.

After the baseline, all ten secondary worktrees received missing local `AGENTS.md` and `CLAUDE.md` entry points, with explicit routing to the primary checkout's canonical policy and workflow. The 20 created paths are listed in `.agent-state/bootstrap-files.json`. These bootstrap files are not integrated into the old branches: `CLAUDE.md` now appears as an intentional untracked file there; the old branches still ignore `AGENTS.md`. Preserve or replace those files deliberately when integrating the shared guidance.
