using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Kufr.Backend.Features.Identity.Migrations
{
    /// <inheritdoc />
    public partial class BetaMembershipAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnrollmentGroupId",
                table: "Ceremonies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EnrollmentRevision",
                table: "Ceremonies",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdmissionUniverse",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LaunchGroupAccessRevision",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LaunchGroupJoinedAt",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LaunchGroupRevokedAt",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql("UPDATE AspNetUsers SET LaunchGroupJoinedAt=CreatedAt,LaunchGroupAccessRevision=1 WHERE LaunchGroupId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnrollmentGroupId",
                table: "Ceremonies");

            migrationBuilder.DropColumn(
                name: "EnrollmentRevision",
                table: "Ceremonies");

            migrationBuilder.DropColumn(
                name: "AdmissionUniverse",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LaunchGroupAccessRevision",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LaunchGroupJoinedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LaunchGroupRevokedAt",
                table: "AspNetUsers");
        }
    }
}
