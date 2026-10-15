using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations.History
{
    /// <inheritdoc />
    public partial class HandledLoadUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HandledLoadUnits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<short>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CellId = table.Column<decimal>(type: "TEXT", nullable: false),
                    LoadUnitCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ProcessId = table.Column<decimal>(type: "TEXT", nullable: true),
                    ResultDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LoadUnitProcessId = table.Column<decimal>(type: "TEXT", nullable: true),
                    NoteHeaderId = table.Column<decimal>(type: "TEXT", nullable: true),
                    ExtractionHeaderId = table.Column<decimal>(type: "TEXT", nullable: true),
                    LoadUnitId = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HandledLoadUnits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HandledLoadUnits_Date",
                table: "HandledLoadUnits",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_HandledLoadUnits_LoadUnitId",
                table: "HandledLoadUnits",
                column: "LoadUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_HandledLoadUnits_MissionId",
                table: "HandledLoadUnits",
                column: "MissionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HandledLoadUnits");
        }
    }
}
