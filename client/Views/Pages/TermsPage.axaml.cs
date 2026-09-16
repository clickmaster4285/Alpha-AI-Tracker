using Avalonia.Controls;

namespace client.Views.Pages;

/// <summary>
/// Page 7 — the locked fullscreen Terms &amp; Conditions acceptance gate.
/// Code-behind is intentionally empty; all behavior lives in TermsViewModel
/// and the shell-level enforcement in MainWindow.axaml.cs / App.axaml.cs.
/// </summary>
public partial class TermsPage : UserControl
{
    public TermsPage()
    {
        InitializeComponent();
    }
}
