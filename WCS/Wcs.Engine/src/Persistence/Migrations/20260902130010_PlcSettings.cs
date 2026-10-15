using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlcSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CheckDataSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    CompareContainerType = table.Column<short>(type: "INTEGER", nullable: false),
                    ContainerType = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareContainerHeight = table.Column<short>(type: "INTEGER", nullable: false),
                    ContainerHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareContainerLength = table.Column<short>(type: "INTEGER", nullable: false),
                    ContainerLength = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareContainerWidth = table.Column<short>(type: "INTEGER", nullable: false),
                    ContainerWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareHeight = table.Column<short>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareLength = table.Column<short>(type: "INTEGER", nullable: false),
                    Length = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareWidth = table.Column<short>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareWeight = table.Column<short>(type: "INTEGER", nullable: false),
                    Weight = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareTemperature = table.Column<short>(type: "INTEGER", nullable: false),
                    Temperature = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareLengthSide1Surplus = table.Column<short>(type: "INTEGER", nullable: false),
                    LengthSide1Surplus = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareLengthSide2Surplus = table.Column<short>(type: "INTEGER", nullable: false),
                    LengthSide2Surplus = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareWidthSide1Surplus = table.Column<short>(type: "INTEGER", nullable: false),
                    WidthSide1Surplus = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareWidthSide2Surplus = table.Column<short>(type: "INTEGER", nullable: false),
                    WidthSide2Surplus = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareHeightSurplus = table.Column<short>(type: "INTEGER", nullable: false),
                    HeightSurplus = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareCode = table.Column<short>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckDataSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlcConnections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    PlcAddress = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    PcmPort = table.Column<int>(type: "INTEGER", nullable: true),
                    LocalAddress = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    LocalPort = table.Column<int>(type: "INTEGER", nullable: true),
                    OpProgress = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusSenderChannel = table.Column<short>(type: "INTEGER", nullable: false),
                    StatusReceiverChannel = table.Column<short>(type: "INTEGER", nullable: false),
                    OutboundChannelStatus = table.Column<short>(type: "INTEGER", nullable: false),
                    InboundChannelStatus = table.Column<short>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlcConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordDataSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordContainerType = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordContainerHeight = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordContainerLength = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordContainerWidth = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordHeight = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordLength = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordWidth = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordWeight = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordLoadUnitType = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordSide1Surplus = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordSide2Surplus = table.Column<bool>(type: "INTEGER", nullable: false),
                    DecodeLabelCodes = table.Column<bool>(type: "INTEGER", nullable: false),
                    NewLoadUnitTypeId = table.Column<decimal>(type: "TEXT", nullable: false),
                    NewLoadUnitReference = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    NewLoadUnitQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    NewLoadUnitQuantityId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordDataSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckDataSettings");

            migrationBuilder.DropTable(
                name: "PlcConnections");

            migrationBuilder.DropTable(
                name: "RecordDataSettings");
        }
    }
}
