using System.IO;
using System.Text.Json;

namespace LinkCloudPlayer.Services;

public class WatchProgress
{
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Worth offering "Resume": past the first 30s and not basically finished.</summary>
    public bool IsResumable => PositionMs >= 30_000 && (DurationMs <= 0 || PositionMs < DurationMs * 0.95);

    public double Fraction => DurationMs > 0 ? Math.Clamp((double)PositionMs / DurationMs, 0, 1) : 0;
}

/// <summary>
/// Remembers how far into each file the user got, in a small JSON file under
/// %LocalAppData%. Keyed by server file id.
/// </summary>
public class WatchProgressStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LinkCloudPlayer",
        "progress.json");

    private readonly Dictionary<int, WatchProgress> _items;

    public WatchProgressStore()
    {
        _items = Load();
    }

    public WatchProgress? Get(int fileId) => _items.GetValueOrDefault(fileId);

    public void Update(int fileId, long positionMs, long durationMs)
    {
        if (durationMs > 0 && positionMs >= durationMs * 0.95)
        {
            // Effectively finished - drop it from Continue Watching.
            Remove(fileId);
            return;
        }
        _items[fileId] = new WatchProgress { PositionMs = positionMs, DurationMs = durationMs, UpdatedAt = DateTime.UtcNow };
        Save();
    }

    public void Remove(int fileId)
    {
        if (_items.Remove(fileId)) Save();
    }

    private static Dictionary<int, WatchProgress> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<int, WatchProgress>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex)
        {
            App.LogToFile("WatchProgressStore.Load", ex);
        }
        return [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.LogToFile("WatchProgressStore.Save", ex);
        }
    }
}
