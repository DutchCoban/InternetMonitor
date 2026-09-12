using System.Reflection;
using InternetMonitor.Localization;

namespace InternetMonitor.UI;

public sealed class AboutForm : Form
{
    private readonly Font _nameFont;
    private readonly Image _logoImage;

    public AboutForm()
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = LocalizationManager.Instance.Get("about.title");
        ClientSize = new Size(320, 220);

        _logoImage = TrayIconFactory.LoadLogoBitmap(48);
        var logo = new PictureBox
        {
            Image = _logoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(16, 16, 48, 48),
        };

        Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

        _nameFont = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        var nameLabel = new Label
        {
            Text = "Internet Monitor",
            Font = _nameFont,
            AutoSize = false,
            Bounds = new Rectangle(76, 20, 220, 24),
        };

        var versionLabel = new Label
        {
            Text = LocalizationManager.Instance.Format("about.version", $"{version.Major}.{version.Minor}.{version.Build}"),
            AutoSize = false,
            Bounds = new Rectangle(76, 46, 220, 20),
        };

        var descriptionLabel = new Label
        {
            Text = LocalizationManager.Instance.Get("about.description"),
            AutoSize = false,
            Bounds = new Rectangle(16, 80, 270, 20),
        };

        var languageLabel = new Label
        {
            Text = LocalizationManager.Instance.Format("about.language", LocalizationManager.Instance.CurrentLanguage == "en" ? "English" : "Nederlands"),
            AutoSize = false,
            Bounds = new Rectangle(16, 104, 270, 20),
        };

        var closeButton = new Button
        {
            Text = LocalizationManager.Instance.Get("about.close"),
            Bounds = new Rectangle(210, 140, 80, 28),
            DialogResult = DialogResult.OK,
        };
        closeButton.Click += (_, _) => Close();

        Controls.Add(logo);
        Controls.Add(nameLabel);
        Controls.Add(versionLabel);
        Controls.Add(descriptionLabel);
        Controls.Add(languageLabel);
        Controls.Add(closeButton);
        AcceptButton = closeButton;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // This form is constructed fresh every time "About" is opened from the tray menu, so
            // the custom Font and the logo Bitmap (Control.Dispose doesn't own either since
            // they're assigned, not inherited from a designer/base Control) must be freed
            // explicitly or each open leaks a GDI handle.
            _nameFont.Dispose();
            _logoImage.Dispose();
        }

        base.Dispose(disposing);
    }
}
