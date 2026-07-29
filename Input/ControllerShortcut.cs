using System;
using System.Collections.Generic;

namespace ReplayGlitchGTA;

[Flags]
internal enum ControllerButtons : uint
{
    None = 0,
    DPadUp = 1u << 0,
    DPadDown = 1u << 1,
    DPadLeft = 1u << 2,
    DPadRight = 1u << 3,
    Back = 1u << 4,
    Start = 1u << 5,
    LeftStick = 1u << 6,
    RightStick = 1u << 7,
    LeftShoulder = 1u << 8,
    RightShoulder = 1u << 9,
    LeftTrigger = 1u << 10,
    RightTrigger = 1u << 11,
    Guide = 1u << 12,
    Touchpad = 1u << 13,
    South = 1u << 14,
    East = 1u << 15,
    West = 1u << 16,
    North = 1u << 17
}

internal enum ControllerDeviceKind
{
    XInput = 1,
    DualShock4 = 2,
    DualSense = 3
}

internal sealed class ControllerShortcut
{
    private const ControllerButtons AllButtons =
        ControllerButtons.DPadUp | ControllerButtons.DPadDown |
        ControllerButtons.DPadLeft | ControllerButtons.DPadRight |
        ControllerButtons.Back | ControllerButtons.Start |
        ControllerButtons.LeftStick | ControllerButtons.RightStick |
        ControllerButtons.LeftShoulder | ControllerButtons.RightShoulder |
        ControllerButtons.LeftTrigger | ControllerButtons.RightTrigger |
        ControllerButtons.Guide | ControllerButtons.Touchpad |
        ControllerButtons.South | ControllerButtons.East |
        ControllerButtons.West | ControllerButtons.North;
    private const ControllerButtons DPadButtons =
        ControllerButtons.DPadUp | ControllerButtons.DPadDown |
        ControllerButtons.DPadLeft | ControllerButtons.DPadRight;

    internal ControllerShortcut(
        ControllerDeviceKind deviceKind, string deviceId, ControllerButtons buttons)
    {
        if (!Enum.IsDefined(typeof(ControllerDeviceKind), deviceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(deviceKind));
        }
        if (!IsValidDeviceId(deviceKind, deviceId))
        {
            throw new ArgumentException("Invalid controller identity.", nameof(deviceId));
        }
        if (!IsValidCombination(buttons))
        {
            throw new ArgumentException(
                "A controller shortcut must contain two or three buttons.", nameof(buttons));
        }

        DeviceKind = deviceKind;
        DeviceId = deviceId;
        Buttons = buttons;
    }

    internal ControllerDeviceKind DeviceKind { get; }
    internal string DeviceId { get; }
    internal ControllerButtons Buttons { get; }

    internal string DisplayName => DeviceKind switch
    {
        ControllerDeviceKind.XInput =>
            $"Xbox Controller {ParseXInputSlot(DeviceId) + 1}",
        ControllerDeviceKind.DualShock4 => "DualShock 4",
        ControllerDeviceKind.DualSense => "DualSense",
        _ => "Controller"
    };

    internal string Format() => $"{DisplayName}  //  {FormatButtons(DeviceKind, Buttons)}";

    internal static bool IsValidCombination(ControllerButtons buttons) =>
        buttons != ControllerButtons.None &&
        (buttons & ~AllButtons) == ControllerButtons.None &&
        CountInputs(buttons) is >= 2 and <= 3;

    internal static bool IsExactCombination(
        ControllerButtons pressed, ControllerButtons configured) =>
        IsValidCombination(configured) && pressed == configured;

    internal static bool IsValidDeviceId(ControllerDeviceKind deviceKind, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 2048)
        {
            return false;
        }
        foreach (var character in deviceId)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        if (deviceKind == ControllerDeviceKind.XInput)
        {
            return ParseXInputSlot(deviceId) is >= 0 and <= 3;
        }
        return deviceId.StartsWith(@"\\?\", StringComparison.Ordinal);
    }

    internal static int CountButtons(ControllerButtons buttons)
    {
        var value = (uint)buttons;
        var count = 0;
        while (value != 0)
        {
            count += (int)(value & 1);
            value >>= 1;
        }
        return count;
    }

    internal static int CountInputs(ControllerButtons buttons)
    {
        var dPad = buttons & DPadButtons;
        return CountButtons(buttons & ~DPadButtons) +
               (dPad == ControllerButtons.None ? 0 : 1);
    }

    internal static string FormatButtons(
        ControllerDeviceKind deviceKind, ControllerButtons buttons)
    {
        var names = new List<string>();
        Add(names, buttons, ControllerButtons.LeftShoulder,
            deviceKind == ControllerDeviceKind.XInput ? "LB" : "L1");
        Add(names, buttons, ControllerButtons.RightShoulder,
            deviceKind == ControllerDeviceKind.XInput ? "RB" : "R1");
        Add(names, buttons, ControllerButtons.LeftTrigger,
            deviceKind == ControllerDeviceKind.XInput ? "LT" : "L2");
        Add(names, buttons, ControllerButtons.RightTrigger,
            deviceKind == ControllerDeviceKind.XInput ? "RT" : "R2");
        Add(names, buttons, ControllerButtons.LeftStick, "L3");
        Add(names, buttons, ControllerButtons.RightStick, "R3");
        Add(names, buttons, ControllerButtons.Back,
            deviceKind switch
            {
                ControllerDeviceKind.XInput => "VIEW",
                ControllerDeviceKind.DualShock4 => "SHARE",
                _ => "CREATE"
            });
        Add(names, buttons, ControllerButtons.Start,
            deviceKind == ControllerDeviceKind.XInput ? "MENU" : "OPTIONS");
        Add(names, buttons, ControllerButtons.Guide,
            deviceKind == ControllerDeviceKind.XInput ? "XBOX" : "PS");
        Add(names, buttons, ControllerButtons.Touchpad, "TOUCHPAD");
        Add(names, buttons, ControllerButtons.DPadUp, "DPAD UP");
        Add(names, buttons, ControllerButtons.DPadDown, "DPAD DOWN");
        Add(names, buttons, ControllerButtons.DPadLeft, "DPAD LEFT");
        Add(names, buttons, ControllerButtons.DPadRight, "DPAD RIGHT");
        Add(names, buttons, ControllerButtons.South,
            deviceKind == ControllerDeviceKind.XInput ? "A" : "CROSS");
        Add(names, buttons, ControllerButtons.East,
            deviceKind == ControllerDeviceKind.XInput ? "B" : "CIRCLE");
        Add(names, buttons, ControllerButtons.West,
            deviceKind == ControllerDeviceKind.XInput ? "X" : "SQUARE");
        Add(names, buttons, ControllerButtons.North,
            deviceKind == ControllerDeviceKind.XInput ? "Y" : "TRIANGLE");
        return string.Join(" + ", names);
    }

    internal static int ParseXInputSlot(string deviceId) =>
        deviceId.StartsWith("xinput:", StringComparison.Ordinal) &&
        int.TryParse(deviceId.Substring("xinput:".Length), out var slot)
            ? slot
            : -1;

    private static void Add(
        ICollection<string> names, ControllerButtons value,
        ControllerButtons button, string name)
    {
        if ((value & button) != 0)
        {
            names.Add(name);
        }
    }
}
