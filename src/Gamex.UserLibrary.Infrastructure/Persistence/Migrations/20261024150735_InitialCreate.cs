using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gamex.UserLibrary.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "catalog_games",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    title = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    cover_image_url = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    release_date = table.Column<DateTime>(type: "date", nullable: true),
                    metacritic = table.Column<short>(type: "smallint", nullable: true),
                    source_updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    synced_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_games", x => x.id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "catalog_platforms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    slug = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    source_updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_platforms", x => x.id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    library_entry_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    game_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    owner_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    partner_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    display_name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    password_hash = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    avatar_url = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    city = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    region = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    country = table.Column<string>(type: "char(2)", nullable: false),
                    bio = table.Column<string>(type: "text", nullable: true),
                    role = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    rating_avg = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false),
                    rating_count = table.Column<int>(type: "int", nullable: false),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "catalog_game_platforms",
                columns: table => new
                {
                    game_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    platform_id = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_game_platforms", x => new { x.game_id, x.platform_id });
                    table.ForeignKey(
                        name: "fk_catalog_game_platforms_catalog_games_game_id",
                        column: x => x.game_id,
                        principalTable: "catalog_games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_game_platforms_catalog_platforms_platform_id",
                        column: x => x.platform_id,
                        principalTable: "catalog_platforms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "library_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    user_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    game_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    platform_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    format = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    is_owned = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    is_wishlist = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    for_exchange = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    for_sale = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    want_to_buy = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    condition = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    price = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    preferred_games = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    listing_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    listed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    listing_expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_library_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_library_entries_catalog_games_game_id",
                        column: x => x.game_id,
                        principalTable: "catalog_games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_library_entries_catalog_platforms_platform_id",
                        column: x => x.platform_id,
                        principalTable: "catalog_platforms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_library_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "oauth_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    user_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    provider = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    provider_uid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_providers", x => x.id);
                    table.ForeignKey(
                        name: "fk_oauth_providers_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    user_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    token_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    used_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_reset_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_password_reset_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    user_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    token_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    replaced_by_token_id = table.Column<Guid>(type: "char(36)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_platforms",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    platform_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_platforms", x => new { x.user_id, x.platform_id });
                    table.ForeignKey(
                        name: "fk_user_platforms_catalog_platforms_platform_id",
                        column: x => x.platform_id,
                        principalTable: "catalog_platforms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_platforms_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_ratings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    rater_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    ratee_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    transaction_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    score = table.Column<sbyte>(type: "tinyint", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_ratings", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_ratings_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_ratings_users_ratee_id",
                        column: x => x.ratee_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_ratings_users_rater_id",
                        column: x => x.rater_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "listing_completions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    library_entry_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    listed_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    user_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    partner_user_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_listing_completions", x => x.id);
                    table.ForeignKey(
                        name: "fk_listing_completions_library_entries_library_entry_id",
                        column: x => x.library_entry_id,
                        principalTable: "library_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_listing_completions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_game_platforms_platform_id",
                table: "catalog_game_platforms",
                column: "platform_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_games_title",
                table: "catalog_games",
                column: "title");

            migrationBuilder.CreateIndex(
                name: "ux_catalog_platforms_slug",
                table: "catalog_platforms",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_library_entries_game_id_platform_id_is_wishlist",
                table: "library_entries",
                columns: new[] { "game_id", "platform_id", "is_wishlist" });

            migrationBuilder.CreateIndex(
                name: "ix_library_entries_listing_status_listed_at",
                table: "library_entries",
                columns: new[] { "listing_status", "listed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_library_entries_listing_status_listing_expires_at",
                table: "library_entries",
                columns: new[] { "listing_status", "listing_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_library_entries_platform_id_listing_status",
                table: "library_entries",
                columns: new[] { "platform_id", "listing_status" });

            migrationBuilder.CreateIndex(
                name: "ix_library_entries_user_id_game_id",
                table: "library_entries",
                columns: new[] { "user_id", "game_id" });

            migrationBuilder.CreateIndex(
                name: "ux_library_entries_user_id_game_id_platform_id_format",
                table: "library_entries",
                columns: new[] { "user_id", "game_id", "platform_id", "format" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_listing_completions_user_id",
                table: "listing_completions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_listing_completions_library_entry_id_listed_at_user_id",
                table: "listing_completions",
                columns: new[] { "library_entry_id", "listed_at", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_providers_user_id",
                table: "oauth_providers",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_oauth_providers_provider_provider_uid",
                table: "oauth_providers",
                columns: new[] { "provider", "provider_uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_tokens_user_id",
                table: "password_reset_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_password_reset_tokens_token_hash",
                table: "password_reset_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id_revoked_at",
                table: "refresh_tokens",
                columns: new[] { "user_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ux_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_library_entry_id",
                table: "transactions",
                column: "library_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_owner_id",
                table: "transactions",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_partner_id",
                table: "transactions",
                column: "partner_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_platforms_platform_id",
                table: "user_platforms",
                column: "platform_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_ratings_ratee_id_created_at",
                table: "user_ratings",
                columns: new[] { "ratee_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_user_ratings_transaction_id",
                table: "user_ratings",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_ratings_rater_id_transaction_id",
                table: "user_ratings",
                columns: new[] { "rater_id", "transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_city",
                table: "users",
                column: "city");

            migrationBuilder.CreateIndex(
                name: "ux_users_email",
                table: "users",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_game_platforms");

            migrationBuilder.DropTable(
                name: "listing_completions");

            migrationBuilder.DropTable(
                name: "oauth_providers");

            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "user_platforms");

            migrationBuilder.DropTable(
                name: "user_ratings");

            migrationBuilder.DropTable(
                name: "library_entries");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.DropTable(
                name: "catalog_games");

            migrationBuilder.DropTable(
                name: "catalog_platforms");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
