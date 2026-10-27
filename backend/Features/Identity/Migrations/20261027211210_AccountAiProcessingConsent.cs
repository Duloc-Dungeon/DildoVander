using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kufr.Backend.Features.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AccountAiProcessingConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OpenAiConsentAt",
                table: "Ceremonies",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenAiConsentVersion",
                table: "Ceremonies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "OpenAiConsentAt",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenAiConsentVersion",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenAiConsentAt",
                table: "Ceremonies");

            migrationBuilder.DropColumn(
                name: "OpenAiConsentVersion",
                table: "Ceremonies");

            migrationBuilder.DropColumn(
                name: "OpenAiConsentAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "OpenAiConsentVersion",
                table: "AspNetUsers");
        }
    }
}
