using System.IO;
using System.Text.Json;
using SentientDesk.Models;

namespace SentientDesk.Services;

public sealed class LayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public LayoutStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SentientDesk");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "layout.json");
    }

    public string FilePath => _path;

    public AppLayout Load()
    {
        if (!File.Exists(_path))
            return CreateDefault();

        try
        {
            var json = File.ReadAllText(_path);
            var layout = JsonSerializer.Deserialize<AppLayout>(json, JsonOptions);
            if (layout is null)
                return CreateDefault();

            layout.ItemFenceMap = new Dictionary<string, string>(
                layout.ItemFenceMap ?? [],
                StringComparer.OrdinalIgnoreCase);

            // Empty fences array is a corrupt/torn write from an earlier bug — recover.
            if (layout.Fences is null || layout.Fences.Count == 0)
                return CreateDefault();

            return layout;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save(AppLayout layout)
    {
        // Refuse to wipe a good layout with an empty fence list.
        if ((layout.Fences is null || layout.Fences.Count == 0) && File.Exists(_path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<AppLayout>(File.ReadAllText(_path), JsonOptions);
                if (existing?.Fences is { Count: > 0 })
                    return;
            }
            catch
            {
                // Fall through and write if we can't read the old file.
            }
        }

        var json = JsonSerializer.Serialize(layout, JsonOptions);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);

        // Atomic replace where possible.
        if (File.Exists(_path))
            File.Replace(temp, _path, null);
        else
            File.Move(temp, _path);
    }

    private static AppLayout CreateDefault()
    {
        return new AppLayout
        {
            Fences =
            [
                new FenceData
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = "Desktop",
                    Left = 48,
                    Top = 48,
                    Width = 240,
                    Height = 200,
                    Color = "#66202838"
                }
            ]
        };
    }
}
