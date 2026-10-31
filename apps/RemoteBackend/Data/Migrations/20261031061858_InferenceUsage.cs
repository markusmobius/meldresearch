using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeldResearch.RemoteBackend.Data.Migrations
{
    /// <inheritdoc />
    public partial class InferenceUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inference_usage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Deployment = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inference_usage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inference_usage_AtMs",
                table: "inference_usage",
                column: "AtMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inference_usage");
        }
    }
}
