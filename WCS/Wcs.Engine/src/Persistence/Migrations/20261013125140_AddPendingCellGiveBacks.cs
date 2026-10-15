using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingCellGiveBacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingCellGiveBacks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoadUnitId = table.Column<decimal>(type: "TEXT", nullable: false),
                    CellId = table.Column<decimal>(type: "TEXT", nullable: false),
                    DoorCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Parked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingCellGiveBacks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingCellGiveBacks_LoadUnitId",
                table: "PendingCellGiveBacks",
                column: "LoadUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingCellGiveBacks_OperationId",
                table: "PendingCellGiveBacks",
                column: "OperationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingCellGiveBacks");
        }
    }
}
