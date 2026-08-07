using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace SentientDesk.Models;

public sealed class DesktopItem : INotifyPropertyChanged
{
    private string _id = "";
    private string _path = "";
    private string _displayName = "";
    private BitmapSource? _icon;
    private bool _isDirectory;

    public required string Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(); } }
    }

    public required string Path
    {
        get => _path;
        set { if (_path != value) { _path = value; OnPropertyChanged(); } }
    }

    public required string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }

    public BitmapSource? Icon
    {
        get => _icon;
        set { if (!ReferenceEquals(_icon, value)) { _icon = value; OnPropertyChanged(); } }
    }

    public bool IsDirectory
    {
        get => _isDirectory;
        set { if (_isDirectory != value) { _isDirectory = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
