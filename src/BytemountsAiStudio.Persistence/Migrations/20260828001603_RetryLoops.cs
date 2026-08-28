using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BytemountsAiStudio.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetryLoops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_node_executions_run_id_node_id_attempt",
                table: "node_executions");

            migrationBuilder.AddColumn<int>(
                name: "retry_loop",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "loop",
                table: "node_executions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_node_executions_run_id_node_id_loop_attempt",
                table: "node_executions",
                columns: new[] { "run_id", "node_id", "loop", "attempt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_node_executions_run_id_node_id_loop_attempt",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "retry_loop",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "loop",
                table: "node_executions");

            migrationBuilder.CreateIndex(
                name: "ix_node_executions_run_id_node_id_attempt",
                table: "node_executions",
                columns: new[] { "run_id", "node_id", "attempt" },
                unique: true);
        }
    }
}
