using System;
using System.Data;

namespace Helper.Converters;

public static class DataRowConverter
{
    #region Public Methods

    /// <summary>
    /// Legge una colonna obbligatoria, sollevando un'eccezione se non c'e' o non e' convertibile.
    /// </summary>
    /// <typeparam name="T">Tipo atteso del valore</typeparam>
    /// <param name="value">Riga da cui leggere</param>
    /// <param name="field">Nome della colonna</param>
    /// <returns>Il valore convertito</returns>
    /// <exception cref="ArgumentException">La colonna non esiste nel risultato</exception>
    /// <exception cref="InvalidCastException">Il valore non e' convertibile nel tipo richiesto</exception>
    /// <remarks>
    /// <para>
    /// Affianca <see cref="GetValue{T}"/> e non lo sostituisce. <c>GetValue</c> cattura l'errore di
    /// colonna inesistente e restituisce <c>default</c>: una colonna che non c'e' non produce un
    /// errore ma uno zero, <b>indistinguibile da uno zero legittimo</b>. E' il motivo per cui alcuni
    /// difetti dell'area WCS sono rimasti invisibili per anni — si vedano le voci <c>D56</c>,
    /// <c>R9</c>, <c>R10</c> e <c>R11</c> del registro in <c>WCS/doc/debito-tecnico.md</c>.
    /// </para>
    /// <para>
    /// Va usato dove la colonna e' <b>obbligatoria</b>, cioe' dove la sua assenza e' un difetto della
    /// query e non un dato mancante. Il comportamento di <c>GetValue</c> non cambia, perche'
    /// cambiarlo farebbe emergere di colpo ogni lettura sbagliata di ogni prodotto del monorepo.
    /// </para>
    /// </remarks>
    public static T GetValueOrThrow<T>(this DataRow? value, string field)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (!value.Table.Columns.Contains(field))
            throw new ArgumentException(
                $"La colonna '{field}' non esiste nel risultato: la query non la seleziona.",
                nameof(field));

        if (!ObjectExtensions.ConvertValueToType(value[field], out T? convertito))
            throw new InvalidCastException(
                $"La colonna '{field}' contiene un valore non convertibile in {typeof(T).Name}.");

        return convertito!;
    }

    public static T? GetValue<T>(this DataRow? value, string? field)
    {
        try
        {
            if (field == null || value == null)
            {
                return default;
            }
            return ObjectExtensions.ConvertValueToType(value[field], out T? convertFrom) ? convertFrom : default;
        }
        catch (Exception)
        {
            return default!;
        }
    }

    public static T? GetValue<T>(this DataRow value, string field, DataRowVersion version)
    {
        try
        {
            return ObjectExtensions.ConvertValueToType(value[field, version], out T? convertFrom) ? convertFrom : default;
        }
        catch (Exception)
        {
            return default!;
        }
    }
        
    #endregion Public Methods

}