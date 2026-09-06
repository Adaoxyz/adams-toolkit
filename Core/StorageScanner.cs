using System.IO;

namespace AdamsToolkit.Core;

public class StorageEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public bool IsDir { get; set; }
    public int Files { get; set; }
    public bool Denied { get; set; }
}

/// <summary>
/// Explorador de espaço em disco (estilo TreeSize lite): tamanho de cada subpasta/ficheiro
/// de uma pasta, em paralelo, sem seguir reparse points (junctions/symlinks — contar
/// duas vezes ou loops). Sem admin: pastas sem permissão contam o que der e ficam marcadas.
/// </summary>
public static class StorageScanner
{
    public static async Task<List<StorageEntry>> ScanAsync(string root, CancellationToken ct, IProgress<int>? progress = null)
    {
        var list = new List<StorageEntry>();
        DirectoryInfo di;
        try { di = new DirectoryInfo(root); if (!di.Exists) return list; } catch { return list; }

        FileSystemInfo[] children;
        try { children = di.GetFileSystemInfos(); } catch { return list; }

        var entries = new StorageEntry[children.Length];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, children.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount), CancellationToken = ct },
            (i, token) =>
            {
                var c = children[i];
                var e = new StorageEntry { Path = c.FullName, Name = c.Name };
                if (c is DirectoryInfo d)
                {
                    e.IsDir = true;
                    if (!c.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        var (bytes, files, denied) = Size(d, token);
                        e.Bytes = bytes; e.Files = files; e.Denied = denied;
                    }
                }
                else if (c is FileInfo f)
                {
                    try { e.Bytes = f.Length; e.Files = 1; } catch { }
                }
                entries[i] = e;
                progress?.Report(Interlocked.Increment(ref done) * 100 / Math.Max(1, children.Length));
                return ValueTask.CompletedTask;
            });

        list.AddRange(entries.Where(e => e != null));
        list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return list;
    }

    private static (long bytes, int files, bool denied) Size(DirectoryInfo d, CancellationToken ct)
    {
        long bytes = 0; int files = 0; bool denied = false;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(d);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) break;
            var cur = stack.Pop();
            try
            {
                foreach (var fi in cur.EnumerateFiles())
                {
                    try { bytes += fi.Length; files++; } catch { }
                }
                foreach (var sub in cur.EnumerateDirectories())
                {
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    stack.Push(sub);
                }
            }
            catch (UnauthorizedAccessException) { denied = true; }
            catch { }
        }
        return (bytes, files, denied);
    }

    public static List<(string root, string label, long total, long free)> Drives()
    {
        var r = new List<(string, string, long, long)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Network) continue;
                var name = d.VolumeLabel.Length > 0 ? d.VolumeLabel : "Disco";
                r.Add((d.RootDirectory.FullName, $"{name} ({d.Name.TrimEnd('\\')})", d.TotalSize, d.AvailableFreeSpace));
            }
            catch { }
        }
        return r;
    }
}
