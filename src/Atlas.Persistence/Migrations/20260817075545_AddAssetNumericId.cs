using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vev.Atlas.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetNumericId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NumericId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Lifecycle = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "relationships",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    FromId = table.Column<string>(type: "TEXT", nullable: false),
                    ToId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relationships", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_assets_TenantId_Kind",
                table: "assets",
                columns: new[] { "TenantId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_TenantId_NumericId",
                table: "assets",
                columns: new[] { "TenantId", "NumericId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relationships_TenantId_FromId",
                table: "relationships",
                columns: new[] { "TenantId", "FromId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "relationships");
        }
    }
}
