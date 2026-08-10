using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.ViewModels;

/// <summary>
/// One row of the inventory table. Applications and packages have different shapes
/// on disk but the same shape on screen, so both are projected onto this so a single
/// virtualised template can render either tab.
/// </summary>
public sealed class InventoryRow
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    /// <summary>Install path (apps) or source manager + path (packages).</summary>
    public string Detail { get; init; } = string.Empty;
    /// <summary>Short tag shown in the trailing chip — "apt", "winget", "GUI"…</summary>
    public string Source { get; init; } = string.Empty;
    public string Initials { get; init; } = "?";
    public string AccentColor { get; init; } = "#2563EB";
    /// <summary>Lowercased haystack precomputed once so filtering stays O(n) with no allocations.</summary>
    public string SearchKey { get; init; } = string.Empty;
}

/// <summary>
/// Page 6 — Installed Applications. Renders the EXACT stored inventory from SQLite
/// (installed_applications / installed_packages), so the page always matches the DB
/// and what gets synced upstream. The collector refreshes these tables on its periodic
/// inventory scan; this page never scans the OS itself.
/// </summary>
public partial class InstalledAppsViewModel : ViewModelBase
{
    private readonly ILogStore _store;

    private IReadOnlyList<InventoryRow> _allApps = Array.Empty<InventoryRow>();
    private IReadOnlyList<InventoryRow> _allPackages = Array.Empty<InventoryRow>();

    // Deterministic accent palette — index by name hash so a given app keeps its colour
    // between refreshes instead of flickering.
    private static readonly string[] Accents =
    {
        "#2563EB", "#0EA5E9", "#8B5CF6", "#10B981", "#F59E0B", "#EC4899", "#14B8A6", "#6366F1"
    };

    public InstalledAppsViewModel(ILogStore store)
    {
        _store = store;
    }

    public ObservableCollection<InventoryRow> Items { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _lastRefreshed = string.Empty;
    [ObservableProperty] private int _appCount;
    [ObservableProperty] private int _packageCount;

    [NotifyPropertyChangedFor(nameof(IsAppsTab))]
    [NotifyPropertyChangedFor(nameof(IsPackagesTab))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    [ObservableProperty] private bool _showPackages;

    [ObservableProperty] private string _searchText = string.Empty;

    [NotifyPropertyChangedFor(nameof(HasResults))]
    [ObservableProperty] private int _visibleCount;

    public bool IsAppsTab => !ShowPackages;
    public bool IsPackagesTab => ShowPackages;
    public bool HasResults => VisibleCount > 0;

    public string EmptyMessage => ShowPackages
        ? "No packages detected. Package managers may not be installed, or the scan has not run yet."
        : "No applications detected. Grant the inventory permission and refresh.";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowPackagesChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private void ShowApps() => ShowPackages = false;

    [RelayCommand]
    private void ShowPackageList() => ShowPackages = true;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // Read the stored inventory straight from SQLite — the same tables the
            // collector writes on its periodic scan and the same rows that get synced
            // upstream. Local reads are fast, so no Task.Run offload is needed.
            var apps = await _store.GetAllInstalledAppsAsync(ct);
            var packages = await _store.GetAllInstalledPackagesAsync(ct);

            _allApps = apps
                .Where(a => !string.IsNullOrWhiteSpace(a.AppName))
                .OrderBy(a => a.AppName, StringComparer.OrdinalIgnoreCase)
                .Select(ToRow)
                .ToList();

            _allPackages = packages
                .Where(p => !string.IsNullOrWhiteSpace(p.PackageName))
                .OrderBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase)
                .Select(ToRow)
                .ToList();

            AppCount = _allApps.Count;
            PackageCount = _allPackages.Count;
            ApplyFilter();

            LastRefreshed = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-scan.
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var source = ShowPackages ? _allPackages : _allApps;
        var needle = SearchText?.Trim().ToLowerInvariant() ?? string.Empty;

        Items.Clear();
        foreach (var row in source)
        {
            if (needle.Length > 0 && !row.SearchKey.Contains(needle, StringComparison.Ordinal)) continue;
            Items.Add(row);
        }
        VisibleCount = Items.Count;
    }

    private static InventoryRow ToRow(InstalledApplication a)
    {
        var detail = string.IsNullOrWhiteSpace(a.InstallPath) ? "—" : a.InstallPath;
        return new InventoryRow
        {
            Name = a.AppName,
            Version = DashboardViewModel.Or(a.AppVersion),
            Publisher = DashboardViewModel.Or(a.Publisher),
            Detail = detail,
            Source = a.IsBrowser ? "browser" : "app",
            Initials = InitialsOf(a.AppName),
            AccentColor = AccentFor(a.AppName),
            SearchKey = $"{a.AppName} {a.BinaryName} {a.Publisher} {a.AppVersion}".ToLowerInvariant()
        };
    }

    private static InventoryRow ToRow(InstalledPackage p)
    {
        var detail = string.IsNullOrWhiteSpace(p.Description)
            ? (string.IsNullOrWhiteSpace(p.InstallPath) ? "—" : p.InstallPath)
            : p.Description;
        return new InventoryRow
        {
            Name = p.PackageName,
            Version = DashboardViewModel.Or(p.Version),
            Publisher = DashboardViewModel.Or(p.Publisher),
            Detail = detail,
            Source = string.IsNullOrWhiteSpace(p.SourceManager) ? p.Category : p.SourceManager,
            Initials = InitialsOf(p.PackageName),
            AccentColor = AccentFor(p.PackageName),
            SearchKey = $"{p.PackageName} {p.Publisher} {p.SourceManager} {p.Category} {p.Version}".ToLowerInvariant()
        };
    }

    private static string InitialsOf(string name)
    {
        var parts = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
    }

    private static string AccentFor(string name)
    {
        var hash = 0;
        foreach (var c in name) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return Accents[hash % Accents.Length];
    }
}
