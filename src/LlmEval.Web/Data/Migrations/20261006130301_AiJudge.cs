using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmEval.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiJudge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JudgeModelId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsJudge",
                table: "Models",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "JudgeRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IterationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    RawOutput = table.Column<string>(type: "text", nullable: true),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    RequestedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JudgeRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JudgeRuns_Iterations_IterationId",
                        column: x => x.IterationId,
                        principalTable: "Iterations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JudgeRuns_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_JudgeModelId",
                table: "Users",
                column: "JudgeModelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JudgeRuns_IterationId",
                table: "JudgeRuns",
                column: "IterationId");

            migrationBuilder.CreateIndex(
                name: "IX_JudgeRuns_ModelId",
                table: "JudgeRuns",
                column: "ModelId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Models_JudgeModelId",
                table: "Users",
                column: "JudgeModelId",
                principalTable: "Models",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Models_JudgeModelId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "JudgeRuns");

            migrationBuilder.DropIndex(
                name: "IX_Users_JudgeModelId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "JudgeModelId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsJudge",
                table: "Models");
        }
    }
}
