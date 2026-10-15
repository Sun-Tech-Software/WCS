using System.Reflection;
using System.Runtime.Loader;
using Wcs.Connection.Interface;

namespace Wcs.Host.Engine;

/// <summary>
/// Picks the field driver from the configuration: the <c>Wcs:ConnectionFactory</c> setting holds the
/// assembly-qualified name of an <see cref="IConnectionFactory"/>, loaded by reflection.
/// </summary>
/// <remarks>
/// <para>
/// Three cases. The key holds a type name: that factory, or the host refuses to start saying what
/// is wrong (a plant configured for a driver must not quietly run without it). The key is
/// <c>none</c> (<see cref="Off"/>, in any case) or present and empty: no field driver, only the
/// loopback driver. The key is absent: the default of the build, which is the assembly metadata
/// <see cref="DefaultMetadataKey"/> of the host assembly; a build without it has no field driver.
/// </para>
/// <para>
/// <c>none</c> exists because an empty value only works in <c>appsettings.json</c>: on Windows an
/// environment variable cannot be empty (<c>set Wcs__ConnectionFactory=</c> deletes it), so the key
/// would be absent and the build default would load.
/// </para>
/// <para>
/// The default is how a build that ships a driver keeps working on existing plants with no
/// configuration change: the build adds the driver to the host and names its factory in that
/// metadata (a <c>Wcs.Connection.*.Host.props</c> file next to the driver project, imported by
/// <c>Wcs.Host.csproj</c>).
/// </para>
/// <para>
/// The assembly is looked up first by the runtime (the host dependencies), then as
/// <c>AssemblyName.dll</c> in the host folder, so a driver can be dropped next to
/// <c>Wcs.Host.exe</c> without rebuilding the host. The assemblies such a driver depends on (a PLC
/// library from NuGet, say) are not in <c>Wcs.Host.deps.json</c> either: they are resolved from the
/// driver's folder, through the driver's own <c>.deps.json</c> when it ships one, otherwise as
/// <c>Name.dll</c> next to the driver. A dependency the host already has is the host's copy.
/// </para>
/// </remarks>
public static class ConnectionFactoryLoader
{
    /// <summary>The configuration key.</summary>
    public const string SettingKey = "Wcs:ConnectionFactory";

    /// <summary>The key of the <see cref="AssemblyMetadataAttribute"/> that names the default factory of a build.</summary>
    public const string DefaultMetadataKey = "Wcs.DefaultConnectionFactory";

    /// <summary>The value that turns the field driver off, also from an environment variable (compared ignoring case).</summary>
    public const string Off = "none";

    private static readonly HashSet<string> ResolvedFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The type name in effect: the configured one, none when the key is empty or <see cref="Off"/>,
    /// the build default when the key is absent.
    /// </summary>
    /// <param name="configured">The value of <c>Wcs:ConnectionFactory</c>, <c>null</c> when absent.</param>
    /// <param name="hostAssembly">The assembly that carries the build default.</param>
    public static string? ResolveTypeName(string? configured, Assembly hostAssembly)
    {
        ArgumentNullException.ThrowIfNull(hostAssembly);

        if (configured != null)
            return string.IsNullOrWhiteSpace(configured) || configured.Trim().Equals(Off, StringComparison.OrdinalIgnoreCase)
                ? null
                : configured.Trim();

        var fallback = hostAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == DefaultMetadataKey)?.Value;
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    /// <summary>
    /// Builds the factory named by <paramref name="typeName"/>, or returns <c>null</c> when there is none.
    /// </summary>
    /// <param name="typeName">An assembly-qualified type name, or <c>null</c>.</param>
    /// <param name="baseDirectory">The folder where a driver assembly unknown to the runtime is looked for.</param>
    /// <exception cref="InvalidOperationException">
    /// The type cannot be found, its assembly cannot be loaded, it is not an
    /// <see cref="IConnectionFactory"/>, or it cannot be built.
    /// </exception>
    public static IConnectionFactory? Create(string? typeName, string baseDirectory)
    {
        if (typeName == null) return null;

        var type = FindType(typeName, baseDirectory)
                   ?? throw Misconfigured(typeName,
                       $"the type was not found. Write it as \"Namespace.TypeName, AssemblyName\" and put " +
                       $"AssemblyName.dll in the host folder ({baseDirectory})");

        if (!typeof(IConnectionFactory).IsAssignableFrom(type))
            throw Misconfigured(typeName, $"{type.FullName} does not implement {typeof(IConnectionFactory).FullName}");

        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
            throw Misconfigured(typeName, $"{type.FullName} needs a public parameterless constructor");

        try
        {
            return (IConnectionFactory)Activator.CreateInstance(type)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw Misconfigured(typeName, $"the constructor of {type.FullName} failed: {ex.InnerException.Message}", ex.InnerException);
        }
    }

    private static Type? FindType(string typeName, string baseDirectory)
    {
        try
        {
            if (Type.GetType(typeName, throwOnError: false) is { } known)
                return known;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // Not among the host dependencies: try the host folder below.
        }

        var comma = typeName.IndexOf(',');
        if (comma <= 0) return null;

        AssemblyName assemblyName;
        try
        {
            assemblyName = new AssemblyName(typeName[(comma + 1)..].Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException)
        {
            return null;
        }

        var fileName = assemblyName.Name + ".dll";
        var path = Path.GetFullPath(Path.Combine(baseDirectory, fileName));
        if (!File.Exists(path)) return null;

        ResolveDependenciesFrom(path);
        Assembly assembly;
        try
        {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
        {
            throw Misconfigured(typeName, $"{fileName} could not be loaded: {ex.Message}", ex);
        }
        return assembly.GetType(typeName[..comma].Trim(), throwOnError: false);
    }

    /// <summary>
    /// Lets the default load context find the dependencies of a driver loaded from
    /// <paramref name="driverPath"/>: the runtime looks only at the host's <c>deps.json</c>, and asks
    /// this handler for what it does not find there. One handler per folder.
    /// </summary>
    private static void ResolveDependenciesFrom(string driverPath)
    {
        var folder = Path.GetDirectoryName(driverPath)!;
        lock (ResolvedFolders)
        {
            if (!ResolvedFolders.Add(folder)) return;
        }

        AssemblyDependencyResolver? resolver = null;
        try
        {
            resolver = new AssemblyDependencyResolver(driverPath);
        }
        catch (InvalidOperationException)
        {
            // No host resolver available (for example a single-file host): the folder lookup below remains.
        }

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var candidate = resolver?.ResolveAssemblyToPath(name);
            if (candidate == null && name.Name != null)
            {
                var sideBySide = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(sideBySide)) candidate = sideBySide;
            }
            return candidate == null ? null : context.LoadFromAssemblyPath(candidate);
        };
    }

    private static InvalidOperationException Misconfigured(string typeName, string reason, Exception? inner = null) =>
        new($"{SettingKey} = \"{typeName}\": {reason}. Set it in appsettings.json ({SettingKey}) or in the " +
            $"environment variable Wcs__ConnectionFactory; set it to \"{Off}\" to run with the loopback driver only " +
            "(connection type Test).", inner);
}
