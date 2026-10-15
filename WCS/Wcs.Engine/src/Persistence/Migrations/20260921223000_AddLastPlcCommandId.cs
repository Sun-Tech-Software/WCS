using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLastPlcCommandId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastCommandId",
                table: "PlcConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCommandId",
                table: "PlcConnections");
        }
    }
}
