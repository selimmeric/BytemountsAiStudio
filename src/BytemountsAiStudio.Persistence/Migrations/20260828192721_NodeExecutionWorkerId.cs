using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BytemountsAiStudio.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NodeExecutionWorkerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "worker_id",
                table: "node_executions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "worker_id",
                table: "node_executions");
        }
    }
}
