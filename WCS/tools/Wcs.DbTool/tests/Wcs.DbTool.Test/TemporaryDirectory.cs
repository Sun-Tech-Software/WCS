namespace Wcs.DbTool.Test;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
    }

    public string Path { get; }

    public string[] Snapshot() => Directory.GetFileSystemEntries(Path, "*", SearchOption.AllDirectories)
        .OrderBy(entry => entry, StringComparer.Ordinal)
        .Select(entry => Directory.Exists(entry)
            ? "directory:" + entry
            : "file:" + entry + "=" + Convert.ToBase64String(File.ReadAllBytes(entry)))
        .ToArray();

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
