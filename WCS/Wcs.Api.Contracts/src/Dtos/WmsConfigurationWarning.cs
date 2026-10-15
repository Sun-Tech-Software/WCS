namespace Wcs.Api.Contracts.Dtos;

/// <summary>Una riga del controllo di configurazione del WMS (<c>sp_check_configurazione</c>): le colonne le decide la procedura.</summary>
public sealed record WmsConfigurationWarning(IReadOnlyDictionary<string, string?> Values);
