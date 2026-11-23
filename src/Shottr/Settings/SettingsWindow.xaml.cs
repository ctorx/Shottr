using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using Shottr.Core;
using Shottr.Interop;
using Shottr.Update;

namespace Shottr.Settings;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private bool _saved;

    public SettingsWindow(App app)
    {
        InitializeComponent();
        _app = app;
        AppSettings s = app.Settings;

        // Release the global hotkeys so pressing them here records them instead of capturing.
        app.SuspendHotkeys();

        RegionHotkey.Hotkey = s.RegionHotkey;
        WindowHotkey.Hotkey = s.WindowHotkey;
        FullScreenHotkey.Hotkey = s.FullScreenHotkey;
        FolderBox.Text = s.SaveFolder;
        PatternBox.Text = s.FileNamePattern;
        FormatBox.SelectedIndex = s.Format == Core.ImageFormat.Jpg ? 1 : 0;
        QualitySlider.Value = s.JpegQuality;
        FullScreenBox.SelectedIndex = s.FullScreen == FullScreenMode.AllMonitors ? 1 : 0;
        MagnifierCheck.IsChecked = s.ShowMagnifier;
        CloseAfterCopyCheck.IsChecked = s.CloseEditorAfterCopy;
        StartupCheck.IsChecked = AppSettings.StartWithWindows;
        AutoUpdateCheck.IsChecked = s.CheckForUpdatesAutomatically;
        VersionText.Text = $"You have Shottr {Updater.CurrentVersion}.";
        CheckUpdatesButton.IsEnabled = !app.Updates.IsBusy;
        app.Updates.BusyChanged += OnUpdatesBusyChanged;
        PrintScreenWarning.Visibility = AppSettings.WindowsUsesPrintScreen ? Visibility.Visible : Visibility.Collapsed;

        Closed += (_, _) =>
        {
            _app.Updates.BusyChanged -= OnUpdatesBusyChanged;
            if (!_saved)
                _app.RegisterHotkeys();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.SetDarkTitleBar(new WindowInteropHelper(this).Handle, ThemeHelper.IsDarkMode);
    }

    private void PatternBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var preview = new AppSettings { FileNamePattern = PatternBox.Text, SaveFolder = Path.GetTempPath() };
        string example;
        try
        {
            example = Path.GetFileNameWithoutExtension(PreviewName(preview));
        }
        catch (Exception)
        {
            example = "(invalid pattern)";
        }
        PatternPreview.Text = $"Example: {example}. Text in braces is a date format, such as {{yyyy-MM-dd}} or {{HH.mm.ss}}.";
    }

    private static string PreviewName(AppSettings s)
    {
        DateTime now = DateTime.Now;
        return System.Text.RegularExpressions.Regex.Replace(s.FileNamePattern, @"\{([^}]+)\}", m =>
        {
            try { return now.ToString(m.Groups[1].Value); }
            catch (FormatException) { return m.Value; }
        });
    }

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityRow != null)
            QualityRow.Visibility = FormatBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where Shottr saves captures",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null,
        };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void FixPrintScreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard");
            key?.SetValue("PrintScreenKeyForSnippingEnabled", 0, RegistryValueKind.DWord);
            PrintScreenWarning.Visibility = Visibility.Collapsed;
            MessageBox.Show(this,
                "Print Screen is now free for Shottr. If it still opens Snipping Tool, sign out of Windows and back in.",
                "Shottr", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Shottr", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AppSettings s = _app.Settings;
        s.RegionHotkey = RegionHotkey.Hotkey;
        s.WindowHotkey = WindowHotkey.Hotkey;
        s.FullScreenHotkey = FullScreenHotkey.Hotkey;
        if (!string.IsNullOrWhiteSpace(FolderBox.Text))
            s.SaveFolder = FolderBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(PatternBox.Text))
            s.FileNamePattern = PatternBox.Text.Trim();
        s.Format = FormatBox.SelectedIndex == 1 ? Core.ImageFormat.Jpg : Core.ImageFormat.Png;
        s.JpegQuality = (int)QualitySlider.Value;
        s.FullScreen = FullScreenBox.SelectedIndex == 1 ? FullScreenMode.AllMonitors : FullScreenMode.MonitorUnderCursor;
        s.ShowMagnifier = MagnifierCheck.IsChecked == true;
        s.CloseEditorAfterCopy = CloseAfterCopyCheck.IsChecked == true;
        s.CheckForUpdatesAutomatically = AutoUpdateCheck.IsChecked == true;
        try
        {
            AppSettings.StartWithWindows = StartupCheck.IsChecked == true;
        }
        catch (Exception)
        {
            // Not being able to write the Run key is not worth blocking the save.
        }
        s.Save();
        _saved = true;

        var failed = _app.RegisterHotkeys();
        if (failed.Count > 0)
        {
            MessageBox.Show(this,
                $"These shortcuts are already used by another app: {string.Join(", ", failed)}. Pick different keys for them.",
                "Shottr", MessageBoxButton.OK, MessageBoxImage.Warning);
            _saved = false;
            return;
        }
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => _app.Updates.CheckNow();

    private void OnUpdatesBusyChanged(object? sender, EventArgs e) => CheckUpdatesButton.IsEnabled = !_app.Updates.IsBusy;
}
