using System;

namespace ReplayGlitchGTA;

internal static class GuideProgressSettings
{
    internal static int Load()
    {
        try
        {
            var value = AppSettingsStorage.ReadText(
                "guide-step.txt", includeLegacy: false, out _);
            return int.TryParse(value, out var step) &&
                   step >= 1 && step <= GuideDialog.StepCount
                ? step
                : 1;
        }
        catch
        {
            return 1;
        }
    }

    internal static void Save(int step)
    {
        if (step < 1 || step > GuideDialog.StepCount)
        {
            throw new ArgumentOutOfRangeException(nameof(step));
        }
        AppSettingsStorage.WriteText("guide-step.txt", step.ToString());
    }
}
