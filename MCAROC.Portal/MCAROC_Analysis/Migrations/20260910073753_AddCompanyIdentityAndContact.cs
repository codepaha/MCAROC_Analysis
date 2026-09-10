using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyIdentityAndContact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessAddress",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntityType",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastAgmDate",
                table: "CompanyProfiles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lei",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeiStatus",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListingStatus",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "McaSumOfChargesCrore",
                table: "CompanyProfiles",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NarrativeDescription",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisteredAddressCity",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisteredAddressPinCode",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisteredAddressState",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Segment",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                table: "CompanyProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CompanyEmails",
                columns: table => new
                {
                    CompanyEmailId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmailAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    IsReachable = table.Column<bool>(type: "bit", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyEmails", x => x.CompanyEmailId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyEmails_RequestId_IngestionRunId",
                table: "CompanyEmails",
                columns: new[] { "RequestId", "IngestionRunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyEmails");

            migrationBuilder.DropColumn(
                name: "BusinessAddress",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "EntityType",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "LastAgmDate",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "Lei",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "LeiStatus",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "ListingStatus",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "McaSumOfChargesCrore",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "NarrativeDescription",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "RegisteredAddressCity",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "RegisteredAddressPinCode",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "RegisteredAddressState",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "Segment",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "Website",
                table: "CompanyProfiles");
        }
    }
}
