using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vev.Atlas.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetCreatedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "assets",
                type: "TEXT",
                maxLength: 128,
                nullable: true,
                defaultValue: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "assets");
        }
    }
}
