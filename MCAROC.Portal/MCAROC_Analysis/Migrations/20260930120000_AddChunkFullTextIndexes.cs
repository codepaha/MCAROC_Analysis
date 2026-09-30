using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations;

/// <summary>#194: full-text indexes on DocumentChunks.ChunkText and LitigationOrderChunks.ChunkText for the lexical
/// half of hybrid chat retrieval (see Services/Chat/HybridSearch.cs). EF Core has no model concept for full-text
/// indexes, so this is raw SQL and the model snapshot is unchanged.
///
/// Full-text DDL cannot run inside a user transaction, hence suppressTransaction. The whole thing is a no-op where
/// Full-Text Search is not installed (plain SQL Server Express, the stock mssql/server Linux image) — the
/// retrievers detect the missing index at query time and stay on pure vector search. Every statement is
/// idempotent, so a server that gains FTS later can pick the indexes up by re-running this script by hand.
/// STOPLIST = OFF keeps short legal tokens ("IA", "TP", "No") searchable; CHANGE_TRACKING AUTO means
/// newly-chunked rows become searchable shortly after insert rather than at the instant of it (an accepted,
/// documented window — the vector path covers new rows immediately).</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260930120000_AddChunkFullTextIndexes")]
public sealed class AddChunkFullTextIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'ChatChunksCatalog')
                    EXEC(N'CREATE FULLTEXT CATALOG ChatChunksCatalog');

                IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.DocumentChunks'))
                    EXEC(N'CREATE FULLTEXT INDEX ON dbo.DocumentChunks (ChunkText LANGUAGE 1033)
                        KEY INDEX PK_DocumentChunks ON ChatChunksCatalog
                        WITH CHANGE_TRACKING AUTO, STOPLIST = OFF');

                IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.LitigationOrderChunks'))
                    EXEC(N'CREATE FULLTEXT INDEX ON dbo.LitigationOrderChunks (ChunkText LANGUAGE 1033)
                        KEY INDEX PK_LitigationOrderChunks ON ChatChunksCatalog
                        WITH CHANGE_TRACKING AUTO, STOPLIST = OFF');
            END
            """, suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.LitigationOrderChunks'))
                EXEC(N'DROP FULLTEXT INDEX ON dbo.LitigationOrderChunks');
            IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.DocumentChunks'))
                EXEC(N'DROP FULLTEXT INDEX ON dbo.DocumentChunks');
            IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'ChatChunksCatalog')
                EXEC(N'DROP FULLTEXT CATALOG ChatChunksCatalog');
            """, suppressTransaction: true);
    }
}
