using System.Windows.Forms;

namespace ReplayGlitchGTA;

/// <summary>
/// Lets a borderless window be dragged by its own chrome.
/// </summary>
/// <remarks>
/// Every window here declares <see cref="FormBorderStyle.None"/> to keep the brutalist title
/// bar, which also removes the caption Windows would otherwise drag. Releasing the capture and
/// posting a non-client caption click hands the drag back to the window manager, so snapping
/// and multi-monitor behaviour keep working as they do for a normal title bar.
/// </remarks>
internal static class WindowDrag
{
    internal static void Attach(Form window, params Control[] dragHandles)
    {
        foreach (var dragHandle in dragHandles)
        {
            dragHandle.MouseDown += (_, eventArgs) =>
            {
                if (eventArgs.Button != MouseButtons.Left)
                {
                    return;
                }
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(window.Handle,
                    NativeMethods.NonClientLeftButtonDown, NativeMethods.HitCaption, 0);
            };
        }
    }
}
