using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vev.Atlas.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModuleSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_module_settings",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConsentAccepted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConsentAcceptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConsentAcceptedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    EncryptedApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_module_settings", x => x.TenantId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_module_settings");
        }
    }
}
