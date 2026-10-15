using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingWarehouseTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingWarehouseTransitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoadUnitId = table.Column<decimal>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    NextAttemptAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingWarehouseTransitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingWarehouseTransitions_MissionId_Kind",
                table: "PendingWarehouseTransitions",
                columns: new[] { "MissionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingWarehouseTransitions_NextAttemptAtUtc_Id",
                table: "PendingWarehouseTransitions",
                columns: new[] { "NextAttemptAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingWarehouseTransitions_OperationId",
                table: "PendingWarehouseTransitions",
                column: "OperationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingWarehouseTransitions");
        }
    }
}
