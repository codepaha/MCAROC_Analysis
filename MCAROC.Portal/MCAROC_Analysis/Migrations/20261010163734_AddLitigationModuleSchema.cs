using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationModuleSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "litigation");

            migrationBuilder.CreateTable(
                name: "Cases",
                schema: "litigation",
                columns: table => new
                {
                    CaseId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LegacyLitigationCaseId = table.Column<long>(type: "bigint", nullable: true),
                    LegacyJobId = table.Column<long>(type: "bigint", nullable: true),
                    LegacyCaseRowId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CaseKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cases", x => x.CaseId);
                });

            migrationBuilder.CreateTable(
                name: "Subjects",
                schema: "litigation",
                columns: table => new
                {
                    SubjectId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    IdentifiersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subjects", x => x.SubjectId);
                });

            migrationBuilder.CreateTable(
                name: "Scopes",
                schema: "litigation",
                columns: table => new
                {
                    ScopeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<long>(type: "bigint", nullable: false),
                    SubjectId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    NextAliasIndex = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scopes", x => x.ScopeId);
                    table.ForeignKey(
                        name: "FK_Scopes_Subjects_SubjectId",
                        column: x => x.SubjectId,
                        principalSchema: "litigation",
                        principalTable: "Subjects",
                        principalColumn: "SubjectId");
                });

            migrationBuilder.CreateTable(
                name: "ScopeCases",
                schema: "litigation",
                columns: table => new
                {
                    ScopeCaseId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScopeId = table.Column<long>(type: "bigint", nullable: false),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AliasIndex = table.Column<int>(type: "int", nullable: false),
                    FeedLastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsHidden = table.Column<bool>(type: "bit", nullable: false),
                    IsExcluded = table.Column<bool>(type: "bit", nullable: false),
                    ExclusionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AnalystNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PinnedFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IncludedInReport = table.Column<bool>(type: "bit", nullable: false),
                    RedirectAliasesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScopeCases", x => x.ScopeCaseId);
                    table.ForeignKey(
                        name: "FK_ScopeCases_Cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "litigation",
                        principalTable: "Cases",
                        principalColumn: "CaseId");
                    table.ForeignKey(
                        name: "FK_ScopeCases_Scopes_ScopeId",
                        column: x => x.ScopeId,
                        principalSchema: "litigation",
                        principalTable: "Scopes",
                        principalColumn: "ScopeId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_CaseKey",
                schema: "litigation",
                table: "Cases",
                column: "CaseKey");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_LegacyJobId_LegacyCaseRowId",
                schema: "litigation",
                table: "Cases",
                columns: new[] { "LegacyJobId", "LegacyCaseRowId" },
                unique: true,
                filter: "[LegacyJobId] IS NOT NULL AND [LegacyCaseRowId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_LegacyLitigationCaseId",
                schema: "litigation",
                table: "Cases",
                column: "LegacyLitigationCaseId",
                unique: true,
                filter: "[LegacyLitigationCaseId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScopeCases_CaseId",
                schema: "litigation",
                table: "ScopeCases",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ScopeCases_ScopeId_Alias",
                schema: "litigation",
                table: "ScopeCases",
                columns: new[] { "ScopeId", "Alias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScopeCases_ScopeId_CaseId",
                schema: "litigation",
                table: "ScopeCases",
                columns: new[] { "ScopeId", "CaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_Kind_ExternalId",
                schema: "litigation",
                table: "Scopes",
                columns: new[] { "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_SubjectId",
                schema: "litigation",
                table: "Scopes",
                column: "SubjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScopeCases",
                schema: "litigation");

            migrationBuilder.DropTable(
                name: "Cases",
                schema: "litigation");

            migrationBuilder.DropTable(
                name: "Scopes",
                schema: "litigation");

            migrationBuilder.DropTable(
                name: "Subjects",
                schema: "litigation");
        }
    }
}
