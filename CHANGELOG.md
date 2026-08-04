# Changelog

## 1.2.6

- When GTA V is not running, **WAITING FOR GTA** is shown in red and **NO-SAVE**
  is disabled and grayed; restoration remains available.
- The **HUD ON** / **HUD OFF** choice is now stored in `hud.txt` and restored at
  the next launch.
- Fixed three ways the keyboard shortcut could stop toggling no-save until
  VaultLoop was restarted: a modifier whose press or release was injected, or
  happened while the hook could not observe it, left the tracked keyboard state
  wrong; a release that was never observed left every later press swallowed
  without toggling anything; and a reconfigured shortcut was published to the
  hook as two separate values, which could be read as a combination that was
  never configured.
- Fixed a controller shortcut that could never fire on a controller resting on an
  analog trigger. A trigger counts as a pressed button above 30 of 255, and a worn
  one can sit above that on its own — measured at 53 on the controller this was
  found with. Every reading then carried that trigger, and the combination is
  matched exactly, so no configured shortcut could ever match. A trigger that has
  not been read below its threshold since the controller appeared is now treated
  as resting rather than pressed.
- A controller combination held for its full duration and refused by the gate now
  reports the missing condition, like the keyboard shortcut.
- Fixed a controller polling race that could stop the timer just after a shortcut
  was configured or capture began.
- Disarming the keyboard shortcut now clears its refusal latch, so the next refused
  press can report its missing condition again.
- `--diagnose` reports the configured controller shortcut, the connected XInput
  controllers, their analog trigger values, and a four-second sample of what the
  application actually reads from the configured one.
- **CONFIGURE SHORTCUT** now reads the keyboard wherever the focus is inside the
  dialog. Capture was bound to the capture field alone, so clicking **REPLACE**
  or **CLEAR** for the controller silently stopped every later key press from
  being read. A press that is not a usable shortcut is also reported on screen
  instead of by a system beep alone.
- A keyboard shortcut refused because VaultLoop is not running as administrator,
  because no verified GTA window is in the foreground, or because the firewall
  state is unavailable now says so in a status toast and in the activity log. It
  previously did nothing at all, which was indistinguishable from a broken
  keyboard.
- The version is displayed next to the window title.
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
