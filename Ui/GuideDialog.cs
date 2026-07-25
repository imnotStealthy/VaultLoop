using System;
using System.Drawing;
using System.Windows.Forms;

namespace ReplayGlitchGTA;

internal sealed class GuideDialog : BrutalistDialog
{
    private readonly GuideStepPanel[] _steps = new GuideStepPanel[6];
    private readonly Label _currentStepLabel;
    private readonly bool _darkMode;

    internal GuideDialog(bool darkMode) :
        base("HOW TO USE NO-SAVE", GetGuideSize(), darkMode ? Palette.DarkCanvas : Palette.Paper)
    {
        _darkMode = darkMode;
        AutoScroll = true;
        AutoScrollMinSize = new Size(0, 700);
        var canvas = darkMode ? Palette.DarkCanvas : Palette.Paper;
        var textColor = darkMode ? Palette.Paper : Palette.Ink;
        Controls.Add(new Label
        {
            Text = "VAULTLOOP WORKFLOW",
            Bounds = new Rectangle(28, 58, 350, 38),
            BackColor = canvas,
            ForeColor = textColor,
            Font = Typography.GuideTitle
        });
        Controls.Add(new Label
        {
            Text = "Click a step to mark your current position.",
            Bounds = new Rectangle(30, 93, 390, 22),
            BackColor = canvas,
            ForeColor = textColor,
            Font = Typography.GuideHint
        });
        _currentStepLabel = new Label
        {
            Bounds = new Rectangle(520, 68, 168, 34),
            BackColor = Palette.Acid,
            ForeColor = Palette.Ink,
            Font = Typography.CompactMono,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_currentStepLabel);

        var titles = new[]
        {
            "START THE ACTIVITY",
            "ENABLE NO-SAVE",
            "COMPLETE THE ACTIVITY",
            "RETURN TO STORY MODE",
            "DISABLE NO-SAVE",
            "RETURN TO GTA ONLINE"
        };
        var descriptions = new[]
        {
            "Launch a high-value mission or heist: Cayo Perico, Kortz Center, Diamond Casino, Doomsday, Dr. Dre, or another activity.",
            "Activate no-save as soon as the activity starts. Keep it active until the activity has been completed.",
            "Avoid failing if you want the Elite Challenge and its bonus. A failure normally removes only the associated bonus.",
            "After a successful completion, leave GTA Online and wait until Story Mode has fully loaded.",
            "Disable no-save and verify that the application clearly displays INACTIVE before continuing.",
            "Rejoin GTA Online. The reward should remain available while the activity can generally be replayed."
        };

        for (var index = 0; index < titles.Length; index++)
        {
            var stepNumber = index + 1;
            var panel = BuildStep(stepNumber, titles[index], descriptions[index],
                new Rectangle(28, 120 + index * 64, 664, 56), darkMode);
            foreach (Control child in panel.Controls)
            {
                child.Click += (_, _) =>
                {
                    panel.Focus();
                    SetCurrentStep(stepNumber, persist: true);
                };
            }
            panel.Click += (_, _) =>
            {
                panel.Focus();
                SetCurrentStep(stepNumber, persist: true);
            };
            var stepIndex = index;
            panel.KeyDown += (_, eventArgs) =>
            {
                if (eventArgs.KeyCode is Keys.Enter or Keys.Space)
                {
                    SetCurrentStep(stepNumber, persist: true);
                    eventArgs.Handled = true;
                }
                else if (eventArgs.KeyCode is Keys.Down or Keys.Right && stepIndex < 5)
                {
                    _steps[stepIndex + 1].Focus();
                    SetCurrentStep(stepIndex + 2, persist: true);
                    eventArgs.Handled = true;
                }
                else if (eventArgs.KeyCode is Keys.Up or Keys.Left && stepIndex > 0)
                {
                    _steps[stepIndex - 1].Focus();
                    SetCurrentStep(stepIndex, persist: true);
                    eventArgs.Handled = true;
                }
            };
            panel.Enter += (_, _) => panel.Invalidate();
            panel.Leave += (_, _) => panel.Invalidate();
            panel.Paint += (_, eventArgs) =>
            {
                if (panel.Focused)
                {
                    ControlPaint.DrawFocusRectangle(eventArgs.Graphics,
                        Rectangle.Inflate(panel.ClientRectangle, -4, -4));
                }
            };
            _steps[index] = panel;
            Controls.Add(panel);
        }

        Controls.Add(new Label
        {
            Text = "TIP — COOLDOWN\n" +
                   "Group heists: 48 minutes. Solo Cayo Perico: 144 minutes. " +
                   "Other activities may use a different timer.",
            Bounds = new Rectangle(28, 510, 664, 48),
            BackColor = Palette.Blue,
            ForeColor = Palette.Ink,
            Font = Typography.SmallBold,
            Padding = new Padding(14, 6, 14, 6)
        });
        Controls.Add(new Label
        {
            Text = "WARNING — USE AT YOUR OWN RISK\n" +
                   "Online exploits may cause progress loss, transaction rollback, suspension, or account sanctions. " +
                   "The perceived risk may be low, but no method is completely risk-free.",
            Bounds = new Rectangle(28, 568, 664, 78),
            BackColor = Palette.AlertRed,
            ForeColor = Palette.Ink,
            Font = Typography.StatusDetail,
            Padding = new Padding(14, 9, 14, 9)
        });
        var closeButton = CreateButton("GOT IT", new Rectangle(582, 654, 110, 36), Palette.Ink, Palette.Paper);
        closeButton.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Controls.Add(closeButton);
        AcceptButton = closeButton;
        SetCurrentStep(GuideProgressSettings.Load(), persist: false);
    }

    protected override void OnShown(EventArgs e)
    {
        var workingArea = Screen.FromControl(Owner ?? this).WorkingArea;
        var dpiScale = DeviceDpi / 96F;
        var maximumWidth = Math.Max(320, (int)(workingArea.Width * 0.9));
        var maximumHeight = Math.Max(320, (int)(workingArea.Height * 0.9));
        ClientSize = new Size(
            Math.Min((int)Math.Round(720 * dpiScale), maximumWidth),
            Math.Min((int)Math.Round(700 * dpiScale), maximumHeight));
        CenterToParent();
        base.OnShown(e);
    }

    private static Size GetGuideSize()
    {
        var workingHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 768;
        return new Size(720, Math.Min(700, Math.Max(520, (int)(workingHeight * 0.9))));
    }

    private static GuideStepPanel BuildStep(
        int number, string title, string description, Rectangle bounds,
        bool darkMode)
    {
        var neutral = darkMode ? Palette.DarkSurface : Palette.GuideNeutral;
        var textColor = darkMode ? Palette.Paper : Palette.Ink;
        var panel = new GuideStepPanel
        {
            Bounds = bounds,
            BackColor = neutral,
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand,
            TabStop = true,
            AccessibleRole = AccessibleRole.RadioButton,
            AccessibleName = $"Step {number}: {title}",
            AccessibleDescription = description,
            AccessibleDefaultActionDescription = "Mark as current step"
        };
        panel.Controls.Add(new Label
        {
            Text = number.ToString("00"),
            Bounds = new Rectangle(0, 0, 58, 60),
            BackColor = Palette.Yellow,
            ForeColor = Palette.Ink,
            Font = Typography.AccentTitle,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        });
        panel.Controls.Add(new Label
        {
            Text = title,
            Bounds = new Rectangle(72, 4, 565, 20),
            BackColor = panel.BackColor,
            ForeColor = textColor,
            Font = Typography.GuideStepTitle,
            Cursor = Cursors.Hand
        });
        panel.Controls.Add(new Label
        {
            Text = description,
            Bounds = new Rectangle(72, 23, 576, 30),
            BackColor = panel.BackColor,
            ForeColor = textColor,
            Font = Typography.GuideStepBody,
            Cursor = Cursors.Hand
        });
        return panel;
    }

    private void SetCurrentStep(int step, bool persist)
    {
        _currentStepLabel.Text = $"CURRENT STEP  {step} / 6";
        for (var index = 0; index < _steps.Length; index++)
        {
            var color = index == step - 1
                ? Palette.Acid
                : _darkMode ? Palette.DarkSurface : Palette.GuideNeutral;
            _steps[index].IsCurrent = index == step - 1;
            _steps[index].BackColor = color;
            for (var childIndex = 1; childIndex < _steps[index].Controls.Count; childIndex++)
            {
                _steps[index].Controls[childIndex].BackColor = color;
                _steps[index].Controls[childIndex].ForeColor =
                    index == step - 1 || !_darkMode ? Palette.Ink : Palette.Paper;
            }
        }
        if (persist)
        {
            try
            {
                GuideProgressSettings.Save(step);
            }
            catch
            {
                // Progress tracking is optional and must never block the guide.
            }
        }
    }
}
