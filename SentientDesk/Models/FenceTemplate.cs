namespace SentientDesk.Models;

/// <summary>
/// Built-in fence size/title presets for the tray New fence menu.
/// </summary>
public sealed record FenceTemplate(string Id, string MenuLabel, double Width, double Height, string? Title)
{
    public static FenceTemplate Blank { get; } = new("blank", "Blank", 240, 200, Title: null);
    public static FenceTemplate Narrow { get; } = new("narrow", "Narrow column", 160, 368, "Narrow");
    public static FenceTemplate Shelf { get; } = new("shelf", "Wide shelf", 480, 160, "Shelf");
    public static FenceTemplate Large { get; } = new("large", "Large", 400, 320, "Large");

    public static IReadOnlyList<FenceTemplate> All { get; } =
    [
        Blank,
        Narrow,
        Shelf,
        Large
    ];
}
