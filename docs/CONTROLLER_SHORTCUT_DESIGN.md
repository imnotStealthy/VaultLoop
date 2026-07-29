# Controller Shortcut Design

## Goal

Add an optional controller-only shortcut that toggles no-save while a verified
GTA V or GTA V Enhanced window is in the foreground. Keyboard shortcuts remain
independent and unchanged.

## Accepted behavior

- Disabled until explicitly configured.
- Supports Xbox controllers through XInput.
- Supports DualShock 4 and DualSense through Raw Input/HID over USB or Bluetooth.
- Binds to the controller used during capture.
- Requires an exact combination of two or three buttons.
- Requires the combination to remain stable for 500 ms.
- Fires once, then remains latched until every configured button is released.
- Extra pressed buttons invalidate the combination.
- Disconnecting the configured controller cancels capture and disarms the shortcut.
- A USB/Bluetooth transport change may require reconfiguration.
- Generic, Switch, PS3, and virtual controllers are outside the initial scope.

## Architecture

`ControllerShortcutService` owns capture, input state, the hold timer, and the
single-fire latch. It polls XInput slots and consumes Raw Input messages forwarded
by `MainForm`. `ControllerShortcutSettings` persists the source, device identity,
display name, and button set in `%LOCALAPPDATA%\VaultLoop`.

The existing shortcut dialog gains a controller section for configure, replace,
and clear operations. The first supported controller that supplies button input
during capture becomes the selected device.

At runtime, `MainForm` arms both keyboard and controller shortcuts from the same
verified foreground-game context. The controller service also uses the same
administrator, applying, and known-state guard as the keyboard hook. Its event
enters the existing hotkey toggle path, so firewall validation and restore
behavior remain unchanged.

## Safety and failure behavior

- Controller input is observed and never swallowed.
- Raw Input is registered only for gamepad and joystick usages.
- Unsupported or malformed HID reports are ignored.
- An unavailable or ambiguous saved device remains disabled.
- No network access, telemetry, package, driver, or controller emulator is added.
- Physical USB/Bluetooth behavior requires manual verification with real hardware.

## Decision log

- Hybrid XInput plus Raw Input/HID selected for background input.
- Windows.Gaming.Input rejected because the app is not foreground during play.
- XInput-only rejected because it cannot provide native PlayStation support.
- Existing shortcut dialog selected instead of adding another main-window control.
- Exact multi-button capture and hold/release gating selected to prevent gameplay
  actions from causing accidental toggles.
