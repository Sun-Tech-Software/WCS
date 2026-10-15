namespace Wcs.WebEditor.Browser.Test.Elsewhere;

/// <summary>
/// Chiede la cartella dei sorgenti dell'Editor web <b>da un'altra cartella</b>.
/// </summary>
/// <remarks>
/// Serve a una cosa sola, e per questo sta qui sotto invece che accanto alle prove:
/// <see cref="ProductBinary.SourceDirectory"/> ricava il percorso dalla posizione di un file al
/// momento della compilazione, e la domanda che conta e' <b>di quale file</b>. Chiamandolo da
/// <c>ProductBinaryTest.cs</c>, che sta nella stessa cartella di <c>ProductBinary.cs</c>, le due
/// risposte coincidono anche quando il file giusto e' quello sbagliato: la prova misurerebbe se
/// stessa. Da qui no - questa cartella e' una piu' in basso - quindi un percorso preso dal
/// chiamante darebbe un risultato diverso, e si vede.
/// </remarks>
internal static class AskedFromAnotherFolder
{
    public static string SourceDirectory() => ProductBinary.SourceDirectory();
}
