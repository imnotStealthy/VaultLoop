# VaultLoop

VaultLoop is a Windows desktop controller for a narrowly scoped outbound firewall
rule used by GTA V no-save workflows. The UI is English-only and the release
artifact is a single `VaultLoop.exe`.

## Safety model

- The managed firewall rule is accepted as active only when every expected
  property matches.
- The rule is scoped to a locally installed, Authenticode-valid Rockstar
  `GTA5.exe` or `GTA5_Enhanced.exe`.
- The keyboard shortcut is armed only while that verified game is foreground.
- Injected keyboard events are rejected.
- Normal exit, Windows shutdown, a watchdog process, and next-start recovery all
  attempt to restore the rule.
- `VaultLoop.exe --restore` provides an explicit emergency restore command.

No process can guarantee cleanup after a total power loss. VaultLoop therefore
checks for and removes a stale managed rule at the next launch.

The application validates the Windows rule, not Rockstar server behavior. Game
updates can change endpoints or cooldowns, so the UI does not treat a rule as
proof of a successful online exploit.

## Usage

1. Start GTA V and enter the intended activity.
2. Use the on-screen toggle or the configured shortcut.
3. Confirm the VaultLoop toast and `ACTIVE` state.
4. Complete the activity and return fully to Story Mode.
5. Disable no-save and confirm `INACTIVE` before returning online.

The default shortcut is `Ctrl+Shift+F8`. It can be changed in the application.

## Build

The only supported build pipeline is:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```

The project targets .NET Framework 4.8, x64, with nullable analysis, deterministic
release output, an embedded Per-Monitor-V2 manifest, icon, logo, and an explicit
.NET Framework 4.8 startup check.

The build is staged and atomically replaces `publish\VaultLoop.exe` only after a
successful compilation.

### Authenticode signing

Pass a SHA-1 certificate thumbprint from `Cert:\CurrentUser\My`:

```powershell
.\Build.ps1 -CertificateThumbprint "0123456789ABCDEF0123456789ABCDEF01234567"
```

Without a trusted code-signing certificate, the build remains unsigned and
Windows SmartScreen may warn users. A self-signed certificate is not a substitute
for a trusted distribution certificate.

## Validation

Run the dependency-free regression checks:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1
```

The tests do not enable no-save or mutate Windows Firewall.

## Source layout

- `Program.cs` — startup, single instance, watchdog, recovery commands.
- `MainForm.cs` — UI, themes, shortcut handling, guide, status toast.
- `FirewallService.cs` — exact firewall rule state and mutation.
- `GameProcessService.cs` — GTA process and Authenticode validation.
- `AppSettingsStorage.cs` — atomic local preference persistence.
- `legacy\` — historical files that must not be run with VaultLoop.
