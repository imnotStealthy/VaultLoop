# AGENTS.md — VaultLoop working brief

Audience: an autonomous coding agent working in this repository.
Status: the UI refactoring described by the previous revision of this file is
**done** (commits `5681e45`..`24ed5eb`, 2026-07-26). This file now describes the
codebase as it stands, plus the invariants and the validation loop that any future
change must respect. The historical refactoring plan is preserved in git history at
commit `5681e45`.

---

## 1. Project snapshot

| Item | Value |
| --- | --- |
| Product | VaultLoop — Windows desktop controller for one narrowly scoped outbound firewall rule |
| Language / TFM | C# (`LangVersion preview`), `net48`, WinForms, x64, `Nullable enable` |
| Entry point | `Program.cs` |
| Build | `dotnet build ReplayGlitchGTA.csproj -c Release` |
| Ship | `dotnet build ReplayGlitchGTA.csproj -c Release -t:Ship` → `publish\VaultLoop.exe` |
| Tests | `VaultLoop.exe --selftest` from a **DEBUG** build, elevated terminal |
| Namespace | `ReplayGlitchGTA` for every type — no sub-namespaces |
| Tooling | **No PowerShell.** No build or test scripts; `dotnet` plus MSBuild targets only |

```
Program.cs                     startup, single instance, watchdog, --restore, --diagnose, --selftest, --render-preview
MainForm.cs              742   window chrome, layout, firewall state orchestration
SelfTest.cs                    --selftest regression checks (DEBUG only)
DiagnosticsReport.cs           --diagnose: blocked set, rule state, live game endpoints
FirewallService.cs             exact firewall rule state and mutation
GameProcessService.cs          GTA process discovery + Authenticode validation
AppSettingsStorage.cs     86   atomic local preference persistence

Network/IpPrefix.cs            IPv4/IPv6 prefix parsing, canonical form, containment
Network/RockstarNetworks.cs    ARIN-sourced Take-Two tables; the blocked set
Network/GameConnectionInspector.cs  read-only TCP table reader (GetExtendedTcpTable)

Ui/Palette.cs             18   every brand color, once
Ui/Typography.cs          42   shared process-lifetime Font instances
Ui/BrutalistControls.cs   72   button + label factories
Ui/ThemeController.cs     80   theme capture and apply
Ui/BrutalistDialog.cs     74   dialog base (title bar, drag, border)
Ui/ShortcutDialog.cs     115
Ui/GuideDialog.cs        248
Ui/GuideStepPanel.cs      40
Ui/StatusToastForm.cs     89
Ui/BooleanToggle.cs      170

Input/GlobalHotkeyHook.cs 149   WH_KEYBOARD_LL wrapper, Pressed/Released events
Interop/NativeMethods.cs   55   P/Invoke, structs, message + flag constants

Settings/ShortcutSettings.cs      93
Settings/ThemeSettings.cs         37
Settings/GuideProgressSettings.cs 29

legacy/   historical AutoHotkey script, must not be run
```

---

## 2. Hard constraints — do not violate

### 2.1 Security model (read `README.md` §Safety model before starting)

- The firewall rule stays scoped to a locally installed, Authenticode-valid Rockstar
  `GTA5.exe` / `GTA5_Enhanced.exe`. Never widen `IsTrustedGameExecutable`.
- Never relax `FirewallService.IsExactCurrentRule`. Every property comparison in it
  is deliberate: a rule is `Active` only when **all** expected properties match.
- Never remove the injected-keystroke rejection in `GlobalHotkeyHook`
  (`InjectedFlag | LowerIntegrityInjectedFlag`).
- Never remove a restore path: `FormClosing`, the `finally` block in `Program.Main`,
  the watchdog process, next-start stale-rule recovery, `--restore`, or the
  game-loss auto-restore in `MainForm.EvaluateGameLoss`. The rule names the game
  executable by path, so a rule left active after the game exits silently blocks a
  relaunched GTA.
- Any field read from the keyboard hook thread must be `volatile` or accessed
  through `Volatile` / `Interlocked`. That currently means `_capturingShortcut`,
  `_shortcutDown`, `_gameHotkeyReady`, `_verifiedGameWindow`, and — through the
  `_canTrigger` delegate — `MainForm._applying` and `MainForm._stateKnown`.
- Never let the hotkey arm while the verified game is not foreground. The guard is
  two-layered and both layers must survive: `Volatile.Read(ref _gameHotkeyReady)`
  (set through `GlobalHotkeyHook.Arm` / `Disarm`) **and** the live re-check
  `GameProcessService.IsCurrentForegroundWindow(...)` inside the hook callback, plus
  the `_canTrigger` delegate (`!_applying && _stateKnown` in `MainForm`).
- Keep `Marshal.FinalReleaseComObject` release order in `FirewallService`
  (`rule` → `rules` → `policy`) and keep every COM release inside `finally`.
- `Program.Main` must keep `NativeMethods.RestrictDllSearchPathToSystem32()` as its
  **first statement**. `wintrust.dll` and `iphlpapi.dll` are not KnownDLLs, so
  without it the directory holding the executable is searched before System32 and
  anyone able to write there gets code execution inside an elevated process. This
  was reproduced and the fix verified by planting a file named `iphlpapi.dll` next
  to the executable: unhardened, it is loaded and the inspector checks fail;
  hardened, System32 wins and they pass.
- Ship to a directory that is not writable by `Authenticated Users`. The DLL search
  restriction does not stop the executable itself from being replaced — only ACLs
  do. The repository `publish\` directory grants Modify to Authenticated Users and
  is a development output, not a deployment target.
- Keep the settings reparse-point checks in `AppSettingsStorage` on both the
  directory and the files, on the read and write paths: the process is elevated
  while `%LOCALAPPDATA%` is controlled by the unprivileged user.
- Keep `TargetsOnlyManagedAddresses` an **exact set match**. Accepting a superset,
  a subset, or a broader prefix would let a rule that blocks more than intended
  read back as `Active`.
- `GameConnectionInspector` is read-only and reports only the verified game
  process. Do not extend it to arbitrary processes.
- Keep the hook delegate in a field so the GC cannot collect it while installed, and
  keep install/uninstall in `OnHandleCreated` / `OnHandleDestroyed`.

### 2.2 Compatibility surface — values that must not change

- Firewall rule names: `"VaultLoop - No Save"`, `"Replay Glitch GTA V - No Save"`,
  `"123456"`; marker `"VaultLoop managed rule v2"`; grouping `"VaultLoop"`.
- The blocked address set is configurable through `endpoints.txt`
  (`BlockedEndpointsSettings`), defaulting to `RockstarNetworks.DefaultBlocked`.
  Every configured entry **must** be validated as lying inside a Rockstar Online
  Services allocation before use — that guard is the only thing that makes a
  user-editable file safe for a rule written by an elevated process. Never accept
  an entry outside those allocations, and never let a Zynga or Take-Two corporate
  range through.
- Resolve the set **once per process** (`RockstarNetworks.Configuration` is a
  `Lazy`). If the file were re-read mid-run, the rule already written to Windows
  and the check that validates it would disagree and the state would read Invalid.
- The working set is the single address `192.81.241.171`, confirmed in play on
  2026-07-26: the game reports `SAVING FAILED` and the session survives. **Do not
  widen it.** Blocking the surrounding `192.81.241.0/24` reaches Rockstar
  authentication and drops the session mid-activity, before any save occurs.
- Two failures previously masked each other, and both must stay fixed:
  resolving the game path through `Process.MainModule` alone fails against
  anti-cheat protection, so no rule was ever created and no-save was silently a
  no-op; and the widened address set broke authentication once detection worked.
  See `README.md` §Blocked address set.
- The pre-1.3 single endpoint `192.81.241.171` and its three accepted forms must
  keep being recognized by `FirewallService.TargetsOnlyLegacyAddress`. Dropping it
  orphans a rule when a user upgrades while no-save is active, which leaves the
  game permanently blocked.
- Do not bump `RuleMarker` ("VaultLoop managed rule v2"). Rule removal matches on
  it; a new marker makes older rules unremovable, because the historical fallback
  requires a blank `ApplicationName` that those rules do not have.
- Settings file names and formats: `shortcut.txt` (`"{(int)modifiers}|{(int)key}"`),
  `theme.txt` (`dark` / `light`), `guide-step.txt` (`1`–`6`), directories
  `%LOCALAPPDATA%\VaultLoop` and legacy `%LOCALAPPDATA%\ReplayGlitchGTA`.
- Mutex name `Global\ReplayGlitchGTA.NoSave`.
- Assembly attributes in `Program.cs` and the version `1.2.0.0` in `app.manifest`.
- CLI arguments: `--watchdog <pid>`, `--restore`, `--diagnose`, and, in `DEBUG`
  builds, `--render-preview <path> [on|unknown]` and `--selftest`.
- `app.manifest`: `requireAdministrator`, `PerMonitorV2`, `longPathAware`, and the
  compatibility GUID `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`.
- All user-facing strings stay English and byte-identical.

### 2.3 Self-test contract

`SelfTest.Run` calls these members **directly**, so renaming or changing a signature
is a compile error rather than a silent gap — that is the point, do not reintroduce
reflection by name. Keep them reachable (`internal` or wider) inside the assembly:

`GameProcessService.IsSupportedProcessName`, `GameProcessService.IsCurrentForegroundWindow`,
`FirewallService.TargetsOnlyManagedAddresses`, `FirewallService.TargetsOnlyLegacyAddress`,
`FirewallService.GetState`, `ShortcutDialog.IsValidShortcut`, `ShortcutSettings.Format`,
`Program.HasSupportedRuntime`, `RockstarNetworks.IsBlocked`,
`RockstarNetworks.GetOnlineServiceName`, `IpPrefix.TryParse`,
`GameConnectionInspector.GetConnections`, and the
`MainForm(FirewallService?, bool, bool, bool)` constructor with its preview defaults.

### 2.4 Build constraints

- `TreatWarningsAsErrors` is set in the project file. Any new compiler warning
  (nullable, `CS0067`, unused field, obsolete API) fails the build. Compile clean.
- `net48` resolves against `$(SystemRoot)\Microsoft.NET\Framework64\v4.0.30319`
  (`AutomaticallyUseReferenceAssemblyPackages=false`) so the build works offline.
  This machine has no NuGet access — a change that needs a package will not build.
- Build gates that must keep passing: `ValidateFrameworkPath`, `ValidateManifest`,
  `ValidateSingleFileOutput` (a release build emits `VaultLoop.exe` and nothing else,
  hence `DebugType none`, no satellite assemblies, no copy-local references).
- No new NuGet packages, target frameworks, analyzers, or source generators.
- SDK-style globbing: new `.cs` files under the project directory compile
  automatically. Do not add explicit `<Compile>` items.
- Do not modify `bin/`, `obj/`, `publish/`, `Assets/`, or `legacy/`.
- No `.ps1` files, ever. Automation goes into MSBuild targets or the application's
  own CLI arguments.

---

## 3. Validation

### 3.1 Build (mandatory after every change)

```sh
dotnet build ReplayGlitchGTA.csproj -c Release
```

A clean run also proves the manifest and single-file gates passed.

### 3.2 Self-test (mandatory for anything touching behavior)

From an **elevated** terminal — the manifest forces elevation:

```sh
dotnet build ReplayGlitchGTA.csproj -c Debug -o obj/preview
obj/preview/VaultLoop.exe --selftest
```

Expected: `Result = PASS`, exit code `0`, `FirewallState` one of `Inactive` /
`Active` / `Invalid`. The run is read-only and never mutates Windows Firewall. Add
a check to `SelfTest.cs` whenever you add an invariant worth pinning.

Changing anything the rule writes into Windows Firewall also needs a round-trip
check, because Windows rewrites what it is given (`192.81.241.0/24` comes back as
`192.81.241.0/255.255.255.0`). Verify it **without mutating the firewall** by
creating a standalone `HNetCfg.FWRule` COM object, setting the property, and
reading it back — the object is never added to the `Rules` collection.

### 3.3 Pixel parity (mandatory for any UI change)

```sh
obj/preview/VaultLoop.exe --render-preview obj/preview/after-off.png
obj/preview/VaultLoop.exe --render-preview obj/preview/after-on.png on
obj/preview/VaultLoop.exe --render-preview obj/preview/after-unknown.png unknown
sha256sum obj/preview/before-*.png obj/preview/after-*.png
```

**Do not compare against stored PNGs.** Preview mode never reads the firewall and
forces the "admin ready" status label, but it does read
`%LOCALAPPDATA%\VaultLoop\theme.txt` and `shortcut.txt`, and it is sensitive to
display state. `obj/preview/before-*.png` were captured on 2026-07-26 and no longer
reproduce on the same machine, because the stored preferences changed in between —
a stale reference produces a false regression, which costs more time than it saves.

Compare against a freshly built previous commit instead. This isolates the code
change from the environment, since both binaries then run under identical settings:

```sh
git worktree add /tmp/baseline HEAD --detach
dotnet build /tmp/baseline/ReplayGlitchGTA.csproj -c Debug -o /tmp/baseline-out
for variant in "" on unknown; do
  /tmp/baseline-out/VaultLoop.exe --render-preview /tmp/base-${variant:-off}.png $variant
  obj/preview/VaultLoop.exe --render-preview /tmp/changed-${variant:-off}.png $variant
done
sha256sum /tmp/base-*.png /tmp/changed-*.png
git worktree remove /tmp/baseline --force
```

If you must touch the user's preference files to reproduce a state, back them up
and restore them in the same command.

### 3.4 Manual smoke (end of a task, once)

Run `publish\VaultLoop.exe` elevated and confirm: window renders identically; the
toggle is disabled or errors without a verified GTA process; `HOW TO USE` opens and
persists the current step; the shortcut dialog saves and rejects `Alt+F4`; the theme
button flips and the preference survives a restart; closing while `ACTIVE` prompts
and restores. Do not test with a real game session enabled unless the user asks.

---

## 4. Out of scope unless the user asks

- Any change to layout coordinates, sizes, spacing, or the hardcoded pixel
  rectangles. The absolute positioning is deliberate; converting to layout panels is
  a separate, user-approved task.
- Migrating off `net48`, off WinForms, or to `dynamic`-free firewall COM interop.
- Adding logging, telemetry, DI, MVVM, async/await, or an abstraction layer over
  `FirewallService` / `GameProcessService`.
- Adding unit-test projects or test frameworks — `--selftest` is dependency-free by
  design; keep it that way.
- Changing the watchdog, single-instance, or recovery strategy.
- Localization, string edits, wording changes, or rebranding.
- Touching `legacy/`, `CHANGELOG.md` history, or bumping the version.

If you find a genuine bug, report it with file and line instead of fixing it inside
an unrelated change.

---

## 5. Measured facts

Numbers from this machine on 2026-07-26; re-measure before reasoning about cost.

- `FirewallService.GetState()` — median **14 ms**, max 17.5 ms. This sets the
  `ConfirmState` budget: 7 attempts with exponential backoff ≈ 2 s total.
- `GameProcessService.TryFindVerifiedRunningGame` — median 5.1 ms.
- `TryGetVerifiedForegroundGame` — median 1.4 ms.
- One refresh tick therefore costs ~15-20 ms on a thread-pool thread, about 1.5 %
  of one core at the 1200 ms interval. **The polling loop is not a performance
  problem**; do not "optimize" it without new measurements. The one real spike is
  the Authenticode re-verification of the ~100 MB game executable, which the trust
  cache limits to once per 300 s.

A block rule does **not** tear down an already established TCP flow. "A connection
to a blocked address exists" is therefore not evidence that the block failed; only
a connection whose **local port** appeared after the rule went active is, because
its handshake had to complete through the rule. `MainForm.EvaluateBlockEffectiveness`
and `--diagnose` both depend on this distinction.

## 6. Known follow-ups

- `BrutalistControls.CreateButton` takes eleven positional parameters, four of them
  `Color?`; named wrappers (`WindowButton` / `TextButton` / `ActionButton` /
  `DialogButton`) would make the call sites readable and prevent a silent
  hover/pressed swap.
- `GlobalHotkeyHook.Released` is raised but never subscribed.
- `Typography.StatusDetail` doubles as the dialog button font in `BrutalistDialog`;
  the name no longer matches every use.
- `MainForm` still mixes window chrome, layout, and firewall state orchestration;
  extracting a layout builder and a state presenter is the next natural step.
- `--restore` and `--diagnose` run before the single-instance mutex is taken, so
  they can mutate or read the firewall while the main window is doing the same.
- The watchdog is never respawned if it dies, and does not verify that the pid it
  waits on is really VaultLoop (the reuse window is tiny — the parent has just
  spawned it — so this is theoretical).
- `PolicyCanEnforce` returning false because Windows Firewall is switched off is
  surfaced as `INVALID` / "CLICK RESTORE", which misdescribes the cause.
- `--render-preview` pumps a single `Application.DoEvents()` before capturing; a
  `Refresh()` would make the pixel-parity net deterministic by construction.
