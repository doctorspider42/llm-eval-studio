using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmEval.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Batches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "Iterations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Repetition",
                table: "Iterations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    Repetitions = table.Column<int>(type: "integer", nullable: false),
                    ModelIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    AutoJudge = table.Column<bool>(type: "boolean", nullable: false),
                    JudgeModelIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Batches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Batches_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Iterations_BatchId",
                table: "Iterations",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_CreatedById",
                table: "Batches",
                column: "CreatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_Iterations_Batches_BatchId",
                table: "Iterations",
                column: "BatchId",
                principalTable: "Batches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Iterations_Batches_BatchId",
                table: "Iterations");

            migrationBuilder.DropTable(
                name: "Batches");

            migrationBuilder.DropIndex(
                name: "IX_Iterations_BatchId",
                table: "Iterations");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "Iterations");

            migrationBuilder.DropColumn(
                name: "Repetition",
                table: "Iterations");
        }
    }
}
