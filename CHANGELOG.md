# Changelog

## Unreleased

- Fixed DualShock 4 and DualSense shortcuts, which never worked: the
  `RID_DEVICE_INFO` structure was declared 24 bytes instead of 32, so Windows
  refused every device query and no PlayStation controller was ever identified.
- Fixed keyboard and controller shortcuts being disarmed for the duration of
  every runtime refresh, which silently swallowed a keystroke made in that
  window.
- Moved firewall changes off the UI thread. Confirming a rule could block the
  message loop for about two seconds, during which the window froze and
  controller input was not pumped.
- Raised the Authenticode cache lifetime to the documented 300 seconds. The game
  executable was re-hashed every 30 seconds during play.
- Controllers are polled only while a shortcut is configured or a capture is
  running, instead of continuously.
- Fixed the **HOW TO USE** window placing its close button out of reach on a
  display shorter than 700 logical pixels, and made Escape close it.
- A rejected `endpoints.txt` is now reported in the window instead of falling
  back to the built-in address set in silence.
- Added a local activity log under `%LOCALAPPDATA%\VaultLoop`.
- Added the MIT license, a continuous integration workflow, and recorded the
  minimum Windows and .NET SDK versions in the README.

Note: releases 1.2.2 to 1.2.4 have no entry here. Whether they were published is
not recorded in this repository.

## 1.2.5

- Added optional controller shortcuts for Xbox, DualShock 4, DualSense, and
  DualSense Edge controllers. A shortcut uses an exact two- or three-button
  combination held for 500 milliseconds.
- Added system tray controls, an optional foreground-only status HUD, and a
  **START WITH WINDOWS** option.
- Applied the same verified-GTA foreground gate to keyboard and controller
  shortcuts.
- Reorganized controller input, runtime polling, tray handling, and the mini HUD
  into focused files without changing firewall behavior or the window layout.

## 1.2.1

- Changed elevation to on demand: VaultLoop starts as the current user and requests
  administrator rights only when a firewall change is required.
- Reorganized the codebase without changing behavior: window layout separated from
  window behavior, Authenticode verification and its cache extracted from game
  detection, and the duplicated firewall, status, settings, and window-chrome code
  reduced to single implementations.

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
