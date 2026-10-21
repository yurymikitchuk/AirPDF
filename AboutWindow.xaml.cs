using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace AirPDF;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null
            ? "Номер версии: 0.9.5"
            : $"Номер версии: {version.Major}.{version.Minor}.{version.Build}";
    }

    private void ReleaseLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: ignore if the shell cannot open the URL.
        }

        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
