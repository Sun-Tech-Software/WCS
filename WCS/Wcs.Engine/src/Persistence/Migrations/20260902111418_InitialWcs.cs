using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWcs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Missions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoadUnitId = table.Column<decimal>(type: "TEXT", nullable: false),
                    PositionItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DestinationItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LoadUnitCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    StatusDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StorageDoorCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    PickUpDoorCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    CommandId = table.Column<decimal>(type: "TEXT", nullable: true),
                    RelocateItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MissionType = table.Column<short>(type: "INTEGER", nullable: false),
                    MovingDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    OperationType = table.Column<short>(type: "INTEGER", nullable: false),
                    BufferCellId = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExtractionQueueId = table.Column<decimal>(type: "TEXT", nullable: false),
                    LoadUnitTypeId = table.Column<decimal>(type: "TEXT", nullable: false),
                    GroupedMovementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HandlingMode = table.Column<int>(type: "INTEGER", nullable: false),
                    GatewayItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StorageDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceAsi = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    SourceSubitem = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    SourcePartition = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    DestinationAsi = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    DestinationSubitem = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    DestinationPartition = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    NoteAssignmentId = table.Column<decimal>(type: "TEXT", nullable: false),
                    AssignmentGuid = table.Column<Guid>(type: "TEXT", nullable: true),
                    NoteProcessingId = table.Column<decimal>(type: "TEXT", nullable: false),
                    ActiveFilter = table.Column<bool>(type: "INTEGER", nullable: false),
                    Tare = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReallocatedDestination = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Missions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Missions_MissionId",
                table: "Missions",
                column: "MissionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Missions");
        }
    }
}
