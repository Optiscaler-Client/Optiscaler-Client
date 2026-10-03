# dlssg_for_sm86 support — implementation plan

Fork-only document (see [`AGENTS.md`](../AGENTS.md) → "Fork-only files"). It is never part of an
upstream PR.

- Upstream issue: https://github.com/Optiscaler-Client/Optiscaler-Client/issues/103
- Mod: https://github.com/sdli1995/dlssg_for_sm86
- Written 2026-10-03 against client `general` @ `f73cf2c` (v1.0.8) and mod tag `0.3.5`.

---

## 1. What the maintainer agreed to (issue #103, Agustinm28)

| Topic | Agreed |
|---|---|
| Shape | **Standalone component first**, like the FSR 4 DLL swap; doesn't need OptiScaler installed. Nvngx FG provider integration comes later, in its own PR. |
| Scope | RTX 20 / 30 (Turing / Ampere), **Windows only**, D3D12 games that **already ship DLSS-G** (Streamline). |
| Target branch | PRs against **`general`**. |
| Install source | Download from upstream **at a pinned tag**, hash + signature verified. **No mirroring.** |
| Backups | Use the **existing backup store** for install and uninstall. |
| Linux | **Hide the section entirely** (don't show it disabled). Platform logic in **its own service**. |
| GPU gating | Reuse **`GpuSelectionHelper`** to limit to Turing / Ampere. |
| Proxy names | Skip names already used by OptiScaler or other mods. |
| UI | Manage Game window: install / update / uninstall, multiplier cap, 310.9 vs 310.1 build, HAGS warning. Must look native (global styles, existing controls, section layout). |
| Diagnose button | Liked, **may be a follow-up PR**. |
| Auto-selection | End goal, **follow-up PR**: on RTX 20/30, pick this as the FG option when the game qualifies, otherwise fall back to the current recommendation. Hook: `FrameGenerationConfigurationService.GetRecommendation`. |
| Conventions | MVVM (no logic in Views); services registered in the DI container; async disk and network work off the UI thread; **no hardcoded user-facing strings**, and every new string goes into **all 14** `Languages/Strings.*.axaml`. |

## 2. Where the codebase doesn't match the stated conventions — ask before coding

What the code actually does (checked at `f73cf2c`):

- **No DI container and no ViewModels.** Services are created with `new` at the call site, e.g.
  `new ComponentManagementService()` and `new GameInstallationService()`.
  `PlatformServiceFactory` is the only factory, and it's static. Views are Avalonia code-behind
  using `FindControl<T>`. `ManageGameWindow.axaml.cs` alone is about 7.9k lines.
- **No test project in the repo**, although the csproj has
  `InternalsVisibleTo("Optiscaler-Client.Tests")`. The maintainer probably keeps one outside the
  repo.

Proposed approach, to confirm on the issue (§9 Q1–Q2):
- All logic lives in the new service(s). The Manage window gets a thin code-behind section that
  only binds and forwards clicks, or a small `DlssgSm86SectionViewModel` if the maintainer wants
  MVVM to start here.
- Create the service through `PlatformServiceFactory.CreateDlssgSm86Service()`, which returns
  `null` off Windows, so the section is hidden. This matches how GPU and gamepad services are
  created. Only bring in `Microsoft.Extensions.DependencyInjection` if the maintainer asks.

## 3. Upstream facts, checked 2026-10-03 (these replace the older brief)

- **Tags** `0.1.0`, `0.3.0`–`0.3.5`. **No GitHub Releases.** The docs mention release zips
  (`dlssg-release-x64.zip`, `dlssg-release-x64-310.9.zip`), but none are published. Download
  each file at a pinned **commit SHA**, because tags can move:
  `https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/<commit>/<path>`.
  - `0.3.5` → commit `9621db573e07ed54f50c15bbb585ed9a7bdfac28`.
- **Repo layout at 0.3.5:** the root holds the **310.9** build: `version.dll` (~30 MB),
  `dlssg_sm86.ini`, and `alternatives/{winmm,dbghelp,dinput8,dxgi,d3d12}.dll`. `310.1/` holds the
  older build in the same layout. The docs describe a *package* with four utility proxies at its
  root; the repo keeps three of them under `alternatives/`. **VERIFY the per-build file list when
  pinning.**
- **Multi-proxy design:** `version.dll`, `winmm.dll`, `dbghelp.dll` and `dinput8.dll` can all be
  installed together. The first one the game loads becomes *active* and the others become
  *standby* (forwarding only). `dxgi.dll` and `d3d12.dll` are on the render hot path and
  load-order sensitive, so they're manual only. **v1 never installs them.**
- **Signing:** self-signed `CN=DLSSG for SM86 (self-signed)`, SHA-1 thumbprint
  `85BA66762F851E49148D706915D09026281418E6`. Windows won't trust it, so check the thumbprint
  (`X509Certificate.CreateFromSignedFile`) rather than `WinVerifyTrust`.
- **The README no longer publishes SHA-256s.** Trust has to come from a manifest we pin
  ourselves (§4.2).
- **Factory INI** (`dlssg_sm86.ini`):
  `[General] Enabled=1`, `[FrameGeneration] Optimized=1 MaxGeneratedFrames=3`,
  `[Compatibility] Preset=Auto`, `[Logging] Level=1 Directory=dlssg_sm86\logs`,
  `[Runtime] Mode=Bundled CacheDirectory=`.
  - `MaxGeneratedFrames`: 1=2X, 2=3X, 3=4X (default), 5=6X (310.9 only; 310.1 clamps it to 3).
  - `Optimized` 0–3. 1 is the default and bit-identical; 2 and 3 are lossy, and 2 is 310.9 only.
  - Bad keys fall back to their own defaults, so INI edits are low-risk.
- The old `Router` / `KernelImage` keys are now advanced or diagnostic only. **Don't expose them
  in v1.**
- **Runtime cache:** the mod extracts files to `%LOCALAPPDATA%\DlssgSm86\bundles\<id>`. Upstream
  says that cache can stay on uninstall. Logs go to `<exeDir>\dlssg_sm86\logs\`
  (`loader_<pid>.jsonl`, `backend_<pid>.jsonl`).
- **Diagnose data** (for the later PR): `fg_gate_environment` (`hags.state=on|off|unsupported`,
  driver, Streamline modules), `fg_gate_requirements` (bit 8 = HAGS off),
  `fg_gate_capability`, `fg_gate_create_feature` (`0xbad0000b` = out-of-date / arch) and
  `fg_gate_summary` (`create_calls=0` means the game never asked for FG). Also: does
  `backend_*.jsonl` contain an `install` line with `route active=true`?
- **Since 0.3.3 the mod spoofs Blackwell** to Streamline (`SpoofArchToGame`), so games on
  Streamline 2.8 can unlock 3X/4X/6X. The "the game's Streamline version caps the multiplier"
  rule from the FF16 notes still applies to older plugins (4X-only plugins can't go to 6X).
- **License:** the mod's source is GPLv3, but the embedded `nvngx_dlssg` runtime is NVIDIA
  material. That's one more reason never to mirror or bundle it.

## 4. Design

### 4.1 New files (each new `.cs` gets the GPL header that other files use)

| File | Role |
|---|---|
| `Models/DlssgSm86.cs` | `DlssgSm86Build` (`V310_9`, `V310_1`), `DlssgSm86Multiplier` (2X/3X/4X/6X → `MaxGeneratedFrames` 1/2/3/5), `DlssgSm86Eligibility` (Eligible / NotWindows / GpuNotSupported / NoDlssG / NotDx12 / AntiCheat / NoFreeProxyName, plus detail), `DlssgSm86PackageManifest` (pinned JSON shape) and `DlssgSm86InstallState`. |
| `assets/configs/dlssg_sm86_manifest.json` | Pinned upstream: `version`, `commit`, `signerThumbprint`, and `builds[]`. Each build has `files[]` = `{ path, role: proxy\|ini, proxyName, size, sha256, gitBlobSha }`. Copied to output like the other `assets/configs/*.json`. |
| `Services/DlssgSm86PackageService.cs` | Reads the pinned manifest. Downloads into the cache (`AppPaths` cache root → `DlssgSm86/<version>/<build>/`) through the existing `NetworkService` / `HttpRetryHelper` (proxy settings respected), with progress. Checks **size + SHA-256** against the manifest, and the Authenticode signer **thumbprint** for every DLL. **Deletes and refuses** on any mismatch. Lists and deletes cached versions for Cache Management. |
| `Services/DlssgSm86Service.cs` (`[SupportedOSPlatform("windows")]`) | Eligibility, target directory, proxy-name selection, install / update / uninstall through the backup store, INI read/write, HAGS read. Created by `PlatformServiceFactory.CreateDlssgSm86Service()`, which returns `null` off Windows. |
| `Helpers/GpuSelectionHelper.cs` (edit) | Add `IsTuringRtx(gpu)` and `IsAmpere(gpu)`, matching the regex style of `IsBlackwell`. RTX 20xx, TITAN RTX and Quadro RTX are Turing; RTX 30xx and RTX A-series (A2000–A6000, not "RTX 2000 Ada") are Ampere. GTX 16xx is Turing but **has no tensor cores, so exclude it**. Laptop and Ti suffixes are fine. |

### 4.2 Pinning and verification

- Add a dev script (fork-only, `fork/tools/pin-dlssg-manifest.sh`) that resolves a tag to its
  commit, lists the tree through `gh api`, downloads each needed file, and writes `size`,
  `sha256` and `gitBlobSha`. It also checks the signer thumbprint with PowerShell
  `Get-AuthenticodeSignature`, which reports `UnknownError` for this cert; that's expected, so
  compare the thumbprint only. Only the JSON it generates goes upstream.
- At runtime, trust comes **only** from the shipped JSON. Not from the GitHub API, and not from
  the README. Moving to a new mod version means a client change that updates the manifest.
  "Update available" in v1 therefore means "the installed version differs from the pinned one".

### 4.3 Eligibility (computed in the service; the View only shows the result)

1. `OperatingSystem.IsWindows()`. Otherwise the factory returns null and the section is hidden.
2. Preferred GPU (`GpuSelectionHelper.GetPreferredGpu` with the configured default GPU id) is
   Turing RTX or Ampere. Otherwise **hide the section** (to confirm in §9 Q3) and show why in a
   tooltip.
3. The game ships DLSS-G: `game.DlssFrameGenVersion` is set, or `nvngx_dlssg.dll` /
   `sl.dlss_g.dll` exists. Reuse `FrameGenerationConfigurationService.DetectCapabilities`
   (`HasNativeDlssG && IsDirectX12`) rather than scanning again.
4. No anti-cheat: `AntiCheatHelper.IsPresent(game.InstallPath)`. Block and show the existing
   anti-cheat warning style.
5. At least one utility proxy name is free (§4.4).

### 4.4 Target directory and proxy names

- Target directory = the directory of the rendering exe. Start from
  `GameInstallationService.DetermineInstallDirectory(game)`, which already handles UE / Phoenix
  layouts, since that's where OptiScaler's proxy goes. **VERIFY** that it equals the exe
  directory for UE games where `sl.interposer.dll` sits under `Engine\Plugins\…`. The mod belongs
  next to the exe.
- Candidate names, in order: `version.dll`, `winmm.dll`, `dbghelp.dll`, `dinput8.dll`. Skip a
  name if:
  - OptiScaler's committed manifest uses it as `InjectionMethod`, or lists it in
    `FilesCreated` / `FilesOverwritten`;
  - a file with that name exists and isn't ours (a game file, an ASI loader, ReShade, Special K,
    another mod). Record what owns it where it's recognisable (e.g. an ASI loader if `*.asi`
    files exist);
  - it collides with a FSR 4 swap entry.
- Install **every free utility name** (upstream's active/standby design). If **none** is free,
  report `NoFreeProxyName` with the occupying files listed. v1 has no `dxgi.dll` / `d3d12.dll`
  fallback.
- **Reverse collision:** `GameInstallationService.InstallOptiScaler` has to stop treating our
  proxies as originals. Otherwise it backs them up and later "restores" them, or overwrites them
  when the chosen `InjectionMethod` is one of our names. Guard: if the chosen injection method is
  a DLSSG proxy name, refuse with a clear message (or re-pick a name — see §9 Q4), and leave our
  files out of its backup and residue logic.

### 4.5 Backups — a separate component-scoped record in the backup store

The trap: there is **one manifest per game** (`Backups/<slug>/manifest.json`), and
`UninstallOptiScaler` restores it and then calls `DeleteBackup`, which **removes the whole
`Backups/<slug>/`**. The FSR 4 swap can share that manifest only because uninstalling OptiScaler
is also meant to undo the swap. The DLSSG component must survive an OptiScaler uninstall, and
uninstalling DLSSG must leave OptiScaler alone.

- Extend `BackupStoreService` with an optional component scope:
  `GetBackupRoot(gameDir, component: "dlssg_sm86")` →
  `Backups/_components/dlssg_sm86/<slug>/`, using the same `manifest.json` + `files/` layout.
  This is a sibling of the per-game roots, so OptiScaler's `DeleteBackup`, `FindBackupDirUnder`,
  residue detection and legacy migration are **unchanged**. Make sure `FindBackupDirUnder` and the
  other slug enumerations skip `_components`.
- Reuse `InstallationManifest` / `ManifestFileRecord`, with `IncludesOptiscaler = false` and new
  fields `DlssgSm86Version`, `DlssgSm86Build` and `DlssgSm86ProxyNames`. Or use a small dedicated
  record; decide while coding and keep `OperationStatus` `in_progress` → `committed`.
- **Install:** stage verified files beside the targets as `*.dlssg_tmp`. Back up every
  pre-existing target (normally none, since taken names are skipped). Then move the staged files
  into place, write the manifest, and mark it committed. **Roll back on any failure**: restore
  backups and delete created files.
- **Uninstall:** for each record, if the current SHA-256 equals `PostInstallSha256`, delete the
  file or restore its backup. If it differs (the user edited the INI, or something else replaced
  the proxy), ask first. Delete `dlssg_sm86\logs` only when the user opts in. Remove the
  component backup root at the end. Leave `%LOCALAPPDATA%\DlssgSm86\bundles` alone.
- **Update** (different pinned version, or a build switch 310.9 ↔ 310.1): replace the proxies,
  carry the user's INI values over to the new factory INI, and update the manifest.
- `GameAnalyzerService.AnalyzeGame` reads the component manifest and sets the new `Game` fields
  `IsDlssgSm86Installed`, `DlssgSm86Version` and `DlssgSm86Build`. Add a badge in
  "Detected components" (see the existing NR/FSR badges).

### 4.6 INI

- Start every install from the pinned factory `dlssg_sm86.ini`. Write only `MaxGeneratedFrames`
  (multiplier cap) and, if exposed, `Optimized`, using a minimal line-preserving key setter so
  comments survive. `GameInstallationService` already has a similar section/key reader to reuse
  or extend.
- Multiplier cap choices: 2X / 3X / 4X (default) / 6X. 6X is only offered on the 310.9 build.
  Next to it, a note: "The game requests the actual multiplier; older Streamline plugins stop at
  2X or 4X." It shows the detected `sl.dlss_g.dll` version when known.

### 4.7 HAGS

- Read `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` with
  `Microsoft.Win32.Registry` (2 = on). Do it inside the Windows-only service.
- If it's off: a warning panel styled like `PanelModdedWarning`, explaining that frame generation
  stays greyed out until HAGS is on **and the PC is rebooted**. Add a button that opens
  `ms-settings:display-advancedgraphics` via `IShellService.OpenUrl`. **Warn, don't block:** the
  install is harmless, and the user can turn HAGS on afterwards. Never change the setting for the
  user.

### 4.8 UI (Manage Game window)

- A new section in the options area that matches the existing option cards: a label with a "?"
  tooltip, `ComboBox`es using the global styles, and the existing button styles. Placement:
  either its own bordered row like `GridExperimentalZone`, or **inside** that zone behind
  Settings → Experimental (§9 Q3).
- Controls: status (Not installed / Installed vX (build) / Update available), Build (310.9 /
  310.1), Max multiplier, Install / Update / Uninstall, the HAGS warning, an eligibility reason
  when blocked, and the chosen proxy names after install.
- All disk and network work goes through `await Task.Run(...)` or async service calls, with the
  existing progress bar and toast (`BdProgress` / `BdToast`). Reuse the confirm dialog pattern.
- Strings: new `TxtDlssgSm86*` keys in **all 14** `Languages/Strings.*.axaml` files. English is
  the authoritative text. Other languages are machine-assisted, and the PR says so. Read them with
  `GetResourceString(key, fallback)` like existing code.
- Gamepad navigation: add the new controls to the Manage window's gamepad focus order
  (`GamepadDialogNavigationHelper` / `GamepadNavigationHelper.*`). Check how the FSR 4 swap
  controls are registered.
- Cache Management window: list cached dlssg_sm86 versions and builds with their sizes, plus
  delete. Include them in "Clear application cache".

### 4.9 Out of scope for PR 1

Bulk Install, Quick Install defaults, the Diagnose button, FG-provider integration under
OptiScaler, auto-selection in `GetRecommendation`, `dxgi`/`d3d12` proxies, Streamline DLL
upgrades, and Linux/Proton.

## 5. Delivery: PR slicing

**PR 1: standalone component** (target `general`). Internally, separate reviewable commits:

1. `feat: add dlssg_for_sm86 package model and pinned manifest`: models, manifest JSON, csproj
   copy rule, GPU helpers.
2. `feat: download and verify dlssg_for_sm86 builds`: package service and cache.
3. `feat: add component-scoped backups to the backup store`: `BackupStoreService` scope, plus
   `_components` excluded from enumerations.
4. `feat: install, update and uninstall dlssg_for_sm86`: the service, eligibility, proxy-name
   selection, the reverse-collision guard in `InstallOptiScaler`, and analyzer and `Game` fields.
5. `feat: add dlssg_for_sm86 section to manage game window`: UI, HAGS panel, strings ×14,
   gamepad focus.
6. `feat: show dlssg_for_sm86 in cache management`.
7. `docs: document dlssg_for_sm86 support`: README features and acknowledgments (sdli1995's
   dlssg_for_sm86, Coldwood1026 for SM75), plus CHANGELOG. **Check with the maintainer** first;
   they normally write the CHANGELOG during their release commits.

**PR 2: Diagnose.** Parse the newest `loader_*/backend_*.jsonl` for `fg_gate_*` and
`install route active`, and show a one-line verdict with details. Offer to set `Level=2`
temporarily and restore it afterwards.

**PR 3: FG provider and auto-selection.** Coexistence with OptiScaler's DLSS-G output (Nukem's,
DLSS Enabler), plus the hook in `FrameGenerationConfigurationService.GetRecommendation` and
`DetectCapabilities` (new route or provider) for RTX 20/30.

Each PR branch is cut from `origin/general` and carries product commits only (see `AGENTS.md`).

## 6. Fork-side task breakdown (Warmstart)

| # | Task | Lands on fork `general` | Depends on |
|---|---|---|---|
| 0 | Install the .NET 10 SDK on the dev machine; build is green at baseline | — (environment) | — |
| 1 | Post §9 questions on #103 (the user posts them) | — | — |
| 2 | Pin script + manifest JSON for 0.3.5 (both builds) | fork script, product JSON | 0 |
| 3 | Models + GPU helpers + package service (commits 1–2) | yes | 2 |
| 4 | Backup-store component scope (commit 3) | yes | 0 |
| 5 | Install/update/uninstall service + collision guards (commit 4) | yes | 3, 4 |
| 6 | Manage window section + strings + gamepad (commit 5) | yes | 5, answers to Q1/Q3 |
| 7 | Cache management + docs (commits 6–7) | yes | 5 |
| 8 | Manual test matrix on the RTX 3080 Ti (§7); fix findings | yes | 6, 7 |
| 9 | Cut `pr/dlssg-sm86` from `origin/general`, cherry-pick product commits, open a draft PR | PR | 8 |

Tasks 3 and 4 can run in parallel.

## 7. Test plan

No test project ships in the repo (§9 Q2). Until that's settled, keep fixture tests out of the
app's compile glob. The root csproj compiles **every** `**/*.cs` under the repo root (§AGENTS
pitfall), so a test project has to live outside the root, or be excluded explicitly.

Fixture or unit scenarios (wherever tests end up):
- Pinned manifest: a size or SHA-256 mismatch → file deleted, error raised; a thumbprint mismatch
  → rejected.
- Proxy names: a clean folder → all four. OptiScaler on `version.dll` → winmm/dbghelp/dinput8.
  `dinput8.dll` belongs to an ASI loader → skipped. All taken → `NoFreeProxyName`.
- Install fails partway (a target is read-only) → rollback leaves the folder byte-identical.
- Uninstall restores the folder byte-identical to a pre-install hash snapshot.
- Uninstalling OptiScaler leaves DLSSG and its component backup intact, and the reverse holds
  too.
- Installing OptiScaler with `InjectionMethod` = an installed DLSSG proxy → refused or re-picked,
  never backed up as an "original".
- Update 310.9 → 310.1 keeps the INI values (and clamps a 6X cap to 4X with a notice).
- An edited INI at uninstall → the user is asked.
- Eligibility: Linux → service is null, section hidden. GTX 1660 / RTX 4090 / RX 7900 → hidden.
  Anti-cheat → blocked.

Manual on the dev machine (RTX 3080 Ti, Intel UHD 770 iGPU, Windows 11):
- GPU gate picks the 3080 Ti, not the iGPU.
- FF16: install → launch → FG available at 2X (Streamline 2.4.10 caps it). Uninstall → folder
  identical.
- HAGS off → warning shown, settings link opens. HAGS on + reboot → FG works.
- A game on a newer Streamline (MFG-capable) → 4X. On the 310.9 build with cap 6X → 6X if the
  game offers it.
- OptiScaler + DLSSG together on one game (FG route disabled in OptiScaler): both work; both
  uninstall independently.
- All 14 languages render the section without overflow at the narrow layout
  (`ManageGameWindow.Responsive.cs`).

## 8. Risks

- **Upstream churn:** the layout moved between releases (`altnative/` → `alternatives/`, the
  native build → back to the proxy). Pinning by commit plus a manifest keeps the client stable.
  A new mod version is a deliberate manifest bump.
- **Download size:** about 30 MB per proxy × 4 ≈ 120 MB per build. Download per build on demand
  and show the size up front. Could be reduced to `version.dll` + `winmm.dll` if the maintainer
  prefers (§9 Q5).
- **Interaction with OptiScaler's FG routes:** OptiScaler's Nukem / DLSS Enabler providers hook
  the same NGX DLSS-G path. In v1, warn when OptiScaler's FG is on alongside this mod; the real
  integration is PR 3.
- **The mod spoofs the GPU arch to Streamline:** is there an anti-cheat risk? Anti-cheat games
  are already blocked.
- **ManageGameWindow.axaml.cs is huge and busy:** upstream merge conflicts are likely. Keep the
  section in its own partial class file (`ManageGameWindow.DlssgSm86.cs`), like `.Responsive.cs`
  and `.CoverAmbience.cs`, and touch the main file minimally.

## 9. Questions for the maintainer (post on #103 before PR 1 UI work)

1. **Conventions vs current code:** there's no DI container or ViewModel layer today. Is a thin
   code-behind partial (`ManageGameWindow.DlssgSm86.cs`) that only calls a service OK, or do you
   want this section to introduce a ViewModel and/or `Microsoft.Extensions.DependencyInjection`?
2. **Tests:** the csproj has `InternalsVisibleTo("Optiscaler-Client.Tests")`, but no test project
   is in the repo. Is there one I should add tests to, or should I add one (where)?
3. **Placement and visibility:** a regular section, or inside the Experimental zone behind
   Settings → Experimental? On non-Turing/Ampere GPUs, hide it entirely (like Linux) or show it
   disabled with the reason?
4. **Injection-method collision:** if a user later installs OptiScaler with an injection method
   the DLSSG proxy already uses, should Install refuse with a message, or switch OptiScaler to
   the next free method?
5. **Proxy set:** install all four utility proxies (upstream's recommendation, about 120 MB
   download per build), or just `version.dll` plus one fallback?
6. **CHANGELOG / README:** should the PR touch them, or do you prefer to write those during the
   release?
