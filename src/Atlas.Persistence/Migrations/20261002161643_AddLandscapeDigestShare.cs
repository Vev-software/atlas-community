using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vev.Atlas.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLandscapeDigestShare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "digest_key",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProtectedPrivateKey = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKeySpki = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_digest_key", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "digest_state",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceInstanceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_digest_state", x => x.TenantId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "digest_key");

            migrationBuilder.DropTable(
                name: "digest_state");
        }
    }
}
