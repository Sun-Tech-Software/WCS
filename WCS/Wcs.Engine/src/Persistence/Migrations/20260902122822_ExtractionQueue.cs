using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtractionQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExtractionQueue",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssignmentId = table.Column<decimal>(type: "TEXT", nullable: true),
                    LoadUnitId = table.Column<decimal>(type: "TEXT", nullable: false),
                    CellId = table.Column<decimal>(type: "TEXT", nullable: true),
                    BufferCellId = table.Column<decimal>(type: "TEXT", nullable: true),
                    SourceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Extract = table.Column<bool>(type: "INTEGER", nullable: false),
                    WorkstationId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Processed = table.Column<bool>(type: "INTEGER", nullable: false),
                    RelocatorItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DoorCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SourceAssignmentId = table.Column<decimal>(type: "TEXT", nullable: true),
                    ExtractionRowId = table.Column<decimal>(type: "TEXT", nullable: true),
                    DestinationCellId = table.Column<decimal>(type: "TEXT", nullable: true),
                    DestinationDoorCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Compacting = table.Column<bool>(type: "INTEGER", nullable: false),
                    AssignmentGuid = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApiCommandId = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionQueue", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionQueue_AssignmentId",
                table: "ExtractionQueue",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionQueue_LoadUnitId",
                table: "ExtractionQueue",
                column: "LoadUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionQueue_RelocatorItemId_Processed",
                table: "ExtractionQueue",
                columns: new[] { "RelocatorItemId", "Processed" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtractionQueue");
        }
    }
}
