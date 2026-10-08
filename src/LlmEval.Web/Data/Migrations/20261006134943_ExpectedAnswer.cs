using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmEval.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpectedAnswer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpectedAnswer",
                table: "TestCases",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedAnswerSnapshot",
                table: "Iterations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedAnswer",
                table: "TestCases");

            migrationBuilder.DropColumn(
                name: "ExpectedAnswerSnapshot",
                table: "Iterations");
        }
    }
}
