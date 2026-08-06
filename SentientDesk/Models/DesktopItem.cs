using System.Windows.Media.Imaging;

namespace SentientDesk.Models;

public sealed class DesktopItem
{
    public required string Id { get; init; }
    public required string Path { get; set; }
    public required string DisplayName { get; set; }
    public BitmapSource? Icon { get; set; }
    public bool IsDirectory { get; set; }
}
