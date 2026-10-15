using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DoorMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DoorMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DoorCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CellId = table.Column<decimal>(type: "TEXT", nullable: false),
                    Side = table.Column<string>(type: "TEXT", maxLength: 1, nullable: true),
                    OperationActive = table.Column<short>(type: "INTEGER", nullable: false),
                    StoringLocked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoorMappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoorMappings_CellId",
                table: "DoorMappings",
                column: "CellId");

            migrationBuilder.CreateIndex(
                name: "IX_DoorMappings_DoorCode",
                table: "DoorMappings",
                column: "DoorCode");

            migrationBuilder.CreateIndex(
                name: "IX_DoorMappings_ItemId",
                table: "DoorMappings",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DoorMappings");
        }
    }
}
