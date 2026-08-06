using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SentientDesk.Models;

namespace SentientDesk.ViewModels;

public sealed class FenceViewModel : INotifyPropertyChanged
{
    private string _title;
    private double _left;
    private double _top;
    private double _width;
    private double _height;
    private bool _isCollapsed;
    private Brush _background;

    public FenceViewModel(FenceData data)
    {
        Id = data.Id;
        _title = data.Title;
        _left = data.Left;
        _top = data.Top;
        _width = Math.Max(160, data.Width);
        _height = Math.Max(100, data.Height);
        _isCollapsed = data.IsCollapsed;
        _background = ParseBrush(data.Color);
        ColorHex = data.Color;
    }

    public string Id { get; }
    public ObservableCollection<DesktopItem> Items { get; } = [];

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    public double Left
    {
        get => _left;
        set { if (Math.Abs(_left - value) > 0.1) { _left = value; OnPropertyChanged(); } }
    }

    public double Top
    {
        get => _top;
        set { if (Math.Abs(_top - value) > 0.1) { _top = value; OnPropertyChanged(); } }
    }

    public double Width
    {
        get => _width;
        set { if (Math.Abs(_width - value) > 0.1) { _width = value; OnPropertyChanged(); } }
    }

    public double Height
    {
        get => _height;
        set { if (Math.Abs(_height - value) > 0.1) { _height = value; OnPropertyChanged(); } }
    }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set { if (_isCollapsed != value) { _isCollapsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ContentHeight)); } }
    }

    public double ContentHeight => IsCollapsed ? 0 : Height - 36;

    public Brush Background
    {
        get => _background;
        set { _background = value; OnPropertyChanged(); }
    }

    public string ColorHex { get; set; }

    public FenceData ToData() => new()
    {
        Id = Id,
        Title = Title,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = IsCollapsed ? Math.Max(Height, 120) : Height,
        IsCollapsed = IsCollapsed,
        Color = ColorHex,
        ItemIds = Items.Select(i => i.Id).ToList()
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static Brush ParseBrush(string hex)
    {
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(hex)!;
        }
        catch
        {
            return new SolidColorBrush(Color.FromArgb(0x66, 0x20, 0x28, 0x38));
        }
    }
}
