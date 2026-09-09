using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyOfficers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyOfficers",
                columns: table => new
                {
                    CompanyOfficerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NameNormalized = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Designation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DesignationAppointmentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OriginalAppointmentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CessationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Flags = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DinCellRaw = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyOfficers", x => x.CompanyOfficerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyOfficers_RequestId_IngestionRunId",
                table: "CompanyOfficers",
                columns: new[] { "RequestId", "IngestionRunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyOfficers");
        }
    }
}
