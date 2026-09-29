using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Skypeek.App.Infrastructure;
using Skypeek.Core.Credentials;
using Skypeek.Core.Health;
using Skypeek.Core.Models;
using Skypeek.Storage;

namespace Skypeek.App.Views;

public partial class SettingsView
{
    public sealed record IntervalOption(string Label, int Minutes);
    /// <param name="SameKey">"Same key as read-only": resolves to the target's own profile.</param>
    public sealed record ProfileOption(string Label, string? Name, bool SameKey = false);

    private static readonly ProfileOption SameKeyOption = new("Same key as read-only (exception, not recommended)", null, SameKey: true);
    public sealed record ScopeOption(string Label, long? TargetId);
    public sealed record OverrideRow(string Key, string Label, string Values);
    public sealed record SuppressionRow(AlarmSuppression Rule, string Pattern, string Scope);
    public sealed record CauseRow(CauseSuppression Rule, string Pattern, string Scope);

    private static readonly IntervalOption[] CatalogOptions =
        [new("Off", 0), new("Every hour", 60), new("Every 3 hours", 180), new("Every 6 hours", 360), new("Every 12 hours", 720), new("Daily", 1440)];
    private static readonly IntervalOption[] HealthOptions =
        [new("Off", 0), new("1 minute", 1), new("2 minutes", 2), new("5 minutes", 5), new("15 minutes", 15), new("30 minutes", 30)];
    private static readonly IntervalOption[] MetricsOptions =
        [new("Off", 0), new("1 minute", 1), new("5 minutes", 5), new("15 minutes", 15), new("30 minutes", 30)];
    private static readonly IntervalOption[] NetworkOptions =
        [new("Only on demand", 0), new("Every 15 minutes", 15), new("Every hour", 60), new("Every 6 hours", 360), new("Daily", 1440)];

    private readonly AppSession _session;
    private readonly List<Target> _targets;
    private readonly HashSet<long> _deleted = new();

    // Overrides and suppressions can also change from the dashboard; keep only the edits made here and merge on save.
    private readonly HashSet<string> _removedOverrides = new();
    private readonly List<AlarmSuppression> _addedSuppressions = new();
    private readonly List<AlarmSuppression> _removedSuppressions = new();
    private readonly List<CauseSuppression> _removedCauses = new();

    private Target? _editing;
    private bool _loadingEditor;
    private long _nextTempId = -1;
    private string _hotkey;

    public SettingsView(AppSession session)
    {
        InitializeComponent();
        _session = session;
        _targets = session.Settings.Targets.Select(t => t.Clone()).ToList();
        var settings = session.Settings.Settings;
        _hotkey = session.Vault.Meta.Hotkey;

        CatalogInterval.ItemsSource = CatalogOptions;
        HealthInterval.ItemsSource = HealthOptions;
        MetricsInterval.ItemsSource = MetricsOptions;
        NetworkInterval.ItemsSource = NetworkOptions;
        Region.ItemsSource = AwsRegions.All;

        LoadGeneral(settings);
        LoadMonitoring(settings);
        LoadProfiles(settings.ShowNonReadOnlyProfiles);
        RefreshTargetList();
        if (_targets.Count > 0)
            TargetList.SelectedIndex = 0;

        VaultPath.Text = $"Vault: {App.Current.VaultDirectory}";
        CredentialsPath.Text = $"Credentials file: {session.Monitor.CredentialsPath} (watched for changes)";
        ShowNonReadOnly.Checked += (_, _) => LoadProfiles(true);
        ShowNonReadOnly.Unchecked += (_, _) => LoadProfiles(false);
        // Same switch, next to the profile picker in Add targets (saved with Settings → General).
        NewShowAll.IsChecked = ShowNonReadOnly.IsChecked;
        NewShowAll.Checked += (_, _) => ShowNonReadOnly.IsChecked = true;
        NewShowAll.Unchecked += (_, _) => ShowNonReadOnly.IsChecked = false;
        ShowNonReadOnly.Checked += (_, _) => NewShowAll.IsChecked = true;
        ShowNonReadOnly.Unchecked += (_, _) => NewShowAll.IsChecked = false;
    }

    /// <summary>Called when the tab becomes visible: pick up profiles, overrides and suppressions changed elsewhere.</summary>
    public void OnShown()
    {
        LoadProfiles(ShowNonReadOnly.IsChecked == true);
        RefreshOverrides();
        RefreshSuppressions();
        RefreshCauses();
    }

    // ---------------- load ----------------

    private void LoadGeneral(AppSettings s)
    {
        HotkeyBox.Text = _hotkey;
        HotkeyStatus.Text = App.Current.Hotkey.Current == _hotkey ? "registered" : "not registered (taken by another app?)";
        RunOnStartup.IsChecked = AutostartService.IsEnabled();
        LockoutMinutes.Text = s.LockoutMinutes.ToString(CultureInfo.InvariantCulture);
        LockOnWindowsLock.IsChecked = s.LockOnWindowsLock;
        WarningsRed.IsChecked = s.WarningsTurnIconRed;
        RetentionDays.Text = s.RequestLogRetentionDays.ToString(CultureInfo.InvariantCulture);
        ShowNonReadOnly.IsChecked = s.ShowNonReadOnlyProfiles;
    }

    private void LoadMonitoring(AppSettings s)
    {
        SetThresholdBoxes(s.EcsThresholds, GEcsCpuWarn, GEcsCpuCrit, GEcsMemWarn, GEcsMemCrit);
        SetThresholdBoxes(s.EbThresholds, GEbCpuWarn, GEbCpuCrit, GEbMemWarn, GEbMemCrit);
        SetThresholdBoxes(s.RdsThresholds, GRdsCpuWarn, GRdsCpuCrit, GRdsConnWarn, GRdsConnCrit);
        SetThresholdBoxes(s.CacheThresholds, GCacheCpuWarn, GCacheCpuCrit, GCacheMemWarn, GCacheMemCrit);
        SetThresholdBoxes(s.Ec2Thresholds, GEc2CpuWarn, GEc2CpuCrit, GEc2MemWarn, GEc2MemCrit);
        GRdsStorageWarn.Text = s.RdsStorageWarn.ToString(CultureInfo.InvariantCulture);
        GRdsStorageCrit.Text = s.RdsStorageCritical.ToString(CultureInfo.InvariantCulture);
        SustainedMinutes.Text = s.SustainedMinutes.ToString(CultureInfo.InvariantCulture);
        AlarmHours.Text = s.AlarmHistoryHours.ToString(CultureInfo.InvariantCulture);
        IgnoreTargetTracking.IsChecked = s.IgnoreTargetTrackingAlarms;
        RefreshOverrides();
        RefreshSuppressions();
        RefreshCauses();
    }

    private Dictionary<string, ThresholdSettings> MergedOverrides() =>
        _session.Settings.Settings.ResourceThresholds.Where(kv => !_removedOverrides.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    private List<AlarmSuppression> MergedSuppressions() =>
        _session.Settings.Settings.SuppressedAlarms.Where(s => !_removedSuppressions.Contains(s))
            .Concat(_addedSuppressions)
            .DistinctBy(s => (s.Pattern.ToLowerInvariant(), s.TargetId))
            .ToList();

    private List<CauseSuppression> MergedCauses() =>
        _session.Settings.Settings.SuppressedCauses.Where(s => !_removedCauses.Contains(s)).ToList();

    private void RefreshCauses() =>
        CauseSuppressionList.ItemsSource = MergedCauses().Select(s => new CauseRow(s, s.Pattern, s.Scope(_targets))).ToList();

    private void OnRemoveCauseSuppression(object sender, RoutedEventArgs e)
    {
        if (CauseSuppressionList.SelectedItem is not CauseRow row)
            return;
        _removedCauses.Add(row.Rule);
        RefreshCauses();
        SaveStatus.Text = "Cause suppression removed — Save to apply.";
    }

    private void RefreshOverrides() =>
        OverrideList.ItemsSource = MergedOverrides()
            .Select(kv => new OverrideRow(kv.Key, DescribeKey(kv.Key), $"CPU {kv.Value.CpuWarn}/{kv.Value.CpuCritical} · {(kv.Key.Contains(":rds:") ? "Conn" : "Mem")} {kv.Value.MemWarn}/{kv.Value.MemCritical}"
                + (kv.Value.StorageWarn is { } sw ? $" · Storage {sw}/{kv.Value.StorageCritical}" : "")))
            .OrderBy(r => r.Label)
            .ToList();

    private void RefreshSuppressions()
    {
        SuppressionList.ItemsSource = MergedSuppressions()
            .Select(s => new SuppressionRow(s, s.Pattern, s.Scope(_targets)))
            .OrderBy(r => r.Pattern, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<ScopeOption> scopes = [new("All targets", null), .. _targets.Where(t => t.Id > 0).Select(t => new ScopeOption($"Only {t.DisplayName}", t.Id))];
        NewPatternScope.ItemsSource = scopes;
        NewPatternScope.SelectedIndex = 0;
    }

    private string DescribeKey(string key)
    {
        var parts = key.Split(':', 3);
        if (parts.Length == 3 && long.TryParse(parts[0], out var id) && _targets.FirstOrDefault(t => t.Id == id) is { } t)
            return $"{t.DisplayName} · {parts[1]} · {parts[2]}";
        return key;
    }

    private void LoadProfiles(bool includeNonReadOnly)
    {
        var all = _session.Monitor.Profiles.Values.OrderBy(p => p.Name).ToList();
        var readOnly = all.Where(p => p.IsReadOnly || includeNonReadOnly).ToList();
        var selected = (NewProfile.SelectedItem as ProfileCredentials)?.Name;
        NewProfile.ItemsSource = readOnly;
        NewProfile.SelectedItem = readOnly.FirstOrDefault(p => p.Name == selected) ?? readOnly.FirstOrDefault();

        List<ProfileOption> elevated = [new("(none)", null), SameKeyOption, .. all.Where(p => !p.IsReadOnly).Select(p => new ProfileOption(p.Name, p.Name)),
            .. all.Where(p => p.IsReadOnly).Select(p => new ProfileOption($"{p.Name} (read-only role)", p.Name))];
        NewElevated.ItemsSource = elevated;
        NewElevated.SelectedIndex = 0;
        ElevatedProfile.ItemsSource = elevated;
        if (_editing is not null)
            SelectElevated(_editing.ElevatedProfileName);
    }

    private void SelectElevated(string? name)
    {
        var options = ElevatedProfile.ItemsSource as IEnumerable<ProfileOption> ?? [];
        if (_editing is { UsesSameKey: true } && name == _editing.ProfileName)
        {
            ElevatedProfile.SelectedItem = options.FirstOrDefault(o => o.SameKey);
            return;
        }
        var match = options.FirstOrDefault(o => o.Name == name);
        if (match is null && name is not null)
        {
            // Profile no longer in the credentials file: keep it visible so saving does not silently drop it.
            var list = options.ToList();
            list.Add(match = new ProfileOption($"{name} (missing from credentials file)", name));
            ElevatedProfile.ItemsSource = list;
        }
        ElevatedProfile.SelectedItem = match ?? options.FirstOrDefault();
    }

    private void OnNewProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        NewRegions.ItemsSource = AwsRegions.All;
        NewRegions.SelectedItems.Clear();
        if (NewProfile.SelectedItem is not ProfileCredentials p)
            return;
        NewRegions.SelectedItems.Add(p.DefaultRegion is { } r && AwsRegions.All.Contains(r) ? r : "us-east-1");

        // Suggest the elevated key already used with this read-only profile, or the same account's non-read-only profile.
        var existing = _targets.FirstOrDefault(t => t.ProfileName == p.Name && t.ElevatedProfileName is not null)?.ElevatedProfileName;
        var sameAccount = _session.Monitor.Profiles.Values.FirstOrDefault(x => !x.IsReadOnly && x.AccountId is not null && x.AccountId == p.AccountId)?.Name;
        var suggestion = existing ?? sameAccount;
        NewElevated.SelectedItem = (NewElevated.ItemsSource as IEnumerable<ProfileOption>)?.FirstOrDefault(o => o.Name == suggestion)
                                   ?? (NewElevated.ItemsSource as IEnumerable<ProfileOption>)?.FirstOrDefault();
    }

    private void RefreshTargetList()
    {
        var selected = (TargetList.SelectedItem as Target)?.Id;
        TargetList.ItemsSource = null;
        TargetList.ItemsSource = _targets.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        if (selected is not null)
            TargetList.SelectedItem = _targets.FirstOrDefault(t => t.Id == selected);
    }

    // ---------------- targets ----------------

    private void OnAddTargets(object sender, RoutedEventArgs e)
    {
        if (NewProfile.SelectedItem is not ProfileCredentials profile)
            return;
        var option = NewElevated.SelectedItem as ProfileOption;
        var elevated = option?.SameKey == true ? profile.Name : option?.Name;
        if (elevated == profile.Name ? !ConfirmSameKey(profile.Name) : !profile.IsReadOnly && !ConfirmNonReadOnly(profile.Name))
            return;
        Target? last = null;
        foreach (var region in NewRegions.SelectedItems.Cast<string>())
        {
            if (_targets.Any(t => t.ProfileName == profile.Name && t.Region == region))
                continue;
            last = new Target
            {
                Id = _nextTempId--,
                ProfileName = profile.Name,
                ElevatedProfileName = elevated,
                Region = region,
                Alias = profile.AccountId ?? profile.Name,
            };
            _targets.Add(last);
        }
        RefreshTargetList();
        if (last is not null)
            TargetList.SelectedItem = _targets.First(t => t.Id == last.Id);
        SaveStatus.Text = last is null ? "Those targets already exist." : "Added — review schedules, then Save.";
    }

    private void OnRemoveTarget(object sender, RoutedEventArgs e)
    {
        if (TargetList.SelectedItem is not Target t)
            return;
        if (!ConfirmDialog.Ask(Window.GetWindow(this), $"Remove {t.DisplayName}?", "Its cached lists and health data are deleted when you save.", "Remove", danger: true))
            return;
        _targets.Remove(t);
        if (t.Id > 0)
            _deleted.Add(t.Id);
        _editing = null;
        RefreshTargetList();
        Editor.IsEnabled = false;
    }

    private void OnTargetSelected(object sender, SelectionChangedEventArgs e)
    {
        _editing = TargetList.SelectedItem as Target;
        Editor.IsEnabled = _editing is not null;
        if (_editing is null)
            return;

        _loadingEditor = true;
        var t = _editing;
        Alias.Text = t.Alias;
        Region.Text = t.Region;
        TargetEnabled.IsChecked = t.Enabled;
        var profile = _session.Monitor.Profiles.GetValueOrDefault(t.ProfileName);
        ProfileText.Text = profile is null
            ? $"Read-only: {t.ProfileName} — not found in the credentials or config file"
            : $"Read-only: {t.ProfileName} · account {profile.AccountId ?? "?"} · role {profile.RoleName ?? "?"} · {profile.SourceText}"
              + (profile.IsReadOnly ? "" : "  ⚠ not a read-only role");
        SelectElevated(t.ElevatedProfileName);
        FeatSecrets.IsChecked = t.SecretsEnabled;
        FeatParams.IsChecked = t.ParamsEnabled;
        FeatEb.IsChecked = t.EbEnabled;
        FeatEcs.IsChecked = t.EcsEnabled;
        FeatRds.IsChecked = t.RdsEnabled;
        FeatCache.IsChecked = t.CacheEnabled;
        FeatEc2.IsChecked = t.Ec2Enabled;
        FeatElb.IsChecked = t.ElbEnabled;
        FeatNetwork.IsChecked = t.NetworkEnabled;
        NetworkInterval.SelectedItem = NetworkOptions.FirstOrDefault(o => o.Minutes == t.NetworkIntervalMinutes) ?? NetworkOptions[2];

        var preset = CatalogOptions.FirstOrDefault(o => o.Minutes == t.CatalogIntervalMinutes);
        CatalogInterval.SelectedItem = preset;
        CatalogCustom.Text = preset is null ? t.CatalogIntervalMinutes.ToString(CultureInfo.InvariantCulture) : "";
        HealthInterval.SelectedItem = HealthOptions.FirstOrDefault(o => o.Minutes == t.HealthIntervalMinutes) ?? HealthOptions[3];
        MetricsInterval.SelectedItem = MetricsOptions.FirstOrDefault(o => o.Minutes == t.MetricsIntervalMinutes) ?? MetricsOptions[2];

        OverrideEcs.IsChecked = t.EcsThresholds is not null;
        SetThresholdBoxes(t.EcsThresholds ?? _session.Settings.Settings.EcsThresholds, EcsCpuWarn, EcsCpuCrit, EcsMemWarn, EcsMemCrit);
        OverrideEb.IsChecked = t.EbThresholds is not null;
        SetThresholdBoxes(t.EbThresholds ?? _session.Settings.Settings.EbThresholds, EbCpuWarn, EbCpuCrit, EbMemWarn, EbMemCrit);
        _loadingEditor = false;
        ApplyEditor();
    }

    private void OnEditorChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingEditor)
            ApplyEditor();
    }

    /// <summary>Choosing the read-only profile as the elevated one too is allowed only after an explicit warning.</summary>
    private void OnElevatedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || _editing is not { } t)
            return;
        var option = ElevatedProfile.SelectedItem as ProfileOption;
        var same = option?.SameKey == true || option?.Name == t.ProfileName;
        if (same && !t.UsesSameKey && !ConfirmSameKey(t.ProfileName))
        {
            _loadingEditor = true;
            SelectElevated(t.ElevatedProfileName);
            _loadingEditor = false;
            return;
        }
        if (option is { SameKey: false } && option.Name == t.ProfileName)
        {
            // Same profile picked by name: show it as the explicit exception.
            _loadingEditor = true;
            ElevatedProfile.SelectedItem = (ElevatedProfile.ItemsSource as IEnumerable<ProfileOption>)?.FirstOrDefault(o => o.SameKey);
            _loadingEditor = false;
        }
        ApplyEditor();
    }

    private bool ConfirmSameKey(string profile) => ConfirmDialog.Ask(Window.GetWindow(this),
        "Use the same key for read-only and elevated access?",
        $"{profile} will sign every call: background refresh, and also the elevated actions (reveal with the elevated key, EB log requests, " +
        "deploy, restart, reboot, terminate, EC2 start/stop and security group rule changes). You lose the separation between a key that can only read and one that can change things: " +
        "if this key has write permissions, a bug or a mistaken approval uses them directly.\n\n" +
        "Skypeek still blocks every call that is not on its allowlist and still asks before each elevated action. " +
        "Use this only when the account has no separate read-only role.",
        "Use the same key", danger: true);

    private bool ConfirmNonReadOnly(string profile) => ConfirmDialog.Ask(Window.GetWindow(this),
        "Use a non-read-only role as the read-only key?",
        $"{profile} is not a read-only role (for example AdministratorAccess). Every call for these targets, including background refresh " +
        "every few minutes, would be signed with a key that can change things.\n\n" +
        "Skypeek still sends only the calls on its read allowlist. Prefer a ReadOnly role when the account has one.",
        "Use it", danger: true);

    /// <summary>Writes editor controls into the working copy of the selected target.</summary>
    private void ApplyEditor()
    {
        EcsOverridePanel.IsEnabled = OverrideEcs.IsChecked == true;
        EbOverridePanel.IsEnabled = OverrideEb.IsChecked == true;
        if (_editing is null)
            return;

        var t = _editing;
        var errors = new List<string>();
        t.Alias = Alias.Text?.Trim() ?? "";
        var region = (Region.Text ?? "").Trim();
        if (region.Length > 0)
            t.Region = region;
        t.Enabled = TargetEnabled.IsChecked == true;

        var option = ElevatedProfile.SelectedItem as ProfileOption;
        var elevated = option?.SameKey == true ? t.ProfileName : option?.Name;
        if (elevated != t.ElevatedProfileName)
        {
            t.ElevatedProfileName = elevated;
            if (ElevatedForAll.IsChecked == true)
                foreach (var other in _targets.Where(o => o.ProfileName == t.ProfileName))
                    other.ElevatedProfileName = elevated;
        }

        t.SecretsEnabled = FeatSecrets.IsChecked == true;
        t.ParamsEnabled = FeatParams.IsChecked == true;
        t.EbEnabled = FeatEb.IsChecked == true;
        t.EcsEnabled = FeatEcs.IsChecked == true;
        t.RdsEnabled = FeatRds.IsChecked == true;
        t.CacheEnabled = FeatCache.IsChecked == true;
        t.Ec2Enabled = FeatEc2.IsChecked == true;
        t.ElbEnabled = FeatElb.IsChecked == true;
        t.NetworkEnabled = FeatNetwork.IsChecked == true;
        if (NetworkInterval.SelectedItem is IntervalOption n) t.NetworkIntervalMinutes = n.Minutes;
        NetworkInterval.IsEnabled = t.NetworkEnabled;

        if (!string.IsNullOrWhiteSpace(CatalogCustom.Text))
        {
            if (int.TryParse(CatalogCustom.Text, out var custom) && custom >= 15)
                t.CatalogIntervalMinutes = custom;
            else
                errors.Add("Custom list refresh must be at least 15 minutes.");
        }
        else if (CatalogInterval.SelectedItem is IntervalOption c)
        {
            t.CatalogIntervalMinutes = c.Minutes;
        }
        if (HealthInterval.SelectedItem is IntervalOption h) t.HealthIntervalMinutes = h.Minutes;
        if (MetricsInterval.SelectedItem is IntervalOption m) t.MetricsIntervalMinutes = m.Minutes;

        t.EcsThresholds = ReadOverride(OverrideEcs, EcsCpuWarn, EcsCpuCrit, EcsMemWarn, EcsMemCrit, "ECS", errors);
        t.EbThresholds = ReadOverride(OverrideEb, EbCpuWarn, EbCpuCrit, EbMemWarn, EbMemCrit, "EB", errors);

        var metricCount = t.Id > 0 ? _session.Health.MetricCount(t.Id) : 0;
        CostEstimate.Text = t.MetricsIntervalMinutes <= 0 || !t.HealthEnabled
            ? "metrics polling off"
            : metricCount == 0
                ? "cost estimate appears after the first health poll (~$0.01 per 1,000 metrics)"
                : $"~{metricCount} metrics per poll ≈ ${HealthRules.EstimateMonthlyMetricsCostUsd(metricCount, t.MetricsIntervalMinutes):0.00}/month (GetMetricData)";

        EditorError.Text = string.Join(" ", errors);
        EditorError.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ThresholdSettings? ReadOverride(CheckBox enabled, TextBox cw, TextBox cc, TextBox mw, TextBox mc, string label, List<string> errors)
    {
        if (enabled.IsChecked != true)
            return null;
        if (ThresholdDialog.TryParseThresholds(cw.Text, cc.Text, mw.Text, mc.Text, out var result, out var error))
            return result;
        errors.Add($"{label} override: {error}");
        return null;
    }

    private static void SetThresholdBoxes(ThresholdSettings t, TextBox cw, TextBox cc, TextBox mw, TextBox mc)
    {
        cw.Text = t.CpuWarn.ToString(CultureInfo.InvariantCulture);
        cc.Text = t.CpuCritical.ToString(CultureInfo.InvariantCulture);
        mw.Text = t.MemWarn.ToString(CultureInfo.InvariantCulture);
        mc.Text = t.MemCritical.ToString(CultureInfo.InvariantCulture);
    }

    // ---------------- alarms ----------------

    private void OnAddSuppression(object sender, RoutedEventArgs e)
    {
        var pattern = NewPattern.Text?.Trim() ?? "";
        if (pattern.Replace("*", "").Replace("?", "").Length == 0)
        {
            SaveStatus.Text = "Enter an alarm name or a pattern with some fixed text (a bare * would hide every alarm).";
            return;
        }
        var scope = (NewPatternScope.SelectedItem as ScopeOption)?.TargetId;
        _addedSuppressions.Add(new AlarmSuppression(pattern, scope, DateTime.UtcNow));
        NewPattern.Text = "";
        RefreshSuppressions();
        SaveStatus.Text = "Suppression added — Save to apply.";
    }

    private void OnRemoveSuppression(object sender, RoutedEventArgs e)
    {
        if (SuppressionList.SelectedItem is not SuppressionRow row)
            return;
        if (!_addedSuppressions.Remove(row.Rule))
            _removedSuppressions.Add(row.Rule);
        RefreshSuppressions();
        SaveStatus.Text = "Suppression removed — Save to apply.";
    }

    private void OnRemoveOverride(object sender, RoutedEventArgs e)
    {
        if (OverrideList.SelectedItem is not OverrideRow row)
            return;
        _removedOverrides.Add(row.Key);
        RefreshOverrides();
        SaveStatus.Text = "Override removed — Save to apply.";
    }

    // ---------------- general ----------------

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            modifiers |= ModifierKeys.Windows;
        if (GlobalHotkey.Format(modifiers, key) is not { } combo)
            return;
        HotkeyBox.Text = combo;
        HotkeyStatus.Text = App.Current.Hotkey.IsAvailable(combo) ? "free" : "taken by another application";
    }

    // ---------------- security ----------------

    private async void OnChangePassword(object sender, RoutedEventArgs e)
    {
        var current = CurrentPassword.Password;
        var next = NewPassword.Password;
        if (next.Length < 8 || next != ConfirmPassword.Password)
        {
            PasswordStatus.Text = next.Length < 8 ? "Use at least 8 characters." : "New passwords do not match.";
            return;
        }
        try
        {
            PasswordStatus.Text = "Re-encrypting vault…";
            await Task.Run(() => _session.Vault.ChangePassword(current, next));
            PasswordStatus.Text = "Password changed.";
            CurrentPassword.Password = NewPassword.Password = ConfirmPassword.Password = "";
        }
        catch (InvalidPasswordException)
        {
            PasswordStatus.Text = "Current password is wrong.";
        }
        catch (Exception ex)
        {
            PasswordStatus.Text = ex.Message;
        }
    }

    // ---------------- save ----------------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ApplyEditor();
        if (EditorError.Visibility == Visibility.Visible)
        {
            SaveStatus.Text = "Fix the highlighted target fields first.";
            return;
        }

        var errors = new List<string>();
        var settings = new AppSettings
        {
            ShowNonReadOnlyProfiles = ShowNonReadOnly.IsChecked == true,
            LockOnWindowsLock = LockOnWindowsLock.IsChecked == true,
            WarningsTurnIconRed = WarningsRed.IsChecked == true,
            IgnoreTargetTrackingAlarms = IgnoreTargetTracking.IsChecked == true,
            LockoutMinutes = ParseInt(LockoutMinutes.Text, 0, 1440, "Lockout minutes", errors),
            RequestLogRetentionDays = ParseInt(RetentionDays.Text, 1, 3650, "Retention days", errors),
            SustainedMinutes = ParseInt(SustainedMinutes.Text, 1, 60, "Sustained minutes", errors),
            AlarmHistoryHours = ParseInt(AlarmHours.Text, 1, 336, "Alarm history hours", errors),
            ResourceThresholds = MergedOverrides(),
            SuppressedAlarms = MergedSuppressions(),
            SuppressedCauses = MergedCauses(),
            // Set from the dashboard, not here.
            EbGroupByApplication = _session.Settings.Settings.EbGroupByApplication,
            Ec2IncludeEbInstances = _session.Settings.Settings.Ec2IncludeEbInstances,
            HiddenResources = _session.Settings.Settings.HiddenResources,
        };
        if (ThresholdDialog.TryParseThresholds(GEcsCpuWarn.Text, GEcsCpuCrit.Text, GEcsMemWarn.Text, GEcsMemCrit.Text, out var ecs, out var ecsError))
            settings.EcsThresholds = ecs;
        else
            errors.Add($"ECS thresholds: {ecsError}");
        if (ThresholdDialog.TryParseThresholds(GEbCpuWarn.Text, GEbCpuCrit.Text, GEbMemWarn.Text, GEbMemCrit.Text, out var eb, out var ebError))
            settings.EbThresholds = eb;
        else
            errors.Add($"EB thresholds: {ebError}");
        if (ThresholdDialog.TryParseThresholds(GRdsCpuWarn.Text, GRdsCpuCrit.Text, GRdsConnWarn.Text, GRdsConnCrit.Text, out var rds, out var rdsError))
            settings.RdsThresholds = rds;
        else
            errors.Add($"RDS thresholds: {rdsError}");
        if (ThresholdDialog.TryParseThresholds(GCacheCpuWarn.Text, GCacheCpuCrit.Text, GCacheMemWarn.Text, GCacheMemCrit.Text, out var cache, out var cacheError))
            settings.CacheThresholds = cache;
        else
            errors.Add($"ElastiCache thresholds: {cacheError}");
        if (ThresholdDialog.TryParseThresholds(GEc2CpuWarn.Text, GEc2CpuCrit.Text, GEc2MemWarn.Text, GEc2MemCrit.Text, out var ec2, out var ec2Error))
            settings.Ec2Thresholds = ec2;
        else
            errors.Add($"EC2 thresholds: {ec2Error}");
        // Same rules as a CPU pair: 1–100, warning not above critical.
        if (ThresholdDialog.TryParseThresholds(GRdsStorageWarn.Text, GRdsStorageCrit.Text, GRdsStorageWarn.Text, GRdsStorageCrit.Text, out var storage, out var storageError))
            (settings.RdsStorageWarn, settings.RdsStorageCritical) = (storage.CpuWarn, storage.CpuCritical);
        else
            errors.Add($"RDS storage thresholds: {storageError}");

        if (errors.Count > 0)
        {
            SaveStatus.Text = string.Join(" ", errors);
            return;
        }

        var messages = new List<string>();

        // Hotkey first: it is the only setting that can fail because of other applications.
        var hotkey = HotkeyBox.Text;
        if (hotkey != _hotkey || App.Current.Hotkey.Current != hotkey)
        {
            if (App.Current.RegisterHotkey(hotkey))
            {
                _session.Vault.SetHotkey(hotkey);
                _hotkey = hotkey;
            }
            else
            {
                App.Current.RegisterHotkey(_hotkey);
                messages.Add($"Hotkey {hotkey} is taken by another application; kept {_hotkey}.");
            }
        }

        try
        {
            AutostartService.SetEnabled(RunOnStartup.IsChecked == true);
        }
        catch (Exception ex)
        {
            messages.Add($"Could not update autostart: {ex.Message}");
        }

        _session.Settings.SaveSettings(settings);
        _removedOverrides.Clear();
        _addedSuppressions.Clear();
        _removedSuppressions.Clear();
        _removedCauses.Clear();

        foreach (var id in _deleted)
            _session.DeleteTarget(id);
        _deleted.Clear();

        var newIds = new List<long>();
        foreach (var target in _targets.ToList())
        {
            var isNew = target.Id <= 0;
            if (isNew)
                target.Id = 0;
            var saved = _session.Settings.SaveTarget(target);
            target.Id = saved.Id;
            if (isNew)
                newIds.Add(saved.Id);
        }

        _session.Health.Reevaluate();
        foreach (var id in newIds)
            _session.Scheduler.RunNow(id);

        RefreshTargetList();
        RefreshOverrides();
        RefreshSuppressions();
        messages.Insert(0, newIds.Count > 0 ? $"Saved. Fetching data for {newIds.Count} new target(s)…" : "Saved.");
        SaveStatus.Text = string.Join(" ", messages);
    }

    private static int ParseInt(string? text, int min, int max, string label, List<string> errors)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
            return value;
        errors.Add($"{label} must be between {min} and {max}.");
        return min;
    }
}
