using CommunityToolkit.Mvvm.ComponentModel;

namespace client.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Greeting { get; set; } = "Welcome to Avalonia v1.0.0.4.1!";
}
