using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmEval.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TestCaseCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "TestCases",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestCases_Category",
                table: "TestCases",
                column: "Category");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TestCases_Category",
                table: "TestCases");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "TestCases");
        }
    }
}
