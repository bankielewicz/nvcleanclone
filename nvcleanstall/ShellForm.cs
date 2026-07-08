using Microsoft.Web.WebView2.WinForms;

namespace CleanDriver;

// Native Windows 11 shell: a fixed-size window whose entire client area is a
// WebView2 rendering the wizard served by the in-process Kestrel server.
public class ShellForm : Form
{
    public ShellForm(string url)
    {
        Text = "CleanDriver";
        ClientSize = new Size(960, 640);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var webview = new WebView2
        {
            Dock = DockStyle.Fill,
            Source = new Uri(url),
        };
        Controls.Add(webview);
    }
}
