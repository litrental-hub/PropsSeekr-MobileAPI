using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddDualHandshakeLegacyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvailabilityStatus",
                table: "PropertyRequests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FreshnessCategory",
                table: "PropertyRequests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FreshnessScore",
                table: "PropertyRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastConfirmedAt",
                table: "PropertyRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "confirmation_compliance_rate",
                table: "brokers",
                type: "numeric",
                nullable: false,
                defaultValue: 100.00m);

            migrationBuilder.AddColumn<bool>(
                name: "visibility_penalty_flag",
                table: "brokers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "visibility_penalty_expires_at",
                table: "brokers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "credit_packs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Credits = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_packs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "credit_transactions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    broker_id = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    balance_after = table.Column<int>(type: "integer", nullable: false),
                    reference_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    reference_id = table.Column<long>(type: "bigint", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_credit_transactions_brokers_broker_id",
                        column: x => x.broker_id,
                        principalTable: "brokers",
                        principalColumn: "brokerid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "credit_wallets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    broker_id = table.Column<int>(type: "integer", nullable: false),
                    free_credits_balance = table.Column<int>(type: "integer", nullable: false),
                    paid_credits_balance = table.Column<int>(type: "integer", nullable: false),
                    free_credits_reset_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_wallets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_credit_wallets_brokers_broker_id",
                        column: x => x.broker_id,
                        principalTable: "brokers",
                        principalColumn: "brokerid",
                        onDelete: ReferentialAction.Cascade);
                });

            // listings table already exists in database

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    broker_id = table.Column<int>(type: "integer", nullable: false),
                    in_app_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    whatsapp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    reminder_frequency_cap_hours = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_preferences_brokers_broker_id",
                        column: x => x.broker_id,
                        principalTable: "brokers",
                        principalColumn: "brokerid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    broker_id = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    channel_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notifications_brokers_broker_id",
                        column: x => x.broker_id,
                        principalTable: "brokers",
                        principalColumn: "brokerid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("ALTER TABLE requirements_table ADD COLUMN IF NOT EXISTS last_confirmed_at timestamp with time zone;");
            migrationBuilder.Sql("ALTER TABLE requirements_table ADD COLUMN IF NOT EXISTS freshness_score integer;");
            migrationBuilder.Sql("ALTER TABLE requirements_table ADD COLUMN IF NOT EXISTS freshness_category character varying(50);");
            migrationBuilder.Sql("ALTER TABLE requirements_table ADD COLUMN IF NOT EXISTS freshness_updated_at timestamp with time zone;");

            migrationBuilder.Sql("DROP VIEW IF EXISTS requirements;");
            migrationBuilder.Sql(@"
                CREATE OR REPLACE VIEW requirements AS 
                SELECT 
                    requirementid,
                    broker_id,
                    source,
                    raw_message_text,
                    requirement_type,
                    property_type,
                    configurations,
                    preferred_locality_ids,
                    budget,
                    budget_unit,
                    size,
                    furnishing_pref,
                    facing_pref,
                    status,
                    expires_at,
                    search_vector,
                    embedding,
                    created_at,
                    updated_at,
                    content_hash,
                    group_name,
                    message_datetime,
                    budget_type,
                    isavailable,
                    last_confirmed_at,
                    freshness_score,
                    freshness_category,
                    freshness_updated_at
                FROM requirements_table
                WHERE isavailable = true;");

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    broker_id = table.Column<int>(type: "integer", nullable: false),
                    credit_pack_id = table.Column<int>(type: "integer", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Gateway = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    gateway_txn_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payments_brokers_broker_id",
                        column: x => x.broker_id,
                        principalTable: "brokers",
                        principalColumn: "brokerid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_payments_credit_packs_credit_pack_id",
                        column: x => x.credit_pack_id,
                        principalTable: "credit_packs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "disputes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    broker_id = table.Column<int>(type: "integer", nullable: false),
                    transaction_id = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolution_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    resolved_amount = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_disputes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_disputes_brokers_broker_id",
                        column: x => x.broker_id,
                        principalTable: "brokers",
                        principalColumn: "brokerid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_disputes_credit_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "credit_transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "matches",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "matched");

            migrationBuilder.CreateTable(
                name: "deals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    match_id = table.Column<int>(type: "integer", nullable: false),
                    marked_by_broker_id = table.Column<int>(type: "integer", nullable: false),
                    deal_value = table.Column<decimal>(type: "numeric", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_deals_brokers_marked_by_broker_id",
                        column: x => x.marked_by_broker_id,
                        principalTable: "brokers",
                        principalColumn: "brokerid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_deals_matches_match_id",
                        column: x => x.match_id,
                        principalTable: "matches",
                        principalColumn: "matchid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "match_confirmations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    match_id = table.Column<int>(type: "integer", nullable: false),
                    broker_id = table.Column<int>(type: "integer", nullable: false),
                    availability_confirmed = table.Column<bool>(type: "boolean", nullable: true),
                    price_valid = table.Column<bool>(type: "boolean", nullable: true),
                    price_negotiable = table.Column<bool>(type: "boolean", nullable: true),
                    ready_to_connect = table.Column<bool>(type: "boolean", nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    window_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_confirmations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_match_confirmations_brokers_broker_id",
                        column: x => x.broker_id,
                        principalTable: "brokers",
                        principalColumn: "brokerid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_match_confirmations_matches_match_id",
                        column: x => x.match_id,
                        principalTable: "matches",
                        principalColumn: "matchid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reveals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    match_id = table.Column<int>(type: "integer", nullable: false),
                    revealed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reveals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reveals_matches_match_id",
                        column: x => x.match_id,
                        principalTable: "matches",
                        principalColumn: "matchid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "visits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    match_id = table.Column<int>(type: "integer", nullable: false),
                    marked_by_broker_id = table.Column<int>(type: "integer", nullable: false),
                    visit_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_visits_brokers_marked_by_broker_id",
                        column: x => x.marked_by_broker_id,
                        principalTable: "brokers",
                        principalColumn: "brokerid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_visits_matches_match_id",
                        column: x => x.match_id,
                        principalTable: "matches",
                        principalColumn: "matchid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_brokers_phone_number\" ON brokers (phone_number);");

            migrationBuilder.CreateIndex(
                name: "IX_credit_transactions_broker_id_CreatedAt",
                table: "credit_transactions",
                columns: new[] { "broker_id", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_wallets_broker_id",
                table: "credit_wallets",
                column: "broker_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deals_marked_by_broker_id",
                table: "deals",
                column: "marked_by_broker_id");

            migrationBuilder.CreateIndex(
                name: "IX_deals_match_id",
                table: "deals",
                column: "match_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_disputes_broker_id_Status",
                table: "disputes",
                columns: new[] { "broker_id", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_disputes_transaction_id",
                table: "disputes",
                column: "transaction_id");

            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_listings_broker_id\" ON listings_table (broker_id);");

            migrationBuilder.CreateIndex(
                name: "IX_match_confirmations_broker_id",
                table: "match_confirmations",
                column: "broker_id");

            migrationBuilder.CreateIndex(
                name: "IX_match_confirmations_match_id_broker_id",
                table: "match_confirmations",
                columns: new[] { "match_id", "broker_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_match_confirmations_window_expires_at",
                table: "match_confirmations",
                column: "window_expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_matches_listing_broker_id",
                table: "matches",
                column: "listing_broker_id");

            migrationBuilder.CreateIndex(
                name: "IX_matches_listing_id",
                table: "matches",
                column: "listing_id");

            migrationBuilder.CreateIndex(
                name: "IX_matches_requirement_broker_id",
                table: "matches",
                column: "requirement_broker_id");

            migrationBuilder.CreateIndex(
                name: "IX_matches_requirement_id",
                table: "matches",
                column: "requirement_id");

            migrationBuilder.CreateIndex(
                name: "IX_matches_state",
                table: "matches",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "IX_matches_status",
                table: "matches",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_notification_preferences_broker_id",
                table: "notification_preferences",
                column: "broker_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_broker_id_read_at",
                table: "notifications",
                columns: new[] { "broker_id", "read_at" });

            migrationBuilder.CreateIndex(
                name: "IX_payments_broker_id",
                table: "payments",
                column: "broker_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_credit_pack_id",
                table: "payments",
                column: "credit_pack_id");

            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_requirements_broker_id\" ON requirements_table (broker_id);");

            migrationBuilder.CreateIndex(
                name: "IX_reveals_match_id",
                table: "reveals",
                column: "match_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_visits_marked_by_broker_id",
                table: "visits",
                column: "marked_by_broker_id");

            migrationBuilder.CreateIndex(
                name: "IX_visits_match_id",
                table: "visits",
                column: "match_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credit_wallets");

            migrationBuilder.DropTable(
                name: "deals");

            migrationBuilder.DropTable(
                name: "disputes");

            migrationBuilder.DropTable(
                name: "match_confirmations");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "reveals");

            migrationBuilder.DropTable(
                name: "visits");

            migrationBuilder.DropTable(
                name: "credit_transactions");

            migrationBuilder.DropTable(
                name: "credit_packs");

            migrationBuilder.DropTable(
                name: "matches");

            migrationBuilder.DropTable(
                name: "listings");

            migrationBuilder.DropTable(
                name: "requirements");

            migrationBuilder.DropTable(
                name: "brokers");

            migrationBuilder.DropColumn(
                name: "AvailabilityStatus",
                table: "PropertyRequests");

            migrationBuilder.DropColumn(
                name: "FreshnessCategory",
                table: "PropertyRequests");

            migrationBuilder.DropColumn(
                name: "FreshnessScore",
                table: "PropertyRequests");

            migrationBuilder.DropColumn(
                name: "LastConfirmedAt",
                table: "PropertyRequests");
        }
    }
}
