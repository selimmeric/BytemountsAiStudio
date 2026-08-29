using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BytemountsAiStudio.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CokDilliTurev : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "derived_from_run_id",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_derived_from_run_id",
                table: "runs",
                column: "derived_from_run_id",
                filter: "derived_from_run_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_runs_derived_from_run_id",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "derived_from_run_id",
                table: "runs");
        }
    }
}
