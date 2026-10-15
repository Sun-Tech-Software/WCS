using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMissionServedOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ServedAssignmentGuid",
                table: "Missions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ServedAssignmentId",
                table: "Missions",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ServedExtractionRowId",
                table: "Missions",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServedAssignmentGuid",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "ServedAssignmentId",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "ServedExtractionRowId",
                table: "Missions");
        }
    }
}
