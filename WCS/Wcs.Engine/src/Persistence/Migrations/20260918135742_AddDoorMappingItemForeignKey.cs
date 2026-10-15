using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <summary>
    /// La chiave esterna di <c>DoorMappings.ItemId</c> verso <c>PlantItems</c>, con cancellazione a
    /// cascata: da qui in poi una riga della mappa porte su un dispositivo che non esiste non puo'
    /// piu' nascere - <b>finche' la connessione ha i vincoli accesi</b>.
    /// </summary>
    /// <remarks>
    /// <b>La condizione, detta e non sottintesa.</b> SQLite applica le chiavi esterne solo con
    /// <c>PRAGMA foreign_keys</c> acceso, che e' per connessione e non per database:
    /// <c>Microsoft.Data.Sqlite</c> lo accende da se' su ogni connessione che apre, ed e' da li' che
    /// viene la garanzia - non dal file. Una connessione aperta da un altro strumento con i vincoli
    /// spenti potrebbe ancora scrivere una riga orfana, e la cancellazione esplicita in
    /// <c>PlantStore.DeleteItemAsync</c> e' scritta per la stessa ragione (si veda il suo
    /// <c>remarks</c>): le due affermazioni dicono ora la stessa cosa.
    /// <para>
    /// <b>Le righe orfane che trova le cancella, e non in silenzio.</b> SQLite costruisce la tabella
    /// nuova e ci ricopia dentro le righe: con anche una sola riga orfana la ricopiatura fallirebbe
    /// con «FOREIGN KEY constraint failed» e l'host non partirebbe piu', quindi la cancellazione qui
    /// sotto e' la condizione perche' questa migrazione possa esistere. Quello che cancella e'
    /// scritto nel registro del motore <b>prima</b> di cancellarlo, con quante righe e su quali
    /// identificativi: lo fa <c>WcsDatabase.ReportOrphanDoorMappings</c>, che guarda il database
    /// appena prima di applicare le migrazioni in sospeso. Una riga orfana non era comunque
    /// raggiungibile dall'Editor (che filtra per <c>ItemId</c>), ma le letture per cella e per porta
    /// del motore la vedevano: e' un dato che toglie, non un dato che si perde senza che se ne sappia
    /// nulla.
    /// </para>
    /// <para>
    /// Misura riportata dal brief del lotto (impianto di collaudo, 2026-09-18), non rifatta qui:
    /// 2.729 righe di mappatura, nessuna orfana - li' questa migrazione non cancella niente. E' un
    /// impianto solo, ed e' la ragione per cui la riga di registro esiste.
    /// </para>
    /// </remarks>
    public partial class AddDoorMappingItemForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM DoorMappings WHERE ItemId NOT IN (SELECT ItemId FROM PlantItems);");

            migrationBuilder.AddForeignKey(
                name: "FK_DoorMappings_PlantItems_ItemId",
                table: "DoorMappings",
                column: "ItemId",
                principalTable: "PlantItems",
                principalColumn: "ItemId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Torna indietro la chiave esterna, non le righe che l'Up ha cancellato: quelle non sono
            // da nessuna parte. Un ritorno indietro rimette il vincolo com'era, non l'impianto.
            migrationBuilder.DropForeignKey(
                name: "FK_DoorMappings_PlantItems_ItemId",
                table: "DoorMappings");
        }
    }
}
