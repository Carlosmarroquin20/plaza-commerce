using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;
using Pgvector;

#nullable disable

namespace Plaza.Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<Vector>(
                name: "embedding",
                table: "products",
                type: "vector(256)",
                nullable: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "products",
                type: "tsvector",
                nullable: false,
                computedColumnSql: "setweight(to_tsvector('english', name), 'A') ||\nsetweight(to_tsvector('english', category), 'B') ||\nsetweight(to_tsvector('english', description), 'C')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_embedding",
                table: "products",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_products_search_vector",
                table: "products",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_products_embedding",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_products_search_vector",
                table: "products");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "products");

            migrationBuilder.DropColumn(
                name: "embedding",
                table: "products");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
