using InternetMonitor.Localization;

namespace InternetMonitor.UI;

/// <summary>
/// Custom outage popup (not a MessageBox) so the logo and layout can be controlled. Any close
/// the user triggers themselves - the in-form "Sluiten"/"Close" button, the title-bar X,
/// Alt+F4, or the system menu - raises <see cref="UserManuallyClosed"/>. A close issued by
/// <see cref="OutagePopupController"/> must go through <see cref="CloseProgrammatically"/>
/// instead of <c>Close()</c> so it is not mistaken for a manual close. This distinction can't
/// rely on WinForms' <c>CloseReason.UserClosing</c> alone: that reason is only set for
/// title-bar/system-menu closes, not for a regular content Button whose Click handler calls
/// Close() - which is exactly how the "Sluiten" button here works.
/// </summary>
public sealed class OutageNotificationForm : Form
{
    private const int FormWidth = 380;
    private const int ContentPadding = 16;

    private bool _closingProgrammatically;
    private readonly Image _logoImage;
    private readonly Font _headingFont;

    public event EventHandler? UserManuallyClosed;

    public OutageNotificationForm()
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        Text = LocalizationManager.Instance.Get("outage.title");

        int contentWidth = FormWidth - (2 * ContentPadding);
        int y = ContentPadding;

        _logoImage = TrayIconFactory.LoadLogoBitmap(40);
        var logo = new PictureBox
        {
            Image = _logoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(ContentPadding, y, 40, 40),
        };
        y += 40 + 12;

        var indicator = new Panel
        {
            BackColor = Color.OrangeRed,
            Bounds = new Rectangle(ContentPadding, y + 6, 12, 12),
        };

        _headingFont = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        var heading = new Label
        {
            Text = LocalizationManager.Instance.Get("outage.heading"),
            Font = _headingFont,
            AutoSize = false,
            Bounds = new Rectangle(ContentPadding + 20, y, contentWidth - 20, 24),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        y += 24 + 12;

        string messageText = LocalizationManager.Instance.Get("outage.message");
        Size measuredMessageSize = TextRenderer.MeasureText(
            messageText,
            Font,
            new Size(contentWidth, int.MaxValue),
            TextFormatFlags.WordBreak);
        var message = new Label
        {
            Text = messageText,
            AutoSize = false,
            Bounds = new Rectangle(ContentPadding, y, contentWidth, measuredMessageSize.Height),
        };
        y += measuredMessageSize.Height + 16;

        var closeButton = new Button
        {
            Text = LocalizationManager.Instance.Get("outage.close"),
            Size = new Size(80, 28),
            DialogResult = DialogResult.Cancel,
        };
        closeButton.Location = new Point(FormWidth - ContentPadding - closeButton.Width, y);
        closeButton.Click += (_, _) => Close();
        y += closeButton.Height + ContentPadding;

        ClientSize = new Size(FormWidth, y);

        Controls.Add(logo);
        Controls.Add(indicator);
        Controls.Add(heading);
        Controls.Add(message);
        Controls.Add(closeButton);

        FormClosing += OnFormClosing;
    }

    /// <summary>Closes the form without raising <see cref="UserManuallyClosed"/>.</summary>
    public void CloseProgrammatically()
    {
        _closingProgrammatically = true;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_closingProgrammatically)
        {
            UserManuallyClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // A fresh instance of this form is created for every outage episode, so the logo
            // Bitmap and heading Font (not owned by Control.Dispose since they're assigned, not
            // inherited from the base Font) must be freed explicitly here.
            _logoImage.Dispose();
            _headingFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
