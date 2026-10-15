using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMissionStorageCellReserved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StorageCellReserved",
                table: "Missions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Le missioni dell'operatore con una porta, scritte prima del segno, hanno la cella prenotata
            // per loro: prima del segno ogni cambio dell'operatore verso un magazzino ne prenotava una
            // (MissionGateway.ReserveACellAndCommitAsync), e non c'era altra via per dargli una porta. Con
            // il segno giu', su un membro di un gruppo sarebbero copie (MissionGateway.IsACopy): il deposito
            // non chiederebbe la cella e il navigatore non la riguarderebbe (revisione del quarto giro del
            // compito 17). Le altre righe restano «non sua», cioe' il deposito di prima.
            migrationBuilder.Sql(
                "UPDATE Missions SET StorageCellReserved = 1 " +
                "WHERE OperatorDestination = 1 AND StorageDoorCode IS NOT NULL AND StorageDoorCode <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageCellReserved",
                table: "Missions");
        }
    }
}
