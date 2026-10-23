using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NvUv.Dcc.Core;

namespace NvUv.Dcc.App;

internal partial class GamesProfilesWindow : Window
{
    private const double BaseWidth = 640;
    private const double BaseHeight = 360;

    private readonly MainWindow? _host;
    private readonly IReadOnlyCollection<DccGameHistoryEntry> _history;
    private IReadOnlyCollection<DccLearnedProfile> _profiles;
    private IReadOnlyList<HistoryRow> _rows;
    private bool _updatingLanguage;

    public GamesProfilesWindow(MainWindow host)
        : this(host.BrowserHistory, host.BrowserProfiles)
    {
        _host = host;
        Owner = host;
        PerformanceLoggingCheckBox.IsChecked = host.BrowserPerformanceLoggingEnabled;
        SelectLanguage(host.BrowserLanguage);
        ApplyLocalization();
    }

    public GamesProfilesWindow(
        IReadOnlyCollection<DccGameHistoryEntry> history,
        IReadOnlyCollection<DccLearnedProfile> profiles)
    {
        InitializeComponent();
        Title = DccLocalization.T("games_window_title");
        _history = history.ToArray();
        _profiles = profiles.ToArray();
        _rows = BuildRows(_history, _profiles);
        SelectLanguage(DccLocalization.Language);
        ApplyLocalization();
        ApplyFilter();
    }

    internal void ApplyPreviewSettings(string? language, int? scalePercent)
    {
        string previewLanguage = language is "de" or "en" or "ru" or "es" ? language : "en";
        DccLocalization.SetLanguage(previewLanguage);
        SelectLanguage(previewLanguage);
        ApplyLocalization();
        if (scalePercent.HasValue)
        {
            double scale = Math.Clamp(scalePercent.Value, 80, 150) / 100d;
            Width = BaseWidth * scale;
            Height = BaseHeight * scale;
        }
    }

    private void ApplyLocalization()
    {
        Title = DccLocalization.T("games_window_title");
        TitleBarText.Text = Title;
        HistoryTitleText.Text = DccLocalization.T("history_title");
        HistorySubtitleText.Text = DccLocalization.T("history_subtitle");
        LanguageLabelText.Text = DccLocalization.T("language").ToUpper(CultureInfo.CurrentCulture);
        LanguageComboBox.ToolTip = DccLocalization.T("language_tip");
        PerformanceLoggingCheckBox.Content = DccLocalization.T("history_performance_logging");
        PerformanceLoggingCheckBox.ToolTip = DccLocalization.T("history_performance_logging_tip");
        SearchLabelText.Text = DccLocalization.T("search");
        SearchBox.ToolTip = DccLocalization.T("history_search_tip");
        GameHeaderText.Text = DccLocalization.T("history_game_exe");
        LastSetupHeaderText.Text = DccLocalization.T("history_last_setup");
        LearnedHeaderText.Text = DccLocalization.T("history_learned");
        LastUsedHeaderText.Text = DccLocalization.T("history_last_used");
        EmptyText.Text = DccLocalization.T("history_empty");
        ExportPerformanceButton.Content = DccLocalization.T("history_export_performance");
        ExportPerformanceButton.ToolTip = DccLocalization.T("history_export_performance_tip");
        ExportProfilesButton.Content = DccLocalization.T("history_export_profiles");
        ExportProfilesButton.ToolTip = DccLocalization.T("menu_export_tip");
        ResetProfileButton.Content = DccLocalization.T("history_reset_profile");
        ResetProfileButton.ToolTip = DccLocalization.T("history_reset_profile_tip");
        ExportDiagnosticsButton.Content = DccLocalization.T("history_diagnostics");
        ExportDiagnosticsButton.ToolTip = DccLocalization.T("menu_export_diagnostics_tip");
        OpenFolderButton.Content = DccLocalization.T("history_data_folder");
        OpenFolderButton.ToolTip = DccLocalization.T("menu_folder_tip");
        _rows = BuildRows(_history, _profiles);
        ApplyFilter();
    }

    private void SelectLanguage(string language)
    {
        _updatingLanguage = true;
        try
        {
            LanguageComboBox.SelectedItem = LanguageComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, language, StringComparison.Ordinal));
        }
        finally
        {
            _updatingLanguage = false;
        }
    }

    private static IReadOnlyList<HistoryRow> BuildRows(
        IEnumerable<DccGameHistoryEntry> history,
        IEnumerable<DccLearnedProfile> profiles)
    {
        Dictionary<string, DccLearnedProfile[]> profilesByExecutable = profiles
            .GroupBy(profile => DccGameCatalog.NormalizeExecutable(profile.Executable), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(profile => profile.TargetFps).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return history
            .OrderByDescending(entry => entry.LastActivatedUtc)
            .Select(entry =>
            {
                string executable = DccGameCatalog.NormalizeExecutable(entry.Executable);
                string gameKey = entry.EffectiveGameKey;
                profilesByExecutable.TryGetValue(gameKey, out DccLearnedProfile[]? learnedProfiles);
                DccLearnedProfile? learnedProfile = learnedProfiles?
                    .FirstOrDefault(profile => profile.TargetFps == entry.LastTargetFps)
                    ?? learnedProfiles?.OrderByDescending(profile => profile.UpdatedAtUtc).FirstOrDefault();
                string learned = learnedProfile is not null
                    ? learnedProfile.TargetFps == DccLearnedProfile.AutomaticTargetFps
                        ? learnedProfile.LowLoadClockMHz is int lowLoadClock &&
                          lowLoadClock < learnedProfile.LearnedClockMHz
                            ? $"{lowLoadClock}–{learnedProfile.LearnedClockMHz} MHz · AUTO"
                            : $"{learnedProfile.LearnedClockMHz} MHz · AUTO"
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"{learnedProfile.LearnedClockMHz} MHz @ {learnedProfile.TargetFps} FPS")
                    : entry.LastMethod == DccControlMethod.NvidiaEfficiency
                        ? DccLocalization.T("history_nvidia_managed")
                        : DccLocalization.T("history_learning_pending");
                string method = entry.LastMethod == DccControlMethod.NvidiaEfficiency ? "NVIDIA" : "DCC";
                string target = entry.LastMethod == DccControlMethod.NvidiaEfficiency
                    ? entry.LastFrameLimiterEnabled
                        ? $"{DccLocalization.T("nvidia_target_fps")}: {entry.LastTargetFps} / {entry.LastFrameLimitFps} FPS cap"
                        : $"{DccLocalization.T("nvidia_target_fps")}: {entry.LastTargetFps} / {DccLocalization.T("history_no_cap")}"
                    : entry.LastFrameLimiterEnabled
                        ? $"AUTO / {entry.LastFrameLimitFps} FPS cap"
                        : $"AUTO / {method}";
                return new HistoryRow(
                    entry.Name,
                    executable,
                    gameKey,
                    target,
                    learned,
                    FormatLastUsed(entry.LastActivatedUtc),
                    entry.NvidiaProfileName,
                    learnedProfile is not null);
            })
            .ToArray();
    }

    private static string FormatLastUsed(DateTimeOffset value)
    {
        DateTime local = value.ToLocalTime().DateTime;
        DateTime today = DateTime.Today;
        if (local.Date == today)
            return DccLocalization.T("history_today");
        if (local.Date == today.AddDays(-1))
            return DccLocalization.T("history_yesterday");
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ResetProfileButton.IsEnabled =
            _host is not null && HistoryList.SelectedItem is HistoryRow { HasLearnedProfile: true };

    private void ApplyFilter()
    {
        if (SearchBox is null || HistoryList is null || CountText is null || EmptyText is null)
            return;
        string query = SearchBox.Text.Trim();
        HistoryRow[] filtered = string.IsNullOrWhiteSpace(query)
            ? _rows.ToArray()
            : _rows.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        HistoryList.ItemsSource = filtered;
        CountText.Text = DccLocalization.T("history_count", filtered.Length, _rows.Count);
        EmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLanguage || LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: string language })
            return;

        if (_host is not null)
            _host.SetLanguageFromBrowser(language);
        else
            DccLocalization.SetLanguage(language);
        ApplyLocalization();
    }

    private async void PerformanceLoggingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null)
            return;
        await _host.SetPerformanceLoggingFromBrowserAsync(PerformanceLoggingCheckBox.IsChecked == true, this);
        PerformanceLoggingCheckBox.IsChecked = _host.BrowserPerformanceLoggingEnabled;
    }

    private void ExportProfilesButton_Click(object sender, RoutedEventArgs e) =>
        _host?.ExportProfilesFromBrowser(this);

    private void ResetProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null || HistoryList.SelectedItem is not HistoryRow { HasLearnedProfile: true } row)
            return;

        if (MessageBox.Show(
                this,
                DccLocalization.T("history_reset_profile_confirm", row.Name),
                DccLocalization.T("history_title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (_host.ResetLearnedProfileFromBrowser(row.GameKey, this) <= 0)
            return;

        _profiles = _host.BrowserProfiles.ToArray();
        _rows = BuildRows(_history, _profiles);
        HistoryList.SelectedItem = null;
        ApplyFilter();
    }

    private void ExportPerformanceButton_Click(object sender, RoutedEventArgs e) =>
        _host?.ExportPerformanceLogsFromBrowser(this);

    private void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e) =>
        _host?.ExportDiagnosticsFromBrowser(this);

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e) =>
        _host?.OpenDataFolderFromBrowser(this);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record HistoryRow(
        string Name,
        string Executable,
        string GameKey,
        string LastSetup,
        string Learned,
        string LastUsed,
        string NvidiaProfileName,
        bool HasLearnedProfile)
    {
        public string SearchText =>
            $"{Name}\n{Executable}\n{GameKey}\n{LastSetup}\n{Learned}\n{LastUsed}\n{NvidiaProfileName}";
    }
}
