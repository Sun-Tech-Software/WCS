using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefusedCellsReservationsToGiveBackAndCellsToMarkFull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CellsRefusedByThePlc",
                table: "Missions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DepositRefusedByThePlc",
                table: "Missions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CellsToMarkFull",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DoorCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CellsToMarkFull", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReservationsToGiveBack",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservationsToGiveBack", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CellsToMarkFull_DoorCode",
                table: "CellsToMarkFull",
                column: "DoorCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReservationsToGiveBack_MissionId",
                table: "ReservationsToGiveBack",
                column: "MissionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CellsToMarkFull");

            migrationBuilder.DropTable(
                name: "ReservationsToGiveBack");

            migrationBuilder.DropColumn(
                name: "CellsRefusedByThePlc",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "DepositRefusedByThePlc",
                table: "Missions");
        }
    }
}
