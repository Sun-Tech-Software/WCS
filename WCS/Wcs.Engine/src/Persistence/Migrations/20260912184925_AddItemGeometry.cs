using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddItemGeometry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Height",
                table: "PlantItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Rotation",
                table: "PlantItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Width",
                table: "PlantItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "X",
                table: "PlantItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Y",
                table: "PlantItems",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Height",
                table: "PlantItems");

            migrationBuilder.DropColumn(
                name: "Rotation",
                table: "PlantItems");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "PlantItems");

            migrationBuilder.DropColumn(
                name: "X",
                table: "PlantItems");

            migrationBuilder.DropColumn(
                name: "Y",
                table: "PlantItems");
        }
    }
}
