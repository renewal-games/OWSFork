## Build Preference

Do not start full builds, compiles, Unreal Editor builds, or long-running validation commands automatically. For code changes, prefer static review, targeted non-build checks, and clear notes about what was not compiled. Only run a build/compile when Julian explicitly asks for it or gives approval in that conversation.

## Project Paths

The backend OWS fork lives at `C:\Users\Julian\Documents\GitHub\OWSFork`. Check that location when backend, OWS, API, or server-side context would help with a task.

The Ragna Remake UE source lives at `C:\Users\Julian\Perforce\perforce_julian_7571\depot\ragna-remake\Source`. Check that location when Unreal gameplay, OWS plugin usage, PlayerCharacterBase, or client/server UE context would help with a task. The project root is `C:\Users\Julian\Perforce\perforce_julian_7571\depot\ragna-remake`.

## Data Access

**Postgres is the only supported database.** MSSQL and MySQL are not deployment targets. The repo is misleading here: `src/OWSData/Repositories/Implementations/MSSQL/` holds full implementations and `Databases/MSSQL/` and `Databases/MySQL/` hold migration scripts, but none of it ships, and Postgres-only features (such as `Characters.EconomyRevision` from the PlayerShops migration) exist in no other schema. Never weigh MSSQL/MySQL behaviour when judging a bug's severity or designing a change, and don't propose fixes that exist only for provider parity. When a shared interface changes, keep the MSSQL implementation compiling — a stub or a loud "not supported" failure is preferred over a silent degrade — but treat it as dead code, not a constraint. New data-access work may use Postgres-specific SQL freely.

Prefer parameterized SQL queries over stored procedures for new backend data-access work. Keep SQL injection protections explicit by binding parameters through the repository/data-access APIs instead of string-concatenating SQL.

## Version Control

Default to Perforce workflows in Julian's game projects. Use `p4 opened`, `p4 edit`, and `p4 diff` instead of assuming Git unless the current workspace is clearly a Git repo or Julian explicitly asks for Git.

## Agent Visibility and Handoffs

Read `docs/agent-workflow.md` before editing. Run `scripts/agents/Workspace.ps1 -Action Status` at the start of work. Register your session with `-Action Claim`, a unique session ID, task, and owned file/directory scope; update it at milestones and complete it with a handoff note. The script shares state across Git worktrees through the primary checkout's ignored `.agent-state` directory. This is a coordination record, not proof that a process is running or an exclusive file lock.

Check existing changes and active claims before editing. Preserve changes owned by Julian or another session. Use a separate worktree for overlapping work; record the actual worktree, branch, and session/thread ID. Do not infer that an idle, unloaded, or old session is finished. Never remove a worktree based on age alone.

Use `rg` for searches. Honor the repository ignore files; do not recursively search `.claude/worktrees` or `.agent-state` during normal code exploration. For an intentional worktree audit, target that worktree explicitly.

When delegation is explicitly requested, announce each subagent's task and scope, report blockers and completion, and consolidate its findings. Do not create subagents merely to populate the visibility register.

Finish with changed files, checks actually run, uncompiled/unvalidated areas, and remaining work. Broader tool permissions do not waive the Build Preference above.
