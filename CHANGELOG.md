# Changelog

## 1.2.0

- Added crash watchdog, next-start recovery, and `--restore`.
- Added exact `Inactive / Active / Invalid` firewall states.
- Scoped the rule to an Authenticode-valid Rockstar GTA executable.
- Rejected injected shortcut events and removed process work from the hook callback.
- Added GTA readiness status and non-activating in-game feedback.
- Added Per-Monitor-V2 DPI support and responsive guide scrolling.
- Improved keyboard accessibility, contrast, settings durability, and guide progress.
- Unified the release pipeline on .NET Framework 4.8 with optional Authenticode signing.
- Moved the obsolete AutoHotkey implementation into `legacy`.
