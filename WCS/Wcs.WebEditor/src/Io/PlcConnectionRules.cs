using Wcs.Api.Contracts.Dtos;
using Wcs.Common;

namespace Wcs.WebEditor.Io;

/// <summary>Why a row of a list of PLC connections cannot be written.</summary>
public enum PlcConnectionProblemKind
{
    /// <summary>The Id is below one: the WinForms' first check (a new row is born with -1 and waits for a real Id).</summary>
    IdNotPositive,

    /// <summary>The Id is on another row too: the WinForms' second check.</summary>
    IdRepeated,
}

/// <summary>A row that cannot be written: its position in the list checked, its Id, and why.</summary>
public sealed record PlcConnectionProblem(int Index, long Id, PlcConnectionProblemKind Kind);

/// <summary>
/// The ONE check of a list of PLC connections, and the one comparison of two lists - for the grid of
/// the draw.io page (row 62, compito 4) and for the import of a plant (ImportExport) alike. The words
/// are each caller's: these methods only say what is wrong, and where. The comparison itself lives in
/// Wcs.Common (<see cref="PlcConnectionComparison"/>), where Task 4c put it for the host too; the host
/// no longer compares (an import is accepted only in Stop), and here it only maps the DTO onto it.
/// </summary>
/// <remarks>
/// <para>
/// The checks are the WinForms' (<c>FrmPCM_PLCS_LIST_FormClosing</c>: every Id greater than zero,
/// then no Id repeated) and the host's own for an imported graph (<c>GraphEndpoints.Validate</c>: the
/// same two, and a device's <c>DeviceConnectionId</c> a whole number not below zero). The host's
/// <c>PUT /plc/connections</c> checks none of them - a list with a repeated Id fails there inside
/// the database write, not with a refusal - so for the grid this is the only check there is.
/// </para>
/// <para>
/// The comparison is by Id, the key the host replaces by: see <see cref="PlcConnectionComparison"/>.
/// </para>
/// </remarks>
public static class PlcConnectionRules
{
    /// <summary>
    /// Every row that cannot be written, in the WinForms' order: the Ids below one first, then every
    /// row whose Id is on another row too. Empty when the list can be written.
    /// </summary>
    public static IReadOnlyList<PlcConnectionProblem> Problems(IReadOnlyList<PlcConnectionDto> connections)
    {
        var problems = new List<PlcConnectionProblem>();
        for (var i = 0; i < connections.Count; i++)
            if (connections[i].Id < 1)
                problems.Add(new PlcConnectionProblem(i, connections[i].Id, PlcConnectionProblemKind.IdNotPositive));

        var repeated = connections.Where(connection => connection.Id >= 1)
            .GroupBy(connection => connection.Id)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        for (var i = 0; i < connections.Count; i++)
            if (repeated.Contains(connections[i].Id))
                problems.Add(new PlcConnectionProblem(i, connections[i].Id, PlcConnectionProblemKind.IdRepeated));

        return problems;
    }

    /// <summary>
    /// The connection a device's <c>DeviceConnectionId</c> names (zero: none), when it is a whole
    /// number between zero and the largest Id - false otherwise, as the host's graph check refuses it.
    /// </summary>
    public static bool TryReadReference(decimal deviceConnectionId, out long connectionId)
    {
        connectionId = 0;
        if (deviceConnectionId < 0 || deviceConnectionId != decimal.Truncate(deviceConnectionId) || deviceConnectionId > long.MaxValue)
            return false;

        connectionId = (long)deviceConnectionId;
        return true;
    }

    /// <summary>
    /// What <paramref name="proposed"/> would change on <paramref name="current"/>, by Id. Neither list
    /// may repeat an Id (<see cref="Problems"/> first): a repeated Id throws.
    /// </summary>
    public static PlcConnectionChanges Compare(IReadOnlyList<PlcConnectionDto> current, IReadOnlyList<PlcConnectionDto> proposed) =>
        PlcConnectionComparison.Compare(current.Select(Settings), proposed.Select(Settings));

    /// <summary>Every field of the DTO: the five values and the key, as the host replaces them.</summary>
    private static PlcConnectionSettings Settings(PlcConnectionDto connection) =>
        new(connection.Id, connection.Description, connection.PlcAddress, connection.PcmPort, connection.LocalAddress, connection.LocalPort);
}
