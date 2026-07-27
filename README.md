![VaultLoop banner](docs/vaultloop-banner.png)

# VaultLoop

VaultLoop is a Windows desktop controller for a narrowly scoped outbound firewall
rule used by GTA V no-save workflows. The UI is English-only and the release
artifact is a single `VaultLoop.exe`.

## Safety model

- The managed firewall rule is accepted as active only when every expected
  property matches, including an exact match on the blocked address set.
- The rule is scoped to a locally installed, Authenticode-valid Rockstar
  `GTA5.exe` or `GTA5_Enhanced.exe`. Because the rule also names that executable,
  nothing outside the game is affected even though the rule targets a network
  range rather than a single address.
- The keyboard shortcut is armed only while that verified game is foreground.
- Injected keyboard events are rejected.
- Normal exit, Windows shutdown, a watchdog process, and next-start recovery all
  attempt to restore the rule.
- If the verified game disappears while no-save is active, the rule is restored
  automatically after a few seconds. The rule names the game executable by path,
  so leaving it in place would silently block a relaunched GTA.
- While no-save is active, the application watches the game's connections. If GTA
  opens a *new* connection to a blocked address, the block is not working and the
  status bar switches to `BLOCK NOT EFFECTIVE`.
- `VaultLoop.exe --restore` provides an explicit emergency restore command.

No process can guarantee cleanup after a total power loss. VaultLoop therefore
checks for and removes a stale managed rule at the next launch.

The application validates the Windows rule, not Rockstar server behavior. Game
updates can change endpoints or cooldowns, so the UI does not treat a rule as
proof of a successful online exploit.

## Blocked address set

The set is a single address:

| Address | Source |
| --- | --- |
| `192.81.241.171` | Inside Take-Two's RSONET-NA1 (ARIN allocation "RSGEWR"). Observed in the game's own traffic, and the only address the original AutoHotkey script blocked. |

**Confirmed working.** With this address blocked and the game correctly detected,
GTA reports `SAVING FAILED — the Rockstar cloud servers are currently unavailable`
and the session stays alive, which is the wanted behaviour.

Reaching that took two corrections, and both matter if the behaviour ever regresses:

- **Game detection has to work first.** Resolving the game's path through
  `Process.MainModule` fails against anti-cheat protection, so VaultLoop reported
  "Start a verified copy of GTA V" and **never created a rule at all**. Every
  earlier no-save attempt was silently a no-op, which looked like the block being
  ineffective. See `GameProcessService.TryGetProcessImagePath`.
- **The set must not be widened.** Blocking the surrounding `192.81.241.0/24`
  reaches Rockstar authentication: the game drops the session mid-activity with
  `Unable to connect to Rockstar Games Services to authenticate`, before any save
  would happen. The neighbours of this address carry auth traffic.

Use these three outcomes to tell a misconfiguration apart from a game update:

| What the game shows | What it means |
| --- | --- |
| `SAVING FAILED — ... your progress will be saved when the connection is re-established.` | **Correct.** The save fails, the session survives. |
| `Unable to connect to Rockstar Games Services to authenticate` | The blocked set reached the authentication path. Too wide. |
| The activity is consumed, no message at all | Nothing was blocked. Check that the game is detected — run `--diagnose`. |

### Tuning the set

The built-in set is the working one, so no configuration is needed. If a game
update moves the endpoint, create `%LOCALAPPDATA%\VaultLoop\endpoints.txt`, one
address or prefix per line; `#` starts a comment. It replaces the built-in set at
the next application start, and deleting it restores the built-in behaviour.

```
# One address or CIDR prefix per line.
192.81.241.171
```

Every entry must sit inside a known Rockstar Online Services allocation
(`192.81.240.0/21`, `104.255.104.0/22`, `198.133.210.0/24`, `164.153.136.0/22`,
`2620:11b:c000::/44`). Anything else — a typo, an over-eager edit, a Zynga or
Take-Two corporate range — is refused and the built-in set is used instead, with
the reason printed by `--diagnose`. That guard is what makes a user-editable file
safe for a rule created by an elevated process.

`--diagnose` always reports which set is active and where it came from. Use it
while the game is running to see which endpoints are actually in use.

## Usage

1. Start GTA V and enter the intended activity.
2. Use the on-screen toggle or the configured shortcut.
3. Confirm the VaultLoop toast and `ACTIVE` state.
4. Complete the activity and return fully to Story Mode.
5. Disable no-save and confirm `INACTIVE` before returning online.

The default shortcut is `Ctrl+Shift+F8`. It can be changed in the application.

VaultLoop starts without administrator rights. The first action that must change
Windows Firewall triggers one UAC prompt and relaunches VaultLoop elevated for the
rest of that session. Startup requests elevation only when a stale managed rule
must be restored.

## Build

The build is driven entirely by `dotnet`; no build script is involved.

```sh
dotnet build ReplayGlitchGTA.csproj -c Release
```

The project targets .NET Framework 4.8, x64, with nullable analysis, deterministic
release output, an embedded Per-Monitor-V2 manifest, icon, logo, and an explicit
.NET Framework 4.8 startup check. Warnings are errors, `net48` is resolved against
the locally installed framework (so the build works offline), and two build gates
run automatically:

- `ValidateManifest` — fails unless `app.manifest` still declares
  `asInvoker`, `PerMonitorV2`, `longPathAware`, and the Windows 10/11
  compatibility GUID.
- `ValidateSingleFileOutput` — fails if a release build emits anything besides
  `VaultLoop.exe`.

To copy the validated binary to `publish\VaultLoop.exe`:

```sh
dotnet build ReplayGlitchGTA.csproj -c Release -t:Ship
```

### Authenticode signing

Pass a SHA-1 certificate thumbprint to the `Ship` target; it invokes `signtool`
from the Windows SDK:

```sh
dotnet build ReplayGlitchGTA.csproj -c Release -t:Ship -p:CertificateThumbprint=0123456789ABCDEF0123456789ABCDEF01234567
```

The timestamp server defaults to `https://timestamp.digicert.com` and can be
overridden with `-p:TimestampUrl=...`. Without a trusted code-signing certificate,
the build remains unsigned and Windows SmartScreen may warn users. A self-signed
certificate is not a substitute for a trusted distribution certificate.

## Validation

The regression checks live in the application itself and are compiled into `DEBUG`
builds only. They are read-only and run without elevation:

```sh
dotnet build ReplayGlitchGTA.csproj -c Debug -o obj/preview
obj/preview/VaultLoop.exe --selftest
```

The command prints one line per check, ends with `Result = PASS` or `FAIL`, and
exits with code `0` or `1`. The checks are read-only: they do not enable no-save
and never mutate Windows Firewall.

`DEBUG` builds also expose `--render-preview <path> [on|unknown]`, which renders
the main window to a PNG. Comparing the SHA-256 of those PNGs before and after a
UI change is the pixel-parity regression net. The render depends on the stored
theme and shortcut preferences, so compare a change against a freshly built
previous commit rather than against a stored PNG:

```sh
git worktree add /tmp/baseline HEAD --detach
dotnet build /tmp/baseline/ReplayGlitchGTA.csproj -c Debug -o /tmp/baseline-out
/tmp/baseline-out/VaultLoop.exe --render-preview /tmp/base.png
obj/preview/VaultLoop.exe --render-preview /tmp/changed.png
sha256sum /tmp/base.png /tmp/changed.png
git worktree remove /tmp/baseline --force
```

### Connection diagnostics

Available in every build, without elevation, and read-only:

```sh
VaultLoop.exe --diagnose
```

It prints the blocked set, the managed rule state, and every TCP endpoint the
verified game process is connected to, flagging which fall inside the blocked set
and which are Rockstar-owned but uncovered.

Exit code `2` means the rule reports `ACTIVE` while an established connection
remains inside the blocked set. That is *expected* shortly after enabling no-save:
a block rule does not tear down a flow that was already open. It only proves the
block is ineffective if the reported **local port** changes between two runs,
because the game then completed a new handshake through the active rule. The
running application applies exactly this test and raises `BLOCK NOT EFFECTIVE`
on its own.

## Source layout

- `Program.cs` — startup, single instance, watchdog, recovery commands.
- `MainForm.cs` — main window chrome, layout, and firewall state orchestration.
- `SelfTest.cs` — `--selftest` regression checks (`DEBUG` only).
- `DiagnosticsReport.cs` — the `--diagnose` command.
- `FirewallService.cs` — exact firewall rule state and mutation.
- `GameProcessService.cs` — GTA process and Authenticode validation.
- `AppSettingsStorage.cs` — atomic local preference persistence.
- `Network\` — address prefixes, the ARIN-sourced Rockstar tables, and the
  read-only TCP connection inspector.
- `Ui\` — palette, typography, control factories, theme controller, dialogs.
- `Input\` — the low-level keyboard hook.
- `Interop\` — P/Invoke declarations, structs, and message constants.
- `Settings\` — shortcut, theme, and guide-progress persistence.
