using System.ComponentModel;
using System.Windows;
using AirPDF.Services;

namespace AirPDF;

public partial class SettingsWindow : Window
{
    private readonly AppTheme _initialTheme;
    private bool _suppressThemeEvents;
    private bool _accepted;

    public SettingsWindow()
    {
        InitializeComponent();
        _initialTheme = ThemeService.Current;
        _suppressThemeEvents = true;
        LightThemeRadio.IsChecked = _initialTheme == AppTheme.Light;
        DarkThemeRadio.IsChecked = _initialTheme == AppTheme.Dark;
        _suppressThemeEvents = false;
        Closing += SettingsWindow_Closing;
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeEvents)
            return;

        ThemeService.Apply(DarkThemeRadio.IsChecked == true ? AppTheme.Dark : AppTheme.Light);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var theme = DarkThemeRadio.IsChecked == true ? AppTheme.Dark : AppTheme.Light;
        ThemeService.Apply(theme);
        AppSettingsStore.Update(s => s.Theme = theme.ToString());
        _accepted = true;
        DialogResult = true;
    }

    private void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_accepted)
            ThemeService.Apply(_initialTheme);
    }
}
