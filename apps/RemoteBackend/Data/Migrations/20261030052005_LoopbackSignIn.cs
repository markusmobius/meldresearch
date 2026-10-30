using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeldResearch.RemoteBackend.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoopbackSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Device-code links lasted at most ten minutes; issued app credentials are in "devices" and are kept.
            migrationBuilder.DropTable(
                name: "device_grants");

            migrationBuilder.CreateTable(
                name: "sign_in_codes",
                columns: table => new
                {
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtectedName = table.Column<string>(type: "TEXT", nullable: false),
                    Challenge = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sign_in_codes", x => x.CodeHash);
                    table.ForeignKey(
                        name: "FK_sign_in_codes_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sign_in_codes_AccountId",
                table: "sign_in_codes",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_sign_in_codes_ExpiresAt",
                table: "sign_in_codes",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sign_in_codes");

            migrationBuilder.CreateTable(
                name: "device_grants",
                columns: table => new
                {
                    DeviceCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AccessTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Challenge = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CredentialExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NextPollAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ProtectedAccessToken = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UserCode = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false)
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
        }
    }
}
