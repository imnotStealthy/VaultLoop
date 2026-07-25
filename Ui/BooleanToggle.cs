using System;
using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class BooleanToggle : Control
{
    private bool _checked;
    private bool _isStateKnown = true;
    private bool _isRecoveryMode;

    internal bool IsRecoveryMode
    {
        get => _isRecoveryMode;
        set
        {
            if (_isRecoveryMode == value)
            {
                return;
            }
            _isRecoveryMode = value;
            AccessibleDefaultActionDescription = value
                ? "Restore firewall rule"
                : Checked ? "Disable no-save" : "Enable no-save";
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    internal bool IsStateKnown
    {
        get => _isStateKnown;
        set
        {
            if (_isStateKnown == value)
            {
                return;
            }
            _isStateKnown = value;
            AccessibleDefaultActionDescription = value
                ? (Checked ? "Disable no-save" : "Enable no-save")
                : "State unavailable";
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    internal bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }
            _checked = value;
            AccessibleDefaultActionDescription = value ? "Disable no-save" : "Enable no-save";
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    internal event EventHandler? ToggleRequested;

    internal BooleanToggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleDefaultActionDescription = "Enable no-save";
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new BooleanToggleAccessibleObject(this);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var track = new Rectangle(2, 2, Width - 12, Height - 12);
        var shadow = new Rectangle(track.X + 8, track.Y + 8, track.Width, track.Height);
        var knobWidth = 94;
        var knob = new Rectangle(
            Checked ? track.Right - knobWidth - 8 : track.X + 8,
            track.Y + 8, knobWidth, track.Height - 16);
        var labelArea = Checked
            ? new Rectangle(track.X + 8, track.Y, track.Width - knobWidth - 22, track.Height)
            : new Rectangle(track.X + knobWidth + 14, track.Y, track.Width - knobWidth - 22, track.Height);

        using var shadowBrush = new SolidBrush(Palette.Ink);
        using var trackBrush = new SolidBrush(
            !IsStateKnown ? Palette.Yellow : Checked ? Palette.HotPink : Palette.Acid);
        using var knobBrush = new SolidBrush(Palette.Paper);
        using var borderPen = new Pen(Palette.Ink, 4F);
        using var labelFont = new Font("Impact", 23F);
        using var knobFont = new Font("Consolas", 9F, FontStyle.Bold);

        e.Graphics.FillRectangle(shadowBrush, shadow);
        e.Graphics.FillRectangle(trackBrush, track);
        e.Graphics.DrawRectangle(borderPen, track);
        if (IsRecoveryMode)
        {
            TextRenderer.DrawText(e.Graphics, "RESTORE", labelFont, track, Palette.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else if (!IsStateKnown)
        {
            TextRenderer.DrawText(e.Graphics, "STATE ?", labelFont, track, Palette.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            e.Graphics.FillRectangle(knobBrush, knob);
            e.Graphics.DrawRectangle(borderPen, knob);
            TextRenderer.DrawText(e.Graphics, Checked ? "ON" : "OFF", labelFont, labelArea, Palette.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, "NO-SAVE", knobFont, knob, Palette.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (Focused)
        {
            var focus = Rectangle.Inflate(track, -7, -7);
            ControlPaint.DrawFocusRectangle(e.Graphics, focus, Palette.Ink, Checked ? Palette.HotPink : Palette.Acid);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        RequestToggle();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            RequestToggle();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void RequestToggle()
    {
        if (Enabled && (IsStateKnown || IsRecoveryMode))
        {
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class BooleanToggleAccessibleObject(BooleanToggle owner)
        : ControlAccessibleObject(owner)
    {
        public override string? DefaultAction =>
            owner.IsRecoveryMode ? "Restore firewall rule" :
            !owner.IsStateKnown ? "State unavailable" :
            owner.Checked ? "Disable no-save" : "Enable no-save";

        public override AccessibleStates State =>
            base.State |
            (owner.IsStateKnown && owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);

        public override void DoDefaultAction() => owner.RequestToggle();
    }
}
