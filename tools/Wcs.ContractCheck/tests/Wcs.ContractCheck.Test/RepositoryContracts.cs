using System.Text.Json.Nodes;
using Xunit;

namespace Wcs.ContractCheck.Test;

/// <summary>The committed contracts of this repository: <c>guide/contracts</c>, or <c>docs/contracts</c> in the public copy.</summary>
internal static class RepositoryContracts
{
    public static string Folder
    {
        get
        {
            var folder = ContractFiles.FindFolder(AppContext.BaseDirectory);
            Assert.NotNull(folder);
            Assert.True(Directory.Exists(folder), $"{folder} does not exist");
            return folder;
        }
    }

    public static JsonObject Read(string name) =>
        ContractFiles.Read(Path.Combine(Folder, name)) ?? throw new InvalidOperationException($"{name} is missing");
}

/// <summary>
/// The tests that start the host in memory share the engine's static state (<c>Navigator</c>): they run one
/// after the other.
/// </summary>
[CollectionDefinition("host")]
public sealed class HostCollection;
