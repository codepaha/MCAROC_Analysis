using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <summary>Issue #294: the resolver's word index (CompanyNameTokens — derived data, filled afterwards by
    /// ImportCompanyMasterData --backfill-names, which rebuilds it from NameCore) and the IdentityResolutions audit
    /// table.</summary>
    public partial class AddIdentityResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyNameTokens",
                columns: table => new
                {
                    Token = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Identifier = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyNameTokens", x => new { x.Token, x.Identifier });
                });

            migrationBuilder.CreateTable(
                name: "IdentityResolutions",
                columns: table => new
                {
                    IdentityResolutionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: true),
                    InputName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    InputIdentifier = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    HintsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NormalizedInput = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ChosenIdentifier = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: true),
                    RecommendedIdentifier = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: true),
                    AutoSelectEligible = table.Column<bool>(type: "bit", nullable: false),
                    TopScore = table.Column<double>(type: "float", nullable: true),
                    Margin = table.Column<double>(type: "float", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CandidatesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AlgorithmVersion = table.Column<int>(type: "int", nullable: false),
                    NormalizerVersion = table.Column<int>(type: "int", nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MasterSnapshotDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AppliedToRequest = table.Column<bool>(type: "bit", nullable: false),
                    ExistingRequestId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityResolutions", x => x.IdentityResolutionId);
                    table.ForeignKey(
                        name: "FK_IdentityResolutions_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyNameTokens_Identifier",
                table: "CompanyNameTokens",
                column: "Identifier");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_RequestId_CreatedUtc",
                table: "IdentityResolutions",
                columns: new[] { "RequestId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyNameTokens");

            migrationBuilder.DropTable(
                name: "IdentityResolutions");
        }
    }
}
