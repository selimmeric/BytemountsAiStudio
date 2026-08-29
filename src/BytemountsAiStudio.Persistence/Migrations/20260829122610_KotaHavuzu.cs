using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BytemountsAiStudio.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KotaHavuzu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_credentials_channel_id_provider_key",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "ix_credentials_provider_key",
                table: "credentials");

            migrationBuilder.AddColumn<string>(
                name: "account",
                table: "credentials",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,

                // VARSAYILAN "default", BOS DIZGE DEGIL.
                //
                // EF bos dizge uretiyor ve bu SESSIZ BIR HATA olurdu:
                // mevcut kimlik kayitlari "" hesabina duser, kod
                // "default" yazar ve ikisi hicbir zaman eslesmez --
                // calisan bir YouTube kimligi havuzda GORUNMEZ olurdu.
                defaultValue: "default");

            migrationBuilder.CreateTable(
                name: "quota_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    account = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    day_key = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reserved_units = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quota_ledger", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credentials_channel_id_provider_key_account",
                table: "credentials",
                columns: new[] { "channel_id", "provider_key", "account" },
                unique: true,
                filter: "channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_credentials_provider_key_account",
                table: "credentials",
                columns: new[] { "provider_key", "account" },
                unique: true,
                filter: "channel_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_quota_ledger_provider_key_account_day_key",
                table: "quota_ledger",
                columns: new[] { "provider_key", "account", "day_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quota_ledger");

            migrationBuilder.DropIndex(
                name: "ix_credentials_channel_id_provider_key_account",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "ix_credentials_provider_key_account",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "account",
                table: "credentials");

            migrationBuilder.CreateIndex(
                name: "ix_credentials_channel_id_provider_key",
                table: "credentials",
                columns: new[] { "channel_id", "provider_key" },
                unique: true,
                filter: "channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_credentials_provider_key",
                table: "credentials",
                column: "provider_key",
                unique: true,
                filter: "channel_id IS NULL");
        }
    }
}
