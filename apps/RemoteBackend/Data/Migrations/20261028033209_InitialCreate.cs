using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeldResearch.RemoteBackend.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ProtectedName = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedEmail = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: true),
                    Xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_protection_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "device_grants",
                columns: table => new
                {
                    DeviceCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserCode = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    ProtectedName = table.Column<string>(type: "TEXT", nullable: false),
                    Challenge = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NextPollAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProtectedAccessToken = table.Column<string>(type: "TEXT", nullable: true),
                    AccessTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CredentialExpiresAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_grants", x => x.DeviceCodeHash);
                    table.ForeignKey(
                        name: "FK_device_grants_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProtectedName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_devices_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Subject",
                table: "accounts",
                column: "Subject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_grants_AccessTokenHash",
                table: "device_grants",
                column: "AccessTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_device_grants_AccountId",
                table: "device_grants",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_device_grants_ExpiresAt",
                table: "device_grants",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_device_grants_UserCode",
                table: "device_grants",
                column: "UserCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_devices_AccountId",
                table: "devices",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_devices_ExpiresAt",
                table: "devices",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_devices_TokenHash",
                table: "devices",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "device_grants");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "accounts");
        }
    }
}
