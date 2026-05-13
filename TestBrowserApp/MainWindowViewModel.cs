using ReactiveUI;
using System.Windows.Input;

namespace TestBrowserApp;

public class MainWindowViewModel : ReactiveObject
{
    /// <summary>Programmatic navigation. Set this property to trigger browser navigation via Source binding.</summary>
    private string? _address;
    public string? Address
    {
        get => _address;
        set => this.RaiseAndSetIfChanged(ref _address, value);
    }

    private string _title = "TestBrowserApp";
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>Wired by code-behind with access to TextBox + BrowserView.</summary>
    public ICommand? GoCommand { get; set; }
    public ICommand? ReloadCommand { get; set; }
}
