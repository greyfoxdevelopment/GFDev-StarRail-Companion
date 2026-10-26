using System.Reflection;

namespace StarRailCompanionSetup;

internal sealed class SetupForm : Form
{
    private readonly CheckBox acceptLicense = new()
    {
        AutoSize = true,
        Text = "I have read and accept the license terms.",
        Location = new Point(20, 344)
    };

    private readonly TextBox installDirectory = new()
    {
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        Location = new Point(20, 407),
        Size = new Size(390, 27),
        Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Star Rail Companion")
    };

    private readonly CheckBox createShortcut = new()
    {
        AutoSize = true,
        Checked = true,
        Text = "Create a desktop shortcut",
        Location = new Point(20, 447)
    };

    private readonly Button installButton = new()
    {
        Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        Enabled = false,
        Location = new Point(340, 493),
        Size = new Size(150, 36),
        Text = "Install"
    };

    private readonly Label status = new()
    {
        Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        AutoEllipsis = true,
        Location = new Point(20, 498),
        Size = new Size(305, 28),
        Text = "The latest version will be downloaded from GitHub."
    };

    private readonly RichTextBox licenseText = new()
    {
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        Location = new Point(20, 78),
        ReadOnly = true,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        Size = new Size(470, 250)
    };

    private readonly Button browseButton = new()
    {
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
        Location = new Point(420, 405),
        Size = new Size(70, 30),
        Text = "Browse..."
    };

    public SetupForm()
    {
        Text = "Star Rail Companion Setup";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        ClientSize = new Size(510, 545);
        MinimumSize = new Size(526, 584);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var heading = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(20, 18),
            Text = "Star Rail Companion"
        };
        var licenseHeading = new Label
        {
            AutoSize = true,
            Location = new Point(20, 50),
            Text = "Review the license agreement:"
        };
        var folderHeading = new Label
        {
            AutoSize = true,
            Location = new Point(20, 382),
            Text = "Install location:"
        };

        using Stream license = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("StarRailCompanionSetup.LICENSE")
            ?? throw new InvalidOperationException("The license agreement could not be loaded.");
        using var reader = new StreamReader(license);
        licenseText.Text = reader.ReadToEnd();

        Controls.AddRange([
            heading, licenseHeading, licenseText, acceptLicense, folderHeading,
            installDirectory, browseButton, createShortcut, status, installButton
        ]);

        acceptLicense.CheckedChanged += (_, _) => installButton.Enabled = acceptLicense.Checked;
        browseButton.Click += BrowseForDirectory;
        installButton.Click += InstallAsync;
    }

    private void BrowseForDirectory(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where to install Star Rail Companion.",
            SelectedPath = installDirectory.Text,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            installDirectory.Text = dialog.SelectedPath;
    }

    private async void InstallAsync(object? sender, EventArgs e)
    {
        if (!acceptLicense.Checked)
            return;

        string destination = installDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show(this, "Choose an installation folder.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        installButton.Enabled = false;
        browseButton.Enabled = false;
        installDirectory.Enabled = false;
        createShortcut.Enabled = false;
        acceptLicense.Enabled = false;

        string? installedExecutable = null;
        try
        {
            status.Text = "Checking the latest GitHub release...";
            installedExecutable = await InstallerService.InstallLatestAsync(destination);

            string? shortcutWarning = null;
            if (createShortcut.Checked)
            {
                try
                {
                    InstallerService.CreateDesktopShortcut(installedExecutable);
                }
                catch (Exception ex)
                {
                    shortcutWarning = ex.Message;
                }
            }

            status.Text = "Installation complete.";
            if (shortcutWarning is not null)
            {
                MessageBox.Show(this,
                    $"The app was installed, but the desktop shortcut could not be created:\n\n{shortcutWarning}",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (MessageBox.Show(this, "Star Rail Companion was installed successfully. Launch it now?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installedExecutable)
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"The app was installed but could not be launched:\n\n{ex.Message}",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            Close();
        }
        catch (Exception ex)
        {
            status.Text = "Installation failed.";
            MessageBox.Show(this, $"Installation failed:\n\n{ex.Message}", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            installButton.Enabled = acceptLicense.Checked;
            browseButton.Enabled = true;
            installDirectory.Enabled = true;
            createShortcut.Enabled = true;
            acceptLicense.Enabled = true;
        }
    }
}
