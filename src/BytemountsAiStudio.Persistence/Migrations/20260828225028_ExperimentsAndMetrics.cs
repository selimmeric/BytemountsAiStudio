using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BytemountsAiStudio.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExperimentsAndMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "experiments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dimension = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    minimum_detectable_effect = table.Column<double>(type: "double precision", nullable: false),
                    required_per_variant = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experiments", x => x.id);
                    table.ForeignKey(
                        name: "fk_experiments_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "publication_metrics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_offset = table.Column<int>(type: "integer", nullable: false),
                    impressions = table.Column<int>(type: "integer", nullable: false),
                    clicks = table.Column<int>(type: "integer", nullable: false),
                    views = table.Column<int>(type: "integer", nullable: false),
                    watch_seconds = table.Column<long>(type: "bigint", nullable: false),
                    measured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_publication_metrics", x => x.id);
                    table.ForeignKey(
                        name: "fk_publication_metrics_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "experiment_variants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    experiment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_control = table.Column<bool>(type: "boolean", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experiment_variants", x => x.id);
                    table.ForeignKey(
                        name: "fk_experiment_variants_experiments_experiment_id",
                        column: x => x.experiment_id,
                        principalTable: "experiments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "experiment_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    experiment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experiment_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_experiment_assignments_experiment_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "experiment_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_experiment_assignments_experiments_experiment_id",
                        column: x => x.experiment_id,
                        principalTable: "experiments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_experiment_assignments_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_experiment_assignments_experiment_id_run_id",
                table: "experiment_assignments",
                columns: new[] { "experiment_id", "run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_experiment_assignments_run_id",
                table: "experiment_assignments",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_experiment_assignments_variant_id",
                table: "experiment_assignments",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_experiment_variants_experiment_id_name",
                table: "experiment_variants",
                columns: new[] { "experiment_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_experiments_channel_id_dimension_state",
                table: "experiments",
                columns: new[] { "channel_id", "dimension", "state" },
                unique: true,
                filter: "state = 'Running'");

            migrationBuilder.CreateIndex(
                name: "ix_publication_metrics_run_id_day_offset",
                table: "publication_metrics",
                columns: new[] { "run_id", "day_offset" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "experiment_assignments");

            migrationBuilder.DropTable(
                name: "publication_metrics");

            migrationBuilder.DropTable(
                name: "experiment_variants");

            migrationBuilder.DropTable(
                name: "experiments");
        }
    }
}
