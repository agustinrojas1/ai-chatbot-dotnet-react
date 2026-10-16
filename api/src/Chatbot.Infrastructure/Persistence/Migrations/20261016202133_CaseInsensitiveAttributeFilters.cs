using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chatbot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CaseInsensitiveAttributeFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_offerings_Attributes",
                table: "offerings");

            migrationBuilder.AddColumn<JsonElement>(
                name: "AttributesLower",
                table: "offerings",
                type: "jsonb",
                nullable: false,
                computedColumnSql: "lower(\"Attributes\"::text)::jsonb",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_offerings_AttributesLower",
                table: "offerings",
                column: "AttributesLower")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_offerings_AttributesLower",
                table: "offerings");

            migrationBuilder.DropColumn(
                name: "AttributesLower",
                table: "offerings");

            migrationBuilder.CreateIndex(
                name: "IX_offerings_Attributes",
                table: "offerings",
                column: "Attributes")
                .Annotation("Npgsql:IndexMethod", "gin");
        }
    }
}
