using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Wcs.Connection.Interface;
using Wcs.Host.Engine;
using Xunit;

// This test assembly plays a build that names a default driver (see the two ResolveTypeName tests).
[assembly: AssemblyMetadata(ConnectionFactoryLoader.DefaultMetadataKey, "Acme.Plc.Factory, Acme.Plc")]

namespace Wcs.Host.Test;

/// <summary>
/// The loader on its own: a driver dropped in a folder the runtime does not know (as next to
/// <c>Wcs.Host.exe</c>, outside <c>Wcs.Host.deps.json</c>), the values that turn the driver off,
/// and a file that is not a .NET assembly.
/// </summary>
/// <remarks>
/// The driver and its dependency are compiled here, into a folder of their own, with names that
/// are new on every run: an assembly the test project references would already be known to the
/// runtime, and the folder lookup would never be exercised.
/// </remarks>
public sealed class ConnectionFactoryLoaderTest : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wcs-driver-" + Guid.NewGuid().ToString("N"));

    public ConnectionFactoryLoaderTest() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        // The loaded assemblies keep their files open: what cannot be deleted stays in the temp folder.
        try { Directory.Delete(_folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A driver in the folder that uses a library of its own, also in the folder: both load, and
    /// the library is found when the driver first uses it (here in <c>Create</c>, after the host
    /// has started), not only when the driver assembly itself is loaded.
    /// </summary>
    [Fact]
    public void ADriverInTheFolderFindsItsOwnDependencies()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var dependency = Compile("DriverDependency" + suffix,
            "namespace Acme.Plc; public static class Library { public static string Label => \"from the dependency\"; }");
        Compile("Driver" + suffix,
            """
            using Wcs.Connection.Interface;
            namespace Acme.Plc;
            public sealed class Factory : IConnectionFactory
            {
                public string? Seen { get; private set; }
                public void Attach(IConnectionServices services) { }
                public IConnection? Create(ConnectionSettings settings) { Seen = Library.Label; return null; }
            }
            """, dependency);

        var factory = ConnectionFactoryLoader.Create($"Acme.Plc.Factory, Driver{suffix}", _folder);

        Assert.NotNull(factory);
        Assert.Null(factory.Create(new ConnectionSettings(1, "10.0.0.1", 2000, "10.0.0.2", 2001, 0)));
        Assert.Equal("from the dependency", factory.GetType().GetProperty("Seen")!.GetValue(factory));
    }

    /// <summary>
    /// A file with the expected name that is not a .NET assembly (a native DLL, a truncated copy)
    /// stops the host with the same guided message as the other configuration errors.
    /// </summary>
    [Fact]
    public void AFileThatIsNotADotNetAssemblyGivesTheGuidedMessage()
    {
        var name = "Native" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(_folder, name + ".dll"), [0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03]);
        var key = $"Acme.Plc.Factory, {name}";

        var error = Assert.Throws<InvalidOperationException>(() => ConnectionFactoryLoader.Create(key, _folder));

        Assert.StartsWith($"Wcs:ConnectionFactory = \"{key}\": {name}.dll could not be loaded: ", error.Message);
        Assert.Contains("\"none\"", error.Message);
    }

    /// <summary>
    /// <c>none</c> turns the driver off as an empty value does, in any case: on Windows an
    /// environment variable cannot be empty (<c>set Wcs__ConnectionFactory=</c> deletes it, and an
    /// absent key means the default of the build), so <c>none</c> is the value that works there.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData(" NONE ")]
    [InlineData("")]
    public void NoneAndEmptyTurnTheDriverOff(string configured)
    {
        Assert.Null(ConnectionFactoryLoader.ResolveTypeName(configured, typeof(ConnectionFactoryLoaderTest).Assembly));
    }

    /// <summary>The same assembly without a value: the default of the build, the control case of the theory above.</summary>
    [Fact]
    public void AnAbsentKeyStillTakesTheDefaultOfTheBuild()
    {
        Assert.Equal("Acme.Plc.Factory, Acme.Plc",
            ConnectionFactoryLoader.ResolveTypeName(null, typeof(ConnectionFactoryLoaderTest).Assembly));
    }

    private string Compile(string assemblyName, string source, string? reference = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(IConnectionFactory).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        if (reference != null) references.Add(MetadataReference.CreateFromFile(reference));

        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var path = Path.Combine(_folder, assemblyName + ".dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }
}
