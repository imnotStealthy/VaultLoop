# AGENTS.md — VaultLoop refactoring brief

Audience: an autonomous coding agent working in this repository.
Task type: **behavior-preserving refactoring only**. No new features, no UI redesign,
no changes to the security model.

Brief re-verified against the sources on 2026-07-25. Every line count, member name,
and finding below was read from the current files, not carried over from an earlier
revision.

---

## 1. Project snapshot

| Item | Value |
| --- | --- |
| Product | VaultLoop — Windows desktop controller for one narrowly scoped outbound firewall rule |
| Language / TFM | C# (`LangVersion preview`), `net48`, WinForms, x64, `Nullable enable` |
| Entry point | `Program.cs` |
| Build | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1` |
| Tests | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1` |
| Output | single `publish\VaultLoop.exe` |
| Namespace | `ReplayGlitchGTA` (assembly name is `VaultLoop`) |

Current source layout and sizes:

```
Program.cs              207 lines   startup, single instance, watchdog, --restore
MainForm.cs            1724 lines   10 types in one file  <-- primary refactoring target
GameProcessService.cs   420 lines   GTA process discovery + Authenticode validation
FirewallService.cs      273 lines   exact firewall rule state and mutation
AppSettingsStorage.cs    86 lines   atomic local preference persistence
Build.ps1 / Test.ps1                staged build + dependency-free regression checks
legacy/                             historical AutoHotkey script, must not be run
```

The 10 top-level types in `MainForm.cs`, in file order: `MainForm`,
`ShortcutSettings`, `ThemeSettings`, `GuideProgressSettings`, `BrutalistDialog`,
`ShortcutDialog`, `GuideDialog`, `GuideStepPanel`, `StatusToastForm`,
`BooleanToggle`. Two accessibility helpers are nested inside their owners
(`GuideStepPanel.GuideStepAccessibleObject`, `BooleanToggle.BooleanToggleAccessibleObject`)
and must stay nested.

The repository is **still not** under version control as of 2026-07-25 (no `.git`
directory, although a `.gitignore` exists). Before touching anything,
run `git init` and commit the current tree as a baseline, so each refactoring step
can be committed separately and reverted independently. If version control is
refused, copy the whole project directory to a backup location first.

---

## 2. Mission

Reduce structural duplication and file size in the UI layer without changing a
single observable behavior. The end state must be:

1. One type per file, in folders that mirror the type's responsibility.
2. A single definition for each brand color, font, and Win32 interop declaration.
3. The low-level keyboard hook isolated from `MainForm`.
4. Identical runtime behavior, identical pixels, identical firewall semantics.

The success criterion for the whole task is: `Test.ps1` passes, and the rendered
preview PNG of the main window is byte-identical (or visually indistinguishable)
before and after the refactoring. See §7.

---

## 3. Hard constraints — do not violate

These are correctness and safety invariants. A refactoring that breaks any of them
is a failed refactoring, even if it compiles.

### 3.1 Security model (read `README.md` §Safety model before starting)

- The firewall rule stays scoped to a locally installed, Authenticode-valid Rockstar
  `GTA5.exe` / `GTA5_Enhanced.exe`. Never widen `IsTrustedGameExecutable`.
- Never relax `FirewallService.IsExactCurrentRule`. Every property comparison in it
  is deliberate: a rule is `Active` only when **all** expected properties match.
- Never remove the injected-keystroke rejection in the keyboard hook
  (`InjectedFlag | LowerIntegrityInjectedFlag`).
- Never remove a restore path: `FormClosing`, the `finally` block in `Program.Main`,
  the watchdog process, next-start stale-rule recovery, or `--restore`.
- Never let the hotkey arm while the verified game is not foreground. The guard is
  two-layered and both layers must survive: `Volatile.Read(ref _gameHotkeyReady)`
  (set by `RefreshGameContext`) **and** the live re-check
  `GameProcessService.IsCurrentForegroundWindow(new IntPtr(Interlocked.Read(ref _verifiedGameWindow)))`
  performed inside the hook callback, plus `!_applying && _stateKnown`.
  Keep `_verifiedGameWindow` as a `long` accessed through `Interlocked` — it is
  written on the UI thread and read on the hook thread.
- Keep `Marshal.FinalReleaseComObject` release order in `FirewallService`
  (`rule` → `rules` → `policy`) and keep every COM release inside `finally`.

### 3.2 Compatibility surface — values that must not change

- Firewall rule names: `"VaultLoop - No Save"`, `"Replay Glitch GTA V - No Save"`,
  `"123456"`; marker `"VaultLoop managed rule v2"`; grouping `"VaultLoop"`.
- Remote address `192.81.241.171` and the three accepted forms.
- Settings file names and formats: `shortcut.txt` (`"{(int)modifiers}|{(int)key}"`),
  `theme.txt` (`dark` / `light`), `guide-step.txt` (`1`–`6`), directories
  `%LOCALAPPDATA%\VaultLoop` and legacy `%LOCALAPPDATA%\ReplayGlitchGTA`.
- Mutex name `Global\ReplayGlitchGTA.NoSave`.
- Assembly attributes in `Program.cs` and the version `1.2.0.0` in `app.manifest`
  (`Test.ps1` asserts the version; `Build.ps1` reports it).
- CLI arguments: `--watchdog <pid>`, `--restore`, `--render-preview <path> [on|unknown]`.
- `app.manifest`: `requireAdministrator`, `PerMonitorV2`, `longPathAware`.
- All user-facing strings stay English and byte-identical.

### 3.3 Reflection contract with `Test.ps1`

`Test.ps1` reaches into non-public members by name. Renaming or moving any of the
following breaks the test suite. Keep the fully qualified names **exactly** as they
are — in particular, do not introduce sub-namespaces; new folders must keep
`namespace ReplayGlitchGTA;`.

| Reflected member | Required shape |
| --- | --- |
| `ReplayGlitchGTA.GameProcessService.IsSupportedProcessName` | static, non-public, `(string) -> bool` |
| `ReplayGlitchGTA.GameProcessService.IsCurrentForegroundWindow` | static, non-public, `(IntPtr) -> bool`; must return `false` for `IntPtr.Zero` |
| `ReplayGlitchGTA.FirewallService.TargetsOnlyRemoteAddress` | static, non-public, `(string) -> bool` |
| `ReplayGlitchGTA.ShortcutDialog.IsValidShortcut` | static, non-public, `(Keys, Keys) -> bool` |
| `ReplayGlitchGTA.ShortcutSettings.Format` | static, non-public, `(Keys, Keys) -> string`; `(Alt, D8)` must yield `"ALT+8"` |
| `ReplayGlitchGTA.Program.HasSupportedRuntime` | static, non-public, no args, `-> bool` |
| `ReplayGlitchGTA.FirewallService.GetState` | instance, non-public, no args |
| `ReplayGlitchGTA.FirewallService` | must stay instantiable by `Activator.CreateInstance(type, true)` — do not add a constructor with parameters unless a parameterless one remains |
| `ReplayGlitchGTA.MainForm` | `GetConstructors(Instance,NonPublic)[0]` invoked with `(null, true, false, false)`; the built form must keep `Text == "VaultLoop"` and `ClientSize.Width >= 780`, and must survive `Dispose()` |
| Assembly | manifest resource named `ReplayGlitchLogo.png`, version `1.2.0.0`, runtime `v4.0.30319` |
| `app.manifest` | must still contain `PerMonitorV2`, `requireAdministrator`, and the compatibility GUID `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}` |

`MainForm` is the fragile one: the test takes constructor **index 0**. Do not add a
second constructor, and do not change the parameter order or count of the existing
one. If a change there becomes unavoidable, update `Test.ps1` in the same commit and
say so explicitly in your report.

### 3.4 Build constraints

- `Build.ps1` passes `-p:TreatWarningsAsErrors=true`. Any new compiler warning
  (including nullable warnings, `CS0067` unused event, unused field, obsolete API)
  fails the build. Compile clean.
- No new NuGet packages, no new target frameworks, no analyzers, no `.editorconfig`
  churn, no source generators.
- The project is SDK-style with default globbing: new `.cs` files under the project
  directory are compiled automatically. Do not add explicit `<Compile>` items.
- `Build.ps1` fails if the staged Release output directory contains **any file other
  than `VaultLoop.exe`**. Nothing may introduce a satellite assembly, a `.pdb`
  (`DebugType none` / `DebugSymbols false` must stay), a config file, or a
  referenced DLL. `ReplayGlitchGTA.csproj` must keep `AssemblyName VaultLoop`,
  `RootNamespace ReplayGlitchGTA`, `Nullable enable`, `LangVersion preview`,
  `GenerateAssemblyInfo false`, x64, and the `Microsoft.CSharp` reference (the
  firewall COM code is `dynamic`).
- Do not modify `bin/`, `obj/`, `publish/`, `Assets/`, or `legacy/`.

---

## 4. Findings to act on

These were identified by reading the current sources. Each is duplication or
structural noise, not a behavior bug.

1. **`MainForm.cs` holds 10 top-level types** (`MainForm`, `ShortcutSettings`,
   `ThemeSettings`, `GuideProgressSettings`, `BrutalistDialog`, `ShortcutDialog`,
   `GuideDialog`, `GuideStepPanel`, `StatusToastForm`, `BooleanToggle`).
2. **The palette is redeclared across four types.** Exact current counts:
   `Ink` and `Paper` ×4 (`MainForm`, `BrutalistDialog`, `StatusToastForm`,
   `BooleanToggle`); `Yellow` and `Acid` ×3 (`MainForm`, `BrutalistDialog`,
   `BooleanToggle`); `HotPink` ×2 (`MainForm`, `BooleanToggle`); `Blue`,
   `DarkCanvas`, `DarkSurface` ×2 (`MainForm`, `BrutalistDialog`); `Cream` only in
   `MainForm`; `AlertRed` only in `BrutalistDialog`. The guide's neutral
   `Color.FromArgb(246, 242, 228)` is inlined twice inside `GuideDialog`
   (`BuildStep` line ~1359, `SetCurrentStep` line ~1411).
3. **Win32 interop is duplicated.** `ReleaseCapture` and `SendMessage` are declared
   in both `MainForm` and `BrutalistDialog`. `MainForm` names the constants
   (`NonClientLeftButtonDown = 0x00A1`, `HitCaption = 0x0002`) while
   `BrutalistDialog.BeginDrag` passes the raw literals `0x00A1, 0x0002`.
4. **Four near-identical button factories**: `MainForm.MakeWindowButton`,
   `MakeTextButton`, `MakeActionButton`, and `BrutalistDialog.CreateButton`. They
   differ only in border size, hover colors, font, and text alignment.
5. **Fonts are allocated inline.** `new Font(...)` appears **32 times** in
   `MainForm.cs`. Most are handed to a control and never disposed (harmless, but
   duplicated); the two in `BooleanToggle.OnPaint` (`labelFont`, `knobFont`) are
   `using` locals re-created on **every paint**. Controls do not own their `Font`,
   so shared cached instances are safe and strictly better — but when moving the
   two paint fonts to `Typography`, the `using` must be dropped or the shared
   instance is destroyed after the first paint.
6. **`MainForm` mixes five concerns**: window chrome, layout, theming, the global
   keyboard hook, and firewall state orchestration.
7. **Thread-affinity inconsistency in the hook fields**: `_capturingShortcut` and
   `_shortcutDown` are plain `bool` fields written from the UI thread and read or
   written from the hook callback thread, while the adjacent `_gameHotkeyReady`
   uses `Volatile` and `_verifiedGameWindow` uses `Interlocked`.
8. *(resolved before this revision — no action needed)* The four cleanups the
   earlier brief listed here are already done in the current tree:
   `FirewallService.IsNoSaveEnabled()` no longer exists, `ShortcutSettings.Format`
   already uses the imported `List<string>`, `GameProcessService.cs` no longer has
   the redundant `#nullable enable`, and `MainForm` already overrides
   `Dispose(bool)` to dispose `_refreshTimer` and `_logoImage` (lines 204-215).
   Do not "re-fix" any of them.
9. **`GuideStepPanel` is a UI type living in `MainForm.cs`** with its nested
   accessible object; it belongs next to `GuideDialog`, which is its only consumer.

---

## 5. Target structure

Keep every type in `namespace ReplayGlitchGTA;`. Folders are for humans only.

```
Program.cs
MainForm.cs                    MainForm only (~550-650 lines after extraction)
FirewallService.cs             unchanged
GameProcessService.cs          unchanged
AppSettingsStorage.cs          unchanged

Ui/Palette.cs                  internal static class Palette      — every brand color, once
Ui/Typography.cs               internal static class Typography   — cached shared Font instances
Ui/BrutalistControls.cs        internal static class BrutalistControls — label/button factories
Ui/ThemeController.cs          internal sealed class ThemeController   — color capture + apply
Ui/BrutalistDialog.cs          internal abstract class BrutalistDialog
Ui/ShortcutDialog.cs           internal sealed class ShortcutDialog
Ui/GuideDialog.cs              internal sealed class GuideDialog
Ui/GuideStepPanel.cs           internal sealed class GuideStepPanel (+ its accessible object)
Ui/StatusToastForm.cs          internal sealed class StatusToastForm
Ui/BooleanToggle.cs            internal sealed class BooleanToggle (+ its accessible object)

Input/GlobalHotkeyHook.cs      internal sealed class GlobalHotkeyHook  — WH_KEYBOARD_LL wrapper
Interop/NativeMethods.cs       internal static class NativeMethods     — P/Invoke, structs, constants

Settings/ShortcutSettings.cs
Settings/ThemeSettings.cs
Settings/GuideProgressSettings.cs
```

---

## 6. Work plan

Execute the steps **in order**. After every step: build, run `Test.ps1`, commit.
Do not batch steps into one commit. If a step turns out to be riskier than
described, stop at the end of the previous step and report rather than improvising.

### Step 1 — Baseline
`git init`, commit the tree as-is. Build once and run `Test.ps1` to record the
starting state. Capture the reference preview PNGs (§7.3). If the baseline test
already fails, report that and stop — do not refactor on top of a red suite.

### Step 2 — Mechanical file split (no edits to bodies)
Move each of the 9 non-`MainForm` types out of `MainForm.cs` into the target files
of §5, adding only the `using` directives each file needs. Do not rename anything,
do not merge anything, do not change any member body. This step must be a pure
cut-and-paste. **Done when:** `Test.ps1` passes and `git diff --stat` shows only
moves plus `using` lines.

### Step 3 — `Ui/Palette.cs`
Create `internal static class Palette` holding one `internal static readonly Color`
per brand color, with these exact values:

```
Ink        17, 17, 17      Cream      255, 246, 218   Paper      255, 253, 245
Yellow     255, 215, 56    Blue        91, 134, 255   Acid       185, 255, 61
HotPink    255, 83, 112    AlertRed   232, 54, 70
DarkCanvas 20, 20, 20      DarkSurface 34, 34, 34     GuideNeutral 246, 242, 228
```

Delete the duplicated declarations from `MainForm`, `BrutalistDialog`,
`StatusToastForm`, and `BooleanToggle`, and point all references at `Palette.*`.
Note that `BrutalistDialog`'s copies are `protected static readonly` and are used
**unqualified** inside `ShortcutDialog` and `GuideDialog`; every one of those uses
must be requalified in the same step or the build breaks. Verify every ARGB value
survives the move unchanged — this is the step most likely to silently alter pixels.
**Done when:** the preview PNGs still match the reference.

### Step 4 — `Ui/Typography.cs`
Replace inline `new Font(...)` calls with shared `static readonly Font` fields
(`TitleImpact26`, `Mono10Bold`, `Body10`, …; name them by role, not by size).
Every font must keep its exact family, size, and style — the set in use today is
Impact 26/23/22/20/18, Bahnschrift 18B/11B/10/10B/9.5B/9/9B/8.5B/8.4/8B, and
Consolas 16B/10B/9B/8.5B (B = `FontStyle.Bold`). Prioritize
`BooleanToggle.OnPaint`, which currently allocates two fonts per paint inside
`using` statements — drop those `using`s when switching to the shared instances. Fonts are process-lifetime singletons and must
never be disposed by a control.
**Done when:** no `new Font(` remains outside `Typography`, and the preview PNGs
still match.

### Step 5 — `Interop/NativeMethods.cs`
Move `MainForm`'s seven `[DllImport]` declarations (`SetWindowsHookEx`,
`UnhookWindowsHookEx`, `CallNextHookEx`, `GetAsyncKeyState`, `GetModuleHandle`,
`ReleaseCapture`, `SendMessage`), the `LowLevelKeyboardData` struct, the
`LowLevelKeyboardProcedure` delegate, and the message/flag constants
(`LowLevelKeyboardHook = 13`, `KeyDownMessage 0x0100`, `KeyUpMessage 0x0101`,
`SystemKeyDownMessage 0x0104`, `SystemKeyUpMessage 0x0105`,
`LowerIntegrityInjectedFlag 0x02`, `InjectedFlag 0x10`, `AltDownFlag 0x20`,
`NonClientLeftButtonDown 0x00A1`, `HitCaption 0x0002`) into one internal static
class. Delete the duplicate
`ReleaseCapture` / `SendMessage` declarations from `BrutalistDialog` and replace
its raw `0x00A1, 0x0002` literals with the named constants. Keep marshalling
attributes (`SetLastError`, `CharSet`, `StructLayout`) byte-for-byte identical.
Leave `GameProcessService`'s WinTrust/kernel32 interop where it is — it is
cohesive with its only caller and moving it buys nothing.

### Step 6 — `Ui/BrutalistControls.cs`
Consolidate the four button factories into a single factory with explicit
parameters for border size, hover/pressed colors, font, and text alignment, plus
the `MakeLabel` helper. Every produced button must keep exactly its current
`FlatAppearance` values and hover handlers:

- `MakeActionButton` — Bahnschrift 8B, `BorderColor = Ink`, `BorderSize = 3`,
  `MouseOverBackColor = Blue`, `MouseEnter → ForeColor = Ink`,
  `MouseLeave → ForeColor` back to the captured original.
- `MakeWindowButton` — Bahnschrift 11B, `Ink`/`Paper`, `BorderSize = 0`, hover
  **and** pressed set to the per-button hover color, same enter/leave fore-color
  swap.
- `MakeTextButton` — caller-supplied font/colors, `BorderSize = 0`, hover and
  pressed set to the button's own background (i.e. no visible hover feedback),
  `TextAlign = MiddleCenter`, no enter/leave handlers.
- `BrutalistDialog.CreateButton` — Bahnschrift 9B, `BorderColor = Ink`,
  `BorderSize = 3`, no hover colors and no handlers; it is `protected static` and
  called from `ShortcutDialog` and `GuideDialog`, and the dialog's own close button
  then *overrides* `BorderSize = 0` and `MouseOverBackColor = AlertRed` after
  construction. Keep that post-construction override intact.

All four also set `FlatStyle.Flat`, `Cursor = Hand`, `UseVisualStyleBackColor =
false`. Do not "improve" any of these.

### Step 7 — `Ui/ThemeController.cs`
Move `CaptureThemeColors` / `ApplyTheme` and the two `Dictionary<Control, Color>`
maps out of `MainForm`. The controller receives the form and the set of controls
excluded from capture (`_stateKicker`, `_stateTitle`, `_stateDetail`, `_toggle`).
Preserve the exact mapping rules: capture recurses through the whole control tree;
`Cream → DarkCanvas` and `Paper → DarkSurface` for backgrounds; foreground becomes
`Paper` only when `_darkMode && originalFore == Ink && (originalBack == Cream ||
originalBack == Paper)`, otherwise it is restored to the captured original. The
form's own `BackColor`/`ForeColor`, the `_themeButton` text and `AccessibleName`
swap, and the trailing `Invalidate(true)` are part of `ApplyTheme` and must keep
happening in that order. Note `CaptureThemeColors(this)` runs **before**
`ApplyTheme()` in the constructor — keep that ordering.

### Step 8 — `Input/GlobalHotkeyHook.cs`
Extract the `WH_KEYBOARD_LL` install/uninstall and the callback filtering into a
class that raises `Pressed` / `Released` events and exposes an `Armed` flag plus the
current `(modifiers, key)`. `MainForm` keeps only: subscribe, arm/disarm from
`RefreshGameContext`, and marshal to the UI thread via `BeginInvoke`.
Preserve exactly, in this order: the `code >= 0` guard, the injected-flag rejection
(`InjectedFlag | LowerIntegrityInjectedFlag` → `CallNextHookEx`), the
`!_capturingShortcut` bypass combined with the
`VirtualKeyCode == (uint)_shortcutKey` test, the exact modifier equality test
(`pressedModifiers == _shortcutModifiers`, including the `GetAsyncKeyState`
reads for Ctrl/Shift and the `AltDownFlag` read from the event), the full
`canTrigger` conjunction (`keyDown && modifiersMatch && Volatile.Read(ref
_gameHotkeyReady) && GameProcessService.IsCurrentForegroundWindow(...) &&
!_applying && _stateKnown`), the `canTrigger || (keyUp && _shortcutDown)` gate,
the key-down/key-up edge tracking through `_shortcutDown`, the
`!IsDisposed && IsHandleCreated` check before `BeginInvoke`, and the
`return (IntPtr)1` swallow that stops the keystroke from reaching the game.
Only a *key-down* edge dispatches `ToggleState(fromHotkey: true)`; the key-up
branch merely clears `_shortcutDown`. Keep the hook delegate in a field so the GC
cannot collect it while installed, keep `_hotkeyRegistered` driving the
`HandleShown` warning message, and keep install/uninstall in `OnHandleCreated` /
`OnHandleDestroyed` (installed only when `!_previewMode`). Make
`_capturingShortcut` and `_shortcutDown` `volatile` for consistency with
`_gameHotkeyReady`.
This is the highest-risk step. If anything is ambiguous, keep the current code
shape and note the ambiguity instead of guessing.

### Step 9 — Final consistency pass
The four cleanups this step used to list are already applied in the current tree
(see finding 8) — do not redo them. What remains:

- Verify no `using` directive, field, or constant was left orphaned in `MainForm.cs`
  by steps 2-8 (`TreatWarningsAsErrors` will catch unused fields, not unused
  `using`s — check by hand).
- Confirm `MainForm.Dispose(bool)` still disposes `_refreshTimer` before
  `base.Dispose(disposing)` and `_logoImage` after it, and that the new
  `GlobalHotkeyHook` is uninstalled on handle destruction rather than in `Dispose`.
- Re-run the full validation set (§7) one last time, including the manual smoke.

---

## 7. Validation

### 7.1 Per step (mandatory)

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1
```

Expected: `Result = PASS`, `FirewallState` one of `Inactive` / `Active` / `Invalid`.
`Test.ps1` invokes `Build.ps1` itself, so a passing run also proves the build is
clean under `TreatWarningsAsErrors`. It does not enable no-save and does not mutate
Windows Firewall.

### 7.2 Full build

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```

### 7.3 Pixel parity (the real regression net for a UI refactor)

`--render-preview` is `DEBUG`-only and never touches the firewall, but the manifest
forces elevation, so run it from an elevated shell:

```powershell
dotnet build .\ReplayGlitchGTA.csproj -c Debug -o obj\preview
.\obj\preview\VaultLoop.exe --render-preview .\obj\preview\before-off.png
.\obj\preview\VaultLoop.exe --render-preview .\obj\preview\before-on.png on
.\obj\preview\VaultLoop.exe --render-preview .\obj\preview\before-unknown.png unknown
Get-FileHash .\obj\preview\before-*.png -Algorithm SHA256
```

If that `dotnet build` fails to resolve the `net48` reference assemblies, mirror the
two switches `Build.ps1` uses:
`-p:FrameworkPathOverride=$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319`
`-p:AutomaticallyUseReferenceAssemblyPackages=false`.

Preview mode never reads the firewall, forces the "admin ready" status label, and
takes the `previewState` / `previewUnknown` arguments straight from the command
line. It **does** still read `%LOCALAPPDATA%\VaultLoop\theme.txt` and
`shortcut.txt`, which drive the theme colors and the two shortcut captions — do not
change either preference between the `before-*` and `after-*` captures, or the
hashes will differ for a non-refactoring reason. With those held constant, on the
same machine and DPI, a hash difference is a real pixel difference.

Capture these three at step 1, re-capture as `after-*.png` at the end of steps 3, 4,
6, and 7, and compare hashes on the same machine and display scaling. A hash
mismatch means the UI changed — investigate before continuing. If hashes differ for
a non-visual reason, fall back to an explicit visual comparison and say so.

### 7.4 Manual smoke (end of task, once)

Run `publish\VaultLoop.exe` elevated and confirm: window renders identically; the
toggle is disabled or errors without a verified GTA process; `HOW TO USE` opens and
persists the current step; the shortcut dialog saves and rejects `Alt+F4`; the theme
button flips and the preference survives a restart; closing while `ACTIVE` prompts
and restores. Do not test with a real game session enabled unless the user asks.

---

## 8. Out of scope — do not do these

- Any change to layout coordinates, sizes, spacing, or the hardcoded pixel
  rectangles. The absolute positioning is ugly; leave it alone. Converting to
  layout panels is a separate, user-approved task.
- Migrating off `net48`, off WinForms, or to `dynamic`-free firewall COM interop.
- Adding logging, telemetry, DI, MVVM, async/await, or an abstraction layer over
  `FirewallService` / `GameProcessService`.
- Adding unit-test projects or test frameworks. `Test.ps1` is dependency-free by
  design; keep it that way.
- Changing the watchdog, single-instance, or recovery strategy.
- Localization, string edits, wording changes, or rebranding.
- Touching `legacy/`, `README.md` content, `CHANGELOG.md` history, or bumping the
  version. If the user wants a changelog entry, they will ask.
- "Fixing" anything in §4 that is not explicitly listed as a step, and redoing the
  cleanups listed as already resolved in finding 8.
- Renaming, resigning the accessibility of, or relocating to a sub-namespace any
  member listed in the §3.3 reflection table.

If you find a genuine bug while refactoring, **do not fix it**. Record it in the
final report with file and line, and continue.

---

## 9. Report format

Finish with a compact report:

1. Table: step → files touched → `Test.ps1` result → preview-hash result.
2. Any deviation from this brief, with the reason.
3. Bugs found but deliberately not fixed (file:line, one line each).
4. Line counts of `MainForm.cs` before and after.
5. Anything left undone and why.

State failures plainly, with the actual output. Do not report a step as done unless
the build and `Test.ps1` both passed for it.
