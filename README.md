# VaultLoop

VaultLoop is a Windows x64 application that creates or removes one outbound
Windows Firewall rule for GTA V and GTA V Enhanced no-save sessions. It verifies
the game executable and reports the rule state in a WinForms window, a tray icon,
and an optional HUD.

## Requirements

- Windows 10 version 1703 x64 or later. `app.manifest` declares `PerMonitorV2`
  DPI awareness, which earlier releases ignore.
- .NET Framework 4.8.
- A local `GTA5.exe` or `GTA5_Enhanced.exe` with a valid Rockstar Authenticode signature.
- Administrator approval when VaultLoop changes or restores the firewall rule.
- For source builds: Git, .NET SDK 8.0 or later, and the 64-bit .NET Framework
  4.8 reference assemblies. The project sets `LangVersion` to `preview` and uses
  C# 12 collection expressions and primary constructors.

## Install

Build the current revision:
```text
git clone https://github.com/imnotStealthy/VaultLoop.git
cd VaultLoop
dotnet build ReplayGlitchGTA.csproj -c Release -t:Ship
```

Published binaries are listed in [GitHub releases](https://github.com/imnotStealthy/VaultLoop/releases).
Each binary matches its tag and may not include changes from this branch.

The `Ship` target writes `publish\VaultLoop.exe`. Before running it with
administrator rights, copy it to a directory that `Authenticated Users` cannot modify.

To sign the shipped executable with a certificate available to `signtool`, run:

```text
dotnet build ReplayGlitchGTA.csproj -c Release -t:Ship -p:CertificateThumbprint=0123456789ABCDEF0123456789ABCDEF01234567
```

The timestamp server defaults to `https://timestamp.digicert.com`; override it with
`-p:TimestampUrl=<url>`.

## Usage

### Enable and disable no-save

1. Start GTA V or GTA V Enhanced and enter the intended activity.
2. Run `VaultLoop.exe`.
3. Select **LAUNCH AS ADMIN** and approve the Windows prompt.
4. Enable no-save with the on-screen control, the keyboard shortcut, or a
   configured controller shortcut.
5. Confirm that VaultLoop displays `ACTIVE`.
6. Complete the activity and wait until Story Mode has fully loaded.
7. Disable no-save and confirm `INACTIVE` before returning to GTA Online.

The default keyboard shortcut is `CTRL+SHIFT+F8`. Keyboard and controller
shortcuts trigger only while a verified GTA window is in the foreground.

A controller shortcut contains exactly two or three buttons. Hold the combination
for 500 ms, then release it to toggle no-save. Xbox controllers use XInput.
DualShock 4, DualSense, and DualSense Edge use Windows Raw Input over USB or Bluetooth.

Use **HUD ON** or **HUD OFF** to control the status HUD. The choice is stored and
restored at the next launch. The HUD appears only while a verified GTA window is
in the foreground.

Minimizing VaultLoop hides it in the system tray. The tray menu controls the
window, HUD, **START WITH WINDOWS**, and **EXIT & RESTORE**. Windows startup opens it in the tray.

[docs/ACTIVITY_ROTATION_GUIDE.md](docs/ACTIVITY_ROTATION_GUIDE.md) describes activity
rotation. [docs/CONTROLLER_SHORTCUT_DESIGN.md](docs/CONTROLLER_SHORTCUT_DESIGN.md)
documents the controller input design and its accepted behavior.

### Restore the firewall rule

Run the restore command if the application did not close normally:
```text
VaultLoop.exe --restore
```

VaultLoop also attempts restoration on normal exit, game loss, Windows shutdown,
the watchdog path, and the next launch when it finds a stale managed rule.

### Inspect the current state

Run:

```text
VaultLoop.exe --diagnose
```

The command reports the configured blocked set, the managed firewall-rule state,
TCP endpoints owned by the verified GTA process, and the controller shortcut. When
an Xbox controller shortcut is configured, it samples that controller for four
seconds and prints every combination it reads, so a combination that does not match
is visible. It does not change the firewall.

### Validate a source build

Run:

```text
dotnet build ReplayGlitchGTA.csproj -c Debug -o obj/preview
obj\preview\VaultLoop.exe --selftest
```

The self-test prints `Result = PASS` and exits with code `0` when every check
passes. It does not enable no-save or modify Windows Firewall.

## Configuration

File settings are stored under `%LOCALAPPDATA%\VaultLoop`.

| Setting | Default | Effect |
| --- | --- | --- |
| `shortcut.txt` | `CTRL+SHIFT+F8` | Stores the keyboard shortcut. |
| `controller-shortcut.txt` | Disabled | Stores the controller and its exact button combination. |
| `theme.txt` | Light | Stores `light` or `dark`. |
| `hud.txt` | On | Stores `on` or `off` for the status HUD. |
| `guide-step.txt` | Step 1 | Stores the current **HOW TO USE** page. |
| `endpoints.txt` | `192.81.241.171` | Replaces the built-in blocked set at the next launch. |
| `activity.log` | Empty | Written by VaultLoop. Records the last 400 firewall decisions and failures. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\VaultLoop` | Absent | Starts the current executable with `--startup`. |

`endpoints.txt` accepts one IPv4, IPv6, or CIDR prefix per line. Lines beginning
with `#` are comments. Every entry must be inside a configured Rockstar Online
Services allocation. VaultLoop rejects the whole file and uses the built-in value
when an entry is invalid or outside those allocations.

| Command-line flag | Availability | Effect |
| --- | --- | --- |
| `--diagnose` | All builds | Prints rule, endpoint, and connection information. |
| `--restore` | All builds | Removes the managed firewall rule. |
| `--startup` | All builds | Starts without showing the main window. |
| `--selftest` | Debug only | Runs read-only regression checks. |
| `--render-preview <path> [on\|unknown]` | Debug only | Writes a PNG of the main window. |

## Limitations

- VaultLoop runs only on Windows x64 and its interface is English-only.
- The managed rule applies only to a verified `GTA5.exe` or
  `GTA5_Enhanced.exe`. Other processes and unsigned copies are rejected.
- The built-in blocked set is the single address `192.81.241.171`. Rockstar can
  change its services, so the current address may stop affecting saves.
- VaultLoop validates the Windows rule. It does not prove how Rockstar services
  or a specific GTA update will behave.
- A firewall block does not close an existing TCP connection. Diagnostics must
  distinguish an existing flow from a new connection created after activation.
- A total power loss can prevent immediate cleanup. The next launch checks for a
  stale managed rule and attempts to remove it.
- Moving `VaultLoop.exe` after enabling **START WITH WINDOWS** leaves the stored
  registry command pointing to the old path. Disable and re-enable the option.
- Controllers not exposed through the supported XInput or Sony Raw Input paths
  are not configurable.
- A controller shortcut must be pressed exactly: an extra button held at the same
  time prevents it. An analog trigger counts as a button above 30 of 255. A trigger
  that never reads below that value is treated as resting and is ignored, which
  also means it cannot be used in a shortcut on that controller.
- A controller shortcut is bound to one device identity. Moving an Xbox
  controller to another XInput slot, or moving a PlayStation controller between
  USB and Bluetooth, requires configuring the shortcut again.
- The HUD is a normal top-most window. It does not draw over a game running in
  exclusive fullscreen.
- Unsigned builds can trigger a Windows SmartScreen warning.

## License

MIT. See [LICENSE](LICENSE).
