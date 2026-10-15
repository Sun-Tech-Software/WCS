using System.Reflection;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Il collaudo a video del 2026-09-13 ha trovato un verbale che non arrivava da nessuna parte: la
/// migrazione della geometria (voce di registro «l'unica cosa che vedremo quando gira da un
/// cliente») usava <c>Navigator.WriteLog(..., LogEntryType.Information)</c>, che con
/// <see cref="Navigator.ActivateDebugLog"/> spento di serie (voce D94) non passa mai. Su 51
/// dispositivi collocati sull'impianto vero, nel registro dell'host non c'era una riga.
/// </summary>
/// <remarks>
/// Questo collaudo prova solo la meta' del motore: che <see cref="Navigator.LastGeometryMigration"/>
/// porti l'esito della migrazione fuori da <see cref="Navigator.OpenDatabases"/> indipendentemente
/// da <see cref="Navigator.ActivateDebugLog"/>, cioe' senza passare dal canale che l'host non legge
/// mai con la configurazione di serie. Non prova che una riga compaia davvero nel registro
/// dell'host: quella meta' — <c>EngineHostedService.LoadEngineAsync</c> che legge questa proprieta'
/// e scrive col proprio <c>ILogger</c> — la prova
/// <c>Wcs.Host.Test.GeometryMigrationReportReachesTheHostLogTest</c>.
/// </remarks>
public sealed class GeometryMigrationReportIsExposedTest : IDisposable
{
    private readonly string _previousDirectory = Navigator.DatabaseDirectory;
    private readonly bool _previousDebugLog = Navigator.ActivateDebugLog;
    private readonly GeometryMigrationReport? _previousReport = Navigator.LastGeometryMigration;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wcs-geometry-report-{Guid.NewGuid():N}");

    public GeometryMigrationReportIsExposedTest()
    {
        Directory.CreateDirectory(_root);
        Navigator.DatabaseDirectory = _root;
    }

    public void Dispose()
    {
        Navigator.Dispose();
        Navigator.DatabaseDirectory = _previousDirectory;
        Navigator.ActivateDebugLog = _previousDebugLog;
        Navigator.LastGeometryMigration = _previousReport;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* file ancora aperto: pazienza */ }
    }

    private static void CallOpenDatabases()
    {
        var method = typeof(Navigator).GetMethod("OpenDatabases", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        try
        {
            method!.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>
    /// Il punto del difetto: con la diagnostica spenta — la configurazione di serie, voce D94 — il
    /// verbale deve comunque finire su <see cref="Navigator.LastGeometryMigration"/>. Prima di
    /// questa proprieta' l'unico portatore era <c>WriteLog</c> a Informazione, che con
    /// <c>ActivateDebugLog</c> falso usciva subito e non lasciava traccia da nessuna parte.
    /// </summary>
    [Fact]
    public void TheReportIsExposedEvenWithDebugLoggingOff()
    {
        Navigator.ActivateDebugLog = false;
        Navigator.LastGeometryMigration = null;

        CallOpenDatabases();

        var report = Navigator.LastGeometryMigration;
        Assert.NotNull(report);
        Assert.True(report!.Executed);
        // Store nuovo, nessun PlantItem noto e nessun blob: tutti i contatori sono zero, ed e' il
        // valore che GeometryMigrationTest prova gia' per un impianto senza blob (Placed == 0
        // distingue "impianto nuovo" da "migrazione mai girata").
        Assert.Equal(0, report.Placed);
        Assert.Equal(0, report.ItemsWithoutPosition);
        Assert.Equal(0, report.ElementsWithoutGuid);
        Assert.Equal(0, report.UnknownGuids);
        Assert.Equal(0, report.GuidsWithoutGeometry);
        Assert.Equal(0, report.PartiallyPlaced);
    }
}
