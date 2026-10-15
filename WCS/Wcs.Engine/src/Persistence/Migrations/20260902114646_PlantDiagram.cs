using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Engine.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlantDiagram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlantDiagrams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Diagram = table.Column<byte[]>(type: "BLOB", nullable: true),
                    DiagramXml = table.Column<byte[]>(type: "BLOB", nullable: true),
                    View = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CheckCounter = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusCounter = table.Column<int>(type: "INTEGER", nullable: false),
                    CompactingStatus = table.Column<short>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantDiagrams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlantItems",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DeviceType = table.Column<short>(type: "INTEGER", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    LoadUnitStatus = table.Column<short>(type: "INTEGER", nullable: false),
                    Asi = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Partition = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Subitem = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    DelayBlind = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeOutPlcMoving = table.Column<int>(type: "INTEGER", nullable: false),
                    LoadUnitImageHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    LoadUnitImageWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoExecuteStep = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoUnloadLoadUnit = table.Column<bool>(type: "INTEGER", nullable: false),
                    BuildDestinationItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BuildDestinationItemId2 = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConnectionType = table.Column<short>(type: "INTEGER", nullable: false),
                    MotionType = table.Column<short>(type: "INTEGER", nullable: false),
                    GetLoadUnitInfo = table.Column<bool>(type: "INTEGER", nullable: false),
                    LookWhereAllocateLoadUnit = table.Column<short>(type: "INTEGER", nullable: false),
                    RejectItemWhenNoAllocation = table.Column<Guid>(type: "TEXT", nullable: true),
                    RelocateItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecordLoadUnitInfo = table.Column<bool>(type: "INTEGER", nullable: false),
                    RejectItemWhenInfoError = table.Column<Guid>(type: "TEXT", nullable: true),
                    LinkedCellId = table.Column<decimal>(type: "TEXT", nullable: false),
                    StorageOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordStorageLength = table.Column<short>(type: "INTEGER", nullable: false),
                    DeviceConnectionId = table.Column<decimal>(type: "TEXT", nullable: false),
                    ShowInfoMessageAssignedLoadUnit = table.Column<bool>(type: "INTEGER", nullable: false),
                    StoreUnitStep = table.Column<int>(type: "INTEGER", nullable: false),
                    PickUpUnitStep = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderNumberInGroup = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupRoutePathId = table.Column<int>(type: "INTEGER", nullable: false),
                    PriorityInRoutePath = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeLapseToExecuteStep = table.Column<int>(type: "INTEGER", nullable: false),
                    BelongingGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupLevelId = table.Column<int>(type: "INTEGER", nullable: false),
                    WaitingTimeForcedLoading = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupLoadingActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    GroupTransferringActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    GroupedMoveLoadUnitTo = table.Column<bool>(type: "INTEGER", nullable: false),
                    RespectQueue = table.Column<bool>(type: "INTEGER", nullable: false),
                    WaitBelongedItemsEmptyBeforeLoading = table.Column<bool>(type: "INTEGER", nullable: false),
                    StorageOffsetReversed = table.Column<int>(type: "INTEGER", nullable: false),
                    StoragePartition = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    StoragePartitionReversed = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    TriggerItemGetLoadUnitInfo = table.Column<Guid>(type: "TEXT", nullable: true),
                    RunningStepId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TriggerItemToExtractPoint1 = table.Column<Guid>(type: "TEXT", nullable: true),
                    TriggerItemToExtractPoint2 = table.Column<Guid>(type: "TEXT", nullable: true),
                    DistributeOnBuffers = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddStorageLengthLoadUnitDepth = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableDefragmentation = table.Column<bool>(type: "INTEGER", nullable: false),
                    CellToDefragmentId = table.Column<decimal>(type: "TEXT", nullable: false),
                    AcceptLoadUnitIfDestinationIsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxUnloadWeight = table.Column<int>(type: "INTEGER", nullable: false),
                    ExtractionCheckPoint = table.Column<bool>(type: "INTEGER", nullable: false),
                    RejectDestinationUserRequest = table.Column<Guid>(type: "TEXT", nullable: true),
                    TimeOutGetLoadUnitInfo = table.Column<int>(type: "INTEGER", nullable: false),
                    ToBeFiltered = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnLoadLoadUnitActiveFilter = table.Column<bool>(type: "INTEGER", nullable: false),
                    WaitLookingLevels = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantItems", x => x.ItemId);
                });

            migrationBuilder.CreateTable(
                name: "PlantJunctions",
                columns: table => new
                {
                    JunctionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantJunctions", x => x.JunctionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlantJunctions_SourceItemId_DestinationItemId",
                table: "PlantJunctions",
                columns: new[] { "SourceItemId", "DestinationItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlantDiagrams");

            migrationBuilder.DropTable(
                name: "PlantItems");

            migrationBuilder.DropTable(
                name: "PlantJunctions");
        }
    }
}
