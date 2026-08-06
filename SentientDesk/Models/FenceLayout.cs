namespace SentientDesk.Models;

public sealed class AppLayout
{
    public int Version { get; set; } = 1;
    public List<FenceData> Fences { get; set; } = [];
    public Dictionary<string, string> ItemFenceMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HideDesktopIcons { get; set; } = true;
    public bool StartWithWindows { get; set; }
}

public sealed class FenceData
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 280;
    public bool IsCollapsed { get; set; }
    public string Color { get; set; } = "#66202838";
    public List<string> ItemIds { get; set; } = [];
}
