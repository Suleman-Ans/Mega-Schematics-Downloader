using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MegaSchematicsDownloader;

public sealed record CachedNode(string Id, string? ParentId, string Name, long Size, int Kind);

public sealed class CachedIndex
{
    public List<MegaItem> Items { get; init; } = new();
    public DateTime SavedUtc { get; init; }
}

internal sealed class CacheFile
{
    public int Version { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime SavedUtc { get; set; }
    public List<CachedNode> Nodes { get; set; } = new();
}

/// <summary>
/// Saves the file listing (names, sizes, structure) so the next start shows it instantly while the
/// live listing loads in the background. Any problem simply means "no cache".
/// </summary>
public static class IndexCache
{
    private const int CurrentVersion = 1;

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MegaSchematicsDownloader", "index-cache.json.gz");

    public static CachedIndex? Load(string source)
    {
        try
        {
            var path = CachePath;
            if (!File.Exists(path)) return null;

            CacheFile? data;
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                data = JsonSerializer.Deserialize<CacheFile>(gzip);
            }

            if (data is null || data.Version != CurrentVersion || data.Source != source || data.Nodes.Count == 0)
                return null;

            return new CachedIndex
            {
                SavedUtc = data.SavedUtc,
                Items = data.Nodes
                    .Where(n => !string.IsNullOrEmpty(n.Id))
                    .Select(n => new MegaItem(n.Id, n.ParentId, n.Name, n.Size, n.Kind))
                    .ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    public static void Save(IEnumerable<CachedNode> nodes, string source)
    {
        var path = CachePath;
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = new CacheFile
            {
                Version = CurrentVersion,
                Source = source,
                SavedUtc = DateTime.UtcNow,
                Nodes = nodes.ToList()
            };

            using (var file = File.Create(temporary))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gzip, data);
            }
            File.Move(temporary, path, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* The cache is optional. */ }
        }
    }
}
