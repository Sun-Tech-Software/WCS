using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations.History
{
    /// <inheritdoc />
    public partial class PlcMessagesOut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlcMessages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlcMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Asi = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    CodingError = table.Column<bool>(type: "INTEGER", nullable: false),
                    CodingErrorDescription = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DataCrc32 = table.Column<long>(type: "INTEGER", nullable: false),
                    DataLength = table.Column<long>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FrameLength = table.Column<long>(type: "INTEGER", nullable: false),
                    HeaderLength = table.Column<short>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageLength = table.Column<int>(type: "INTEGER", nullable: false),
                    OpCode = table.Column<short>(type: "INTEGER", nullable: false),
                    OpProgress = table.Column<long>(type: "INTEGER", nullable: false),
                    OpType = table.Column<short>(type: "INTEGER", nullable: false),
                    Partition = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    PlcId = table.Column<decimal>(type: "TEXT", nullable: false),
                    Subitem = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Syscode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Type = table.Column<short>(type: "INTEGER", nullable: false),
                    Xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlcMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlcMessages_Asi",
                table: "PlcMessages",
                column: "Asi");

            migrationBuilder.CreateIndex(
                name: "IX_PlcMessages_Date",
                table: "PlcMessages",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_PlcMessages_PlcId",
                table: "PlcMessages",
                column: "PlcId");
        }
    }
}
