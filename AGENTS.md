# Optiscaler-Client (shyoo fork) — agent guide

How to work in this fork. Read [`HANDOFF.md`](HANDOFF.md) first: it is the only file that carries
current state. [`README.md`](README.md) is upstream's own and describes the product.

**This file, `HANDOFF.md`, `fork/` and the marked block at the end of `.gitignore` are fork-only.
They must never reach an upstream pull request.** This repository is public, so nothing committed
here may contain secrets, tokens, or personal machine paths.

## What this fork is for

- Upstream: `origin` = https://github.com/Optiscaler-Client/Optiscaler-Client (maintainer:
  Agustinm28; integration branch `general`, releases from `main`).
- Fork: `fork` = https://github.com/shyoo/Optiscaler-Client.
- Current goal: add **dlssg_for_sm86** (DLSS Frame Generation on RTX 20/30) as a standalone
  component, as agreed in upstream issue #103. The plan is
  [`fork/PLAN-dlssg-sm86.md`](fork/PLAN-dlssg-sm86.md).

## Branch model

| Branch | What it holds |
|---|---|
| `general` (fork, local) | Upstream `general` **+** the fork-only files **+** finished fork work not yet upstream. Warmstart task branches land here. |
| `warmstart/t*` | One task each. Cut from `general`, landed back onto it. |
| `pr/<topic>` | One upstream PR each. Cut from **`origin/general`** and carrying **product commits only**. Pushed to `fork`, opened against `Optiscaler-Client:general`. |

- **Syncing with upstream:** `git fetch origin`, then on `general` run `git merge origin/general`.
  Merge, don't rebase: `general` is pushed to a public fork, so no history rewrites and no force
  pushes.
- **Making a PR branch:**
  ```sh
  git fetch origin
  git switch -c pr/<topic> origin/general
  git cherry-pick <product commits, oldest first>
  # guard: must print nothing
  git diff --name-only origin/general...HEAD | grep -E '^(AGENTS\.md|HANDOFF\.md|fork/)'
  git diff origin/general...HEAD -- .gitignore   # must not contain the fork-local block
  ```
  Once a PR is merged upstream, the next `git merge origin/general` brings the same change back
  into `general` (git resolves identical cherry-picks cleanly in most cases).

## Fork-only files

- `AGENTS.md`, `HANDOFF.md`: this guide and the current state.
- `fork/**`: plans, notes, dev scripts (e.g. the dlssg_for_sm86 manifest pinning script).
  **Never put `.cs` files here.** The root `OptiscalerClient.csproj` compiles every `**/*.cs`
  under the repo root, so they'd be built into the app.
- The block at the end of `.gitignore`, between the `fork-local` markers.

**Never mix fork-only paths and product code in one commit.** Start fork-only commit subjects with
`fork:` (e.g. `fork: update handoff`), so they're easy to leave out when cherry-picking. When a
task touches both, make two commits; don't squash them together.

## Before you start

- **.NET 10 SDK** is required (`net10.0`). As of 2026-10-03 the dev machine has only 9.0.304, so
  the build fails with NETSDK1045 until 10.x is installed.
- Windows is the only platform this fork's feature targets. Linux must keep building, and the new
  UI must be hidden there.

## Checks

Run these and make them pass before you report done:

- `dotnet build OptiscalerClient.csproj -c Debug`: no errors and no new warnings.
- Docs-only changes (fork files, markdown): no build needed.

## When you commit

- Commit style follows upstream: lowercase conventional subjects (`feat: …`, `fix: …`,
  `docs: …`, `style: …`); fork-only work uses `fork: …`. Explain *why* in the body.
- One coherent commit per task where it is safe to squash. Keep fork-only and product commits
  separate (see above).
- Update [`HANDOFF.md`](HANDOFF.md) in a `fork:` commit alongside the work: what changed, what is
  next.
- ⛔ Never commit build output, downloaded mod binaries (`*.dll` from dlssg_for_sm86 or any
  other upstream), credentials, or anything gitignored.

## Upstream conventions (maintainer, issue #103)

- Logic in **Services**, not Views. Async disk and network work, off the UI thread.
- **No hardcoded user-facing strings.** Every new key goes into **all 14**
  `Languages/Strings.*.axaml` files. Read them with `GetResourceString(key, fallback)` as the
  existing code does.
- Platform-specific logic in its own service; Windows-only features are **hidden** on Linux, not
  disabled.
- Reuse what exists: `GpuSelectionHelper` (GPU gating), `BackupStoreService` (backups),
  `AntiCheatHelper`, `NetworkService` / `HttpRetryHelper` (downloads, proxy-aware),
  `PlatformServiceFactory` (platform services), global styles and resources in
  `App.axaml` / `Styles/`.
- New `.cs` files carry the same GPL-3.0-or-later header as existing ones.
- The maintainer's text mentions MVVM and a DI container, but the code has neither (services are
  created with `new`; Views are code-behind). Follow the existing pattern unless the maintainer
  answers otherwise (plan §9 Q1).

## Pitfalls

- **One backup manifest per game, and `UninstallOptiScaler` deletes the whole
  `Backups/<slug>/`.** Anything that has to survive an OptiScaler uninstall can't live in that
  manifest. The FSR 4 swap lives there on purpose; the dlssg_for_sm86 component must not (plan
  §4.5).
- The backup store key is the directory OptiScaler was actually installed into, which isn't
  always `game.InstallPath` (see `BackupStoreService.FindBackupDirUnder`).
- `Views/ManageGameWindow.axaml.cs` is about 7.9k lines and changes often upstream. Put new
  sections in their own partial file (`ManageGameWindow.<Feature>.cs`) to keep merges cheap.
- The csproj has `InternalsVisibleTo("Optiscaler-Client.Tests")`, but no test project is in the
  repo.
