using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Commands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CommandType = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LoadUnitId = table.Column<decimal>(type: "TEXT", nullable: true),
                    StorageDoorCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    ErrorDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AssignmentGuid = table.Column<Guid>(type: "TEXT", nullable: true),
                    Info = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ShowStatus = table.Column<short>(type: "INTEGER", nullable: false),
                    WorkstationId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ProcessId = table.Column<decimal>(type: "TEXT", nullable: true),
                    BufferCellId = table.Column<decimal>(type: "TEXT", nullable: false),
                    PickUpDoorCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    LoadUnitTypeId = table.Column<decimal>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DestinationItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReferenceId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    FromApi = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiStatus = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Commands_Date",
                table: "Commands",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Commands_Status",
                table: "Commands",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Commands");
        }
    }
}
