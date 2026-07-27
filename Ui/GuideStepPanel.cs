using System;
using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class GuideStepPanel : Panel
{
    private bool _isCurrent;

    internal bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }
            _isCurrent = value;
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    /// <summary>
    /// Repaints the step in one pair of colors. The number badge keeps its own accent, so it
    /// is the one child left untouched.
    /// </summary>
    internal void SetStepColors(Color background, Color foreground)
    {
        BackColor = background;
        for (var index = 1; index < Controls.Count; index++)
        {
            Controls[index].BackColor = background;
            Controls[index].ForeColor = foreground;
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new GuideStepAccessibleObject(this);

    internal void PerformDefaultAction() => OnClick(EventArgs.Empty);

    private sealed class GuideStepAccessibleObject(GuideStepPanel owner)
        : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State =>
            base.State |
            (owner.IsCurrent
                ? AccessibleStates.Checked | AccessibleStates.Selected
                : AccessibleStates.None);

        public override void DoDefaultAction() => owner.PerformDefaultAction();
    }
}
