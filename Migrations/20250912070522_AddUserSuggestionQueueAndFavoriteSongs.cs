using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsonDemo.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSuggestionQueueAndFavoriteSongs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FavoriteSongs",
                table: "MusicProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "UserSuggestionQueues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SuggestedUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuePosition = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompatibilityScore = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSuggestionQueues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSuggestionQueues_Users_SuggestedUserId",
                        column: x => x.SuggestedUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserSuggestionQueues_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSuggestionQueues_SuggestedUserId",
                table: "UserSuggestionQueues",
                column: "SuggestedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSuggestionQueues_UserId_QueuePosition",
                table: "UserSuggestionQueues",
                columns: new[] { "UserId", "QueuePosition" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSuggestionQueues_UserId_SuggestedUserId",
                table: "UserSuggestionQueues",
                columns: new[] { "UserId", "SuggestedUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSuggestionQueues");

            migrationBuilder.DropColumn(
                name: "FavoriteSongs",
                table: "MusicProfiles");
        }
    }
}
