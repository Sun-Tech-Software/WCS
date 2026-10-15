using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Il gestore HTTP finto condiviso da <c>DragTest</c>, <c>UndoTest</c> e <c>CommandStackTest</c>:
/// le tre suite distinguevano la stessa cosa - una PUT di
/// geometria singola da una di gruppo - con tre copie quasi identiche della stessa regola. Una sola
/// copia qui, cosi' se la forma delle rotte cambia si aggiorna in un punto solo.
/// </summary>
/// <remarks>
/// Le due rotte si distinguono dal segmento subito prima di "geometry": un Guid per la singola
/// (<c>.../items/{id}/geometry</c>), "items" per l'insieme (<c>.../items/geometry</c>) - conta le
/// richieste HTTP vere che arrivano al gestore, non le invocazioni di un metodo C#, cosi' un test
/// verde prova davvero la forma della chiamata partita.
/// </remarks>
public sealed class GeometryCountingHandler : HttpMessageHandler
{
    public int SingleGeometryRequests { get; private set; }
    public int BulkGeometryRequests { get; private set; }
    public List<string> SingleBodies { get; } = [];
    public List<string> BulkBodies { get; } = [];
    public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
    public IReadOnlyList<Guid>? ConflictItemIds { get; set; }
    public Dictionary<Guid, Guid> ConfirmedVersions { get; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Un cancello opzionale (correzione al compito del riempimento del selezionato): nullo per ogni
    /// test esistente di <c>DragTest</c>, <c>UndoTest</c> e <c>CommandStackTest</c>, che non lo
    /// impostano e vedono la stessa risposta immediata di prima. Quando un test lo imposta, la PUT di
    /// geometria resta sospesa qui finche' il test stesso non completa il <c>Task</c> - la stessa
    /// finestra in cui <c>PlantCanvas</c> tiene il dispositivo in <c>_pendingWrites</c>, usata
    /// per leggere davvero l'attributo di presentazione mostrato mentre la scrittura e' ancora in
    /// volo, non solo dopo che e' gia' tornata.
    /// </summary>
    public TaskCompletionSource? ResponseGate { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isGeometryPut = request.Method == HttpMethod.Put && segments.Length > 0 && segments[^1] == "geometry";

        string? singleBody = null;
        string? bulkBody = null;
        if (isGeometryPut && segments.Length >= 2 && Guid.TryParse(segments[^2], out _))
        {
            SingleGeometryRequests++;
            singleBody = await request.Content!.ReadAsStringAsync(ct);
            SingleBodies.Add(singleBody);
        }
        else if (isGeometryPut)
        {
            BulkGeometryRequests++;
            bulkBody = await request.Content!.ReadAsStringAsync(ct);
            BulkBodies.Add(bulkBody);
        }

        if (ResponseGate is not null) await ResponseGate.Task;

        if (ResponseStatus == HttpStatusCode.Conflict && ConflictItemIds is not null)
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new PlantItemGeometryConflictDto(ConflictItemIds))
            };

        if (ResponseStatus != HttpStatusCode.OK)
            return new HttpResponseMessage(ResponseStatus) { Content = new StringContent(string.Empty) };

        if (singleBody is not null)
        {
            var update = JsonSerializer.Deserialize<PlantItemGeometryUpdateDto>(singleBody, JsonOptions)!;
            var confirmation = Confirm(update.ItemId);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(confirmation) };
        }

        if (bulkBody is not null)
        {
            var updates = JsonSerializer.Deserialize<List<PlantItemGeometryUpdateDto>>(bulkBody, JsonOptions)!;
            var confirmations = updates.Select(update => Confirm(update.ItemId)).ToList();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(confirmations) };
        }

        return new HttpResponseMessage(ResponseStatus) { Content = new StringContent(string.Empty) };
    }

    private PlantItemGeometryVersionDto Confirm(Guid itemId)
    {
        var version = Guid.NewGuid();
        ConfirmedVersions[itemId] = version;
        return new PlantItemGeometryVersionDto(itemId, version);
    }
}
