using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmEval.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResponseCostsAndCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CostUsd",
                table: "Results",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CostUsd",
                table: "JudgeRuns",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoJudgeSuppressed",
                table: "Iterations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CostUsd",
                table: "Results");

            migrationBuilder.DropColumn(
                name: "CostUsd",
                table: "JudgeRuns");

            migrationBuilder.DropColumn(
                name: "AutoJudgeSuppressed",
                table: "Iterations");
        }
    }
}
