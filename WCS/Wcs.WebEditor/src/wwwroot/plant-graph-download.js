// Scarica l'esportazione dell'impianto (compito 12) dal circuito Blazor Server al browser: da
// server non esiste nessun modo di scrivere un file sul disco del client se non passare da qui,
// perche' e' il browser - non l'host - a tenere il file system dell'utente. Riceve un
// DotNetStreamReference (non un array di byte gia' in JSON: l'esportazione e' piccola sull'impianto
// di prova, ma il giro byte[] -> JSON -> array JS raddoppierebbe comunque il lavoro per niente
// quando lo streaming e' gia' incluso in .NET) e lo trasforma in un Blob scaricato con un <a
// download> creato e cliccato al volo - la stessa tecnica che la documentazione di Blazor descrive
// per "download a file from a stream".
export async function downloadFileFromStream(fileName, contentStreamReference) {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);

    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName ?? "";
    anchor.click();
    anchor.remove();

    URL.revokeObjectURL(url);
}
