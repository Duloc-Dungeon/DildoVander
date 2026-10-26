using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kufr.Backend.Features.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AccountLaunchGroupMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LaunchGroupId",
                table: "Invitations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LaunchGroupId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LaunchGroupId",
                table: "Invitations");

            migrationBuilder.DropColumn(
                name: "LaunchGroupId",
                table: "AspNetUsers");
        }
    }
}
