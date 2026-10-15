using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations.History
{
    /// <inheritdoc />
    public partial class InitialHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NavigatorLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Asi = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Partition = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Subitem = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    DestinationItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MissionStart = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MissionEnd = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MissionStatus = table.Column<short>(type: "INTEGER", nullable: false),
                    RunningItemId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavigatorLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NavigatorLog_Date",
                table: "NavigatorLog",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_NavigatorLog_ItemId",
                table: "NavigatorLog",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_NavigatorLog_MissionId",
                table: "NavigatorLog",
                column: "MissionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NavigatorLog");
        }
    }
}
