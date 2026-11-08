using CG.Web.MegaApiClient;
using System.IO;

namespace MegaSchematicsDownloader;

/// <summary>
/// Indexes the MEGA listing once: children lists, unique on-disk paths, folder totals and search.
/// All traversals are iterative, so a very deep or malformed listing cannot overflow the stack.
/// </summary>
public sealed class DatabaseIndex
{
    private readonly Dictionary<string, MegaItem> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MegaItem>> _children = new(StringComparer.Ordinal);
    private readonly List<MegaItem> _files = new();

    public DatabaseIndex(IEnumerable<INode> nodes)
        : this(nodes.Where(n => n is not null && !string.IsNullOrEmpty(n.Id)).Select(n => new MegaItem(n)))
    {
    }

    public DatabaseIndex(IEnumerable<MegaItem> source)
    {
        var items = new List<MegaItem>();
        foreach (var entry in source)
        {
            if (string.IsNullOrEmpty(entry.Id)) continue;
            if (_byId.TryAdd(entry.Id, entry)) items.Add(entry);
        }

        Root = items.FirstOrDefault(x => x.IsRoot)
            ?? items.FirstOrDefault(x => !x.IsFile &&
                   (string.IsNullOrEmpty(x.ParentId) || !_byId.ContainsKey(x.ParentId)))
            ?? throw new InvalidDataException("The MEGA folder listing has no root folder.");

        foreach (var item in items)
        {
            if (ReferenceEquals(item, Root) || string.IsNullOrEmpty(item.ParentId)) continue;
            if (!_children.TryGetValue(item.ParentId, out var siblings))
            {
                siblings = new List<MegaItem>();
                _children[item.ParentId] = siblings;
            }
            siblings.Add(item);
        }
        foreach (var siblings in _children.Values) siblings.Sort(CompareDefault);

        // Walk from the root, folders first and sorted, assigning unique on-disk paths.
        var order = new List<MegaItem>(items.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal) { Root.Id };
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<MegaItem>();
        stack.Push(Root);

        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            order.Add(folder);
            if (!_children.TryGetValue(folder.Id, out var kids)) continue;

            var subFolders = new List<MegaItem>();
            foreach (var kid in kids)
            {
                if (!visited.Add(kid.Id)) continue;
                AssignPaths(folder, kid, usedPaths);
                if (kid.IsFile) order.Add(kid);
                else subFolders.Add(kid);
            }
            for (var idx = subFolders.Count - 1; idx >= 0; idx--) stack.Push(subFolders[idx]);
        }

        // Children always appear after their parent in 'order', so a reverse pass rolls totals upward.
        for (var idx = order.Count - 1; idx > 0; idx--)
        {
            var current = order[idx];
            if (_byId.TryGetValue(current.ParentId ?? string.Empty, out var parent))
            {
                parent.FileCount += current.FileCount;
                parent.TotalBytes += current.TotalBytes;
            }
        }

        foreach (var ordered in order)
            if (ordered.IsFile) _files.Add(ordered);

        FileCount = Root.FileCount;
        TotalBytes = Root.TotalBytes;
        DirectoryCount = Math.Max(0, order.Count - _files.Count - 1);
    }

    public MegaItem Root { get; }
    public int FileCount { get; }
    public int DirectoryCount { get; }
    public long TotalBytes { get; }

    public bool IsRoot(MegaItem item) => ReferenceEquals(item, Root);

    public MegaItem? GetById(string id) => _byId.TryGetValue(id, out var item) ? item : null;

    /// <summary>
    /// Connects live MEGA nodes (needed for downloading) to this index when the listing is unchanged.
    /// Returns false, changing nothing, if anything differs so the caller can rebuild instead.
    /// </summary>
    public bool TryAttach(IReadOnlyCollection<INode> nodes)
    {
        if (nodes.Count != _byId.Count) return false;

        foreach (var node in nodes)
        {
            if (node is null || string.IsNullOrEmpty(node.Id) || !_byId.TryGetValue(node.Id, out var known))
                return false;
            var isFile = node.Type == NodeType.File;
            if (known.IsFile != isFile || known.Name != MegaItem.NormalizeName(node.Name)) return false;
            if (isFile && known.Size != Math.Max(0L, node.Size)) return false;
            if (!string.Equals(known.ParentId ?? string.Empty, node.ParentId ?? string.Empty, StringComparison.Ordinal))
                return false;
        }

        foreach (var node in nodes) _byId[node.Id].Node = node;
        return true;
    }

    public List<CachedNode> ToCache() =>
        _byId.Values.Select(x => new CachedNode(x.Id, x.ParentId, x.Name, x.Size, x.Kind)).ToList();

    public IReadOnlyList<MegaItem> GetChildren(MegaItem folder) =>
        _children.TryGetValue(folder.Id, out var kids) ? kids : Array.Empty<MegaItem>();

    public MegaItem? GetParent(MegaItem item) =>
        !IsRoot(item) && !string.IsNullOrEmpty(item.ParentId) && _byId.TryGetValue(item.ParentId, out var parent)
            ? parent
            : null;

    /// <summary>All files at or below the item (the item itself when it is a file), in folder order.</summary>
    public List<MegaItem> GetFilesUnder(MegaItem start)
    {
        var result = new List<MegaItem>();
        if (start.IsFile)
        {
            result.Add(start);
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { start.Id };
        var stack = new Stack<MegaItem>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            var subFolders = new List<MegaItem>();
            foreach (var kid in GetChildren(folder))
            {
                if (!seen.Add(kid.Id)) continue;
                if (kid.IsFile) result.Add(kid);
                else subFolders.Add(kid);
            }
            for (var idx = subFolders.Count - 1; idx >= 0; idx--) stack.Push(subFolders[idx]);
        }
        return result;
    }

    /// <summary>
    /// Every word must appear in the file's full path (folders plus name), case-insensitive.
    /// Optionally limited to one folder and everything below it.
    /// </summary>
    public List<MegaItem> Search(string query, MegaItem? scope = null)
    {
        var results = new List<MegaItem>();
        var terms = (query ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return results;

        IEnumerable<MegaItem> candidates = scope is null || IsRoot(scope) ? _files : GetFilesUnder(scope);
        foreach (var file in candidates)
        {
            var haystack = file.DisplayPath;
            var matchesAll = true;
            foreach (var term in terms)
            {
                if (haystack.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    matchesAll = false;
                    break;
                }
            }
            if (matchesAll) results.Add(file);
        }
        return results;
    }

    private static void AssignPaths(MegaItem parent, MegaItem kid, HashSet<string> usedPaths)
    {
        var segment = PathHelper.SanitizeSegment(kid.Name);
        var relative = parent.RelativePath.Length == 0
            ? segment
            : parent.RelativePath + Path.DirectorySeparatorChar + segment;

        if (kid.IsFile)
        {
            // Two different names can sanitize to the same path; keep both by numbering the later one.
            var candidate = relative;
            var counter = 2;
            while (!usedPaths.Add(candidate)) candidate = WithSuffix(relative, counter++);
            relative = candidate;
        }
        else
        {
            usedPaths.Add(relative);
        }

        kid.RelativePath = relative;
        kid.DisplayDirectory = parent.DisplayPath;
        kid.DisplayPath = parent.DisplayPath.Length == 0 ? kid.Name : parent.DisplayPath + " / " + kid.Name;
    }

    private static string WithSuffix(string relative, int counter)
    {
        var directory = Path.GetDirectoryName(relative);
        var name = Path.GetFileNameWithoutExtension(relative);
        var extension = Path.GetExtension(relative);
        var numbered = $"{name} ({counter}){extension}";
        return string.IsNullOrEmpty(directory) ? numbered : Path.Combine(directory, numbered);
    }

    private static int CompareDefault(MegaItem x, MegaItem y)
    {
        if (x.IsFile != y.IsFile) return x.IsFile ? 1 : -1;
        var byName = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
        return byName != 0 ? byName : string.CompareOrdinal(x.Id, y.Id);
    }
}
