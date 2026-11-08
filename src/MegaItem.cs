using CG.Web.MegaApiClient;

namespace MegaSchematicsDownloader;

/// <summary>A MEGA file or folder plus the paths and totals the index computes for it.</summary>
public sealed class MegaItem
{
    public MegaItem(INode node)
    {
        Node = node;
        Id = node.Id;
        ParentId = node.ParentId;
        Name = NormalizeName(node.Name);
        IsFile = node.Type == NodeType.File;
        IsRoot = node.Type == NodeType.Root;
        Size = IsFile ? Math.Max(0L, node.Size) : 0L;
        Init();
    }

    /// <summary>Rebuilds an item from the saved index. Node stays null until the live listing is attached.</summary>
    public MegaItem(string id, string? parentId, string name, long size, int kind)
    {
        Id = id;
        ParentId = parentId;
        Name = NormalizeName(name);
        IsFile = kind == 0;
        IsRoot = kind == 2;
        Size = IsFile ? Math.Max(0L, size) : 0L;
        Init();
    }

    /// <summary>The live MEGA node, needed for downloading. Null while only the saved index is loaded.</summary>
    public INode? Node { get; internal set; }

    public string Id { get; }
    public string? ParentId { get; }
    public string Name { get; }
    public bool IsFile { get; }
    public bool IsRoot { get; }
    public long Size { get; }

    /// <summary>0 = file, 1 = folder, 2 = root. Used by the saved index.</summary>
    public int Kind => IsFile ? 0 : (IsRoot ? 2 : 1);

    /// <summary>On-disk path below the download folder (sanitized, unique). Empty for the root.</summary>
    public string RelativePath { get; internal set; } = string.Empty;

    /// <summary>Human readable path with original names, for example "Lenovo / IdeaPad".</summary>
    public string DisplayPath { get; internal set; } = string.Empty;

    /// <summary>Human readable path of the containing folder.</summary>
    public string DisplayDirectory { get; internal set; } = string.Empty;

    /// <summary>Files contained (1 for a file, recursive count for a folder).</summary>
    public int FileCount { get; internal set; }

    /// <summary>Bytes contained (file size for a file, recursive total for a folder).</summary>
    public long TotalBytes { get; internal set; }

    public static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;

    private void Init()
    {
        if (IsFile)
        {
            FileCount = 1;
            TotalBytes = Size;
        }
    }
}
