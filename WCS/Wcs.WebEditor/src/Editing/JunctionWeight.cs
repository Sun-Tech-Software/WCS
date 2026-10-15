namespace Wcs.WebEditor.Editing;

/// <summary>
/// Il peso di una giunzione, come lo sceglie chi collega (decisione del proprietario, 2026-09-14,
/// dopo la prima proposta di questo compito basata su un numero in barra): "bassa" o "alta", mai una
/// cifra - l'interfaccia non deve mostrarla a chi collega. Dietro le quinte resta un intero
/// (<c>JunctionDto.Priority</c>, che l'host e <c>RouteGraph</c> nel motore conoscono): questo tipo e'
/// solo la traduzione fra i due mondi, non un contratto nuovo verso l'host.
/// </summary>
public enum JunctionWeight
{
    Low,
    High,
}

/// <summary>
/// Traduce fra <see cref="JunctionWeight"/> e la priorita' intera che l'host si aspetta, nei due versi.
/// </summary>
/// <remarks>
/// Due fatti misurati il 2026-09-14, non indovinati - il primo sul codice, il secondo sull'impianto
/// vero:
/// <list type="bullet">
/// <item><c>RouteGraph</c> nel motore ordina gli archi uscenti da un dispositivo per priorita'
/// <b>crescente</b> (<c>WCS/Wcs.Engine/src/Routing/RouteGraph.cs:42</c>,
/// <c>OrderBy(e =&gt; e.Priority)</c>): un numero <b>piu' basso</b> e' esplorato per primo. "Alta
/// priorita'" e' quindi un numero <b>piccolo</b> - il contrario di quello che quasi chiunque
/// darebbe per scontato guardando solo l'etichetta.</item>
/// <item>L'impianto DEMOPLANT, interrogato dal vero attraverso <c>GET /plant/junctions</c> sulla
/// 5580 il 2026-09-14, ha <b>59 giunzioni su 59</b> a priorita' <b>99</b>: nessuna varieta' da
/// preservare, e 99 e' il valore "normale" di questo impianto oggi.</item>
/// </list>
/// La mappatura: <see cref="JunctionWeight.High"/> vale <see cref="HighPriority"/> (0) - un numero
/// minore di 99, esplorato prima di ogni giunzione esistente. <see cref="JunctionWeight.Low"/> vale
/// <see cref="LowPriority"/> (99) - alla pari di tutte le giunzioni di oggi, non davanti a loro.
/// </remarks>
public static class JunctionWeights
{
    public const int HighPriority = 0;
    public const int LowPriority = 99;

    public static int ToPriority(this JunctionWeight weight) =>
        weight == JunctionWeight.High ? HighPriority : LowPriority;

    /// <summary>
    /// Il verso opposto: una giunzione puo' portare una priorita' diversa da 0 o 99 (scritta prima di
    /// questo compito, o da un altro strumento) - si legge come <see cref="JunctionWeight.High"/> se
    /// e' minore di <see cref="LowPriority"/> (esplorata prima del valore "normale" di oggi),
    /// <see cref="JunctionWeight.Low"/> altrimenti. Una bucketizzazione dichiarata a due soli valori:
    /// non c'e' una terza categoria da mostrare a chi collega.
    /// </summary>
    public static JunctionWeight FromPriority(int priority) =>
        priority < LowPriority ? JunctionWeight.High : JunctionWeight.Low;
}
