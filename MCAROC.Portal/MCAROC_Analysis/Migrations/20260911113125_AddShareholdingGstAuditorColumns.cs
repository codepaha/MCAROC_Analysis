using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddShareholdingGstAuditorColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveCompliance",
                table: "Shareholdings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CessationDate",
                table: "Shareholdings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompanyStatus",
                table: "Shareholdings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfIncorporation",
                table: "Shareholdings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Designation",
                table: "Shareholdings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "Shareholdings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PaidUpCapitalCrore",
                table: "Shareholdings",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelationshipRaw",
                table: "Shareholdings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "Shareholdings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SumOfChargesCrore",
                table: "Shareholdings",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveCompliance",
                table: "RelatedCorporates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "RelatedCorporates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CentreJurisdiction",
                table: "GstRegistrations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalNameOfBusiness",
                table: "GstRegistrations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateJurisdiction",
                table: "GstRegistrations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirmName",
                table: "AuditorObservations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirmRegistrationNumber",
                table: "AuditorObservations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MembershipNumber",
                table: "AuditorObservations",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveCompliance",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "CessationDate",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "CompanyStatus",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "DateOfIncorporation",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "Designation",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "PaidUpCapitalCrore",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "RelationshipRaw",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "SumOfChargesCrore",
                table: "Shareholdings");

            migrationBuilder.DropColumn(
                name: "ActiveCompliance",
                table: "RelatedCorporates");

            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "RelatedCorporates");

            migrationBuilder.DropColumn(
                name: "CentreJurisdiction",
                table: "GstRegistrations");

            migrationBuilder.DropColumn(
                name: "LegalNameOfBusiness",
                table: "GstRegistrations");

            migrationBuilder.DropColumn(
                name: "StateJurisdiction",
                table: "GstRegistrations");

            migrationBuilder.DropColumn(
                name: "FirmName",
                table: "AuditorObservations");

            migrationBuilder.DropColumn(
                name: "FirmRegistrationNumber",
                table: "AuditorObservations");

            migrationBuilder.DropColumn(
                name: "MembershipNumber",
                table: "AuditorObservations");
        }
    }
}
