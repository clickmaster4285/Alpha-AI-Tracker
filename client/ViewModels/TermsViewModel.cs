using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Core.Models;
using client.Services;

namespace client.ViewModels;

/// <summary>
/// ViewModel for the locked fullscreen Terms &amp; Conditions acceptance page (page 7 —
/// the gated-page pattern from client/UI_ARCHITECTURE.md §7).
///
/// Sequential flow: terms are presented ONE at a time in server order
/// (sort_order ASC, created_at ASC), each with an "I agree" action; a progress
/// indicator shows position; the final acceptance releases the gate (Done event).
///
/// Acceptance is only acknowledged locally after the server returns 2xx — on failure
/// the term stays pending with an honest error notice and can be retried.
/// HTML bodies are stripped to plain text for display (no HTML renderer dependency);
/// the raw HTML stays in SQLite.
/// </summary>
public partial class TermsViewModel : ViewModelBase
{
    private readonly TermsService _terms;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private ClientTerm? _currentTerm;

    [ObservableProperty]
    private string _currentBodyText = string.Empty;

    [ObservableProperty]
    private int _currentIndex;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _showOfflineNotice;

    /// <summary>Raised after the LAST pending term is accepted — the router releases the gate.</summary>
    public event Action? Done;

    public TermsViewModel(TermsService terms)
    {
        _terms = terms;
    }

    /// <summary>Progress 0–100 across the pending queue.</summary>
    public double ProgressPercent => TotalCount == 0
        ? 100
        : Math.Clamp(CurrentIndex * 100.0 / TotalCount, 0, 100);

    /// <summary>"Term 2 of 4" position label.</summary>
    public string PositionLabel => TotalCount == 0
        ? string.Empty
        : $"Term {Math.Min(CurrentIndex + 1, TotalCount)} of {TotalCount}";

    public bool HasError => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Load the pending queue and present the first term.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        StatusMessage = string.Empty;
        try
        {
            var pending = await _terms.GetPendingTermsAsync(ct);
            TotalCount = pending.Count;
            CurrentIndex = 0;
            ShowOfflineNotice = _terms.LastRefreshWasOffline && pending.Count > 0;
            PresentCurrent(pending);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async void PresentCurrent(IReadOnlyList<ClientTerm> pending)
    {
        if (CurrentIndex >= TotalCount)
        {
            CurrentTerm = null;
            CurrentBodyText = string.Empty;
            Done?.Invoke();
            return;
        }

        CurrentTerm = pending[CurrentIndex];
        CurrentBodyText = HtmlToPlainText(CurrentTerm.Body);
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(PositionLabel));

        // Refresh the in-memory queue in the background so mid-session arrivals
        // (admin adds a term) join the flow after the current one.
        await Task.Yield();
    }

    [RelayCommand]
    private async Task AgreeAsync(CancellationToken ct)
    {
        if (CurrentTerm is null) return;

        IsLoading = true;
        StatusMessage = string.Empty;
        try
        {
            // Server-acknowledged acceptance only — on failure the term stays
            // pending, the notice shows, and the user can retry.
            await _terms.AcceptAsync(CurrentTerm, ct);
            CurrentIndex++;
            await LoadRemainingAsync(ct);
        }
        catch (Exception)
        {
            StatusMessage = "Could not record your acceptance. " +
                "Check your connection and try again — nothing was saved.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>After each acceptance, re-read the pending queue (indices shift when a
    /// row is marked accepted) and present the next remaining term.</summary>
    private async Task LoadRemainingAsync(CancellationToken ct)
    {
        var pending = await _terms.GetPendingTermsAsync(ct);
        TotalCount = Math.Max(TotalCount, CurrentIndex + pending.Count);
        ShowOfflineNotice = _terms.LastRefreshWasOffline && pending.Count > 0;
        PresentCurrent(pending);
    }

    /// <summary>
    /// Minimal HTML → plain-text for the acceptance display: block tags become
    /// line breaks, remaining tags are stripped, common entities decoded.
    /// The raw HTML stays untouched in SQLite — this is display-only.
    /// </summary>
    internal static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var text = html
            .Replace("<h1>", "\n\n").Replace("</h1>", "\n\n")
            .Replace("<h2>", "\n\n").Replace("</h2>", "\n\n")
            .Replace("<h3>", "\n").Replace("</h3>", "\n")
            .Replace("<h4>", "\n").Replace("</h4>", "\n")
            .Replace("<p>", string.Empty).Replace("</p>", "\n")
            .Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n")
            .Replace("<li>", "• ").Replace("</li>", "\n")
            .Replace("<ul>", "\n").Replace("</ul>", "\n")
            .Replace("<strong>", string.Empty).Replace("</strong>", string.Empty)
            .Replace("<b>", string.Empty).Replace("</b>", string.Empty)
            .Replace("<em>", string.Empty).Replace("</em>", string.Empty)
            .Replace("<i>", string.Empty).Replace("</i>", string.Empty);

        // Strip any remaining tags (<a href=…>, spans, …).
        var sb = new System.Text.StringBuilder(text.Length);
        var inTag = false;
        foreach (var ch in text)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        text = sb.ToString();

        // Decode the common entities without pulling in a HTML parser.
        text = text
            .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
            .Replace("&nbsp;", " ");

        // Collapse 3+ blank lines to one blank line.
        var lines = text.Split('\n');
        var outLines = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 && outLines.Count > 0 && outLines[^1].Length == 0) continue;
            outLines.Add(line);
        }
        return string.Join('\n', outLines).Trim();
    }
}
