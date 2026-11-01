using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeldResearch.RemoteBackend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountPictures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_pictures",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProtectedImage = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SourceHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_pictures", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_account_pictures_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_pictures");
        }
    }
}
