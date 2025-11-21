using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsonDemo.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Age = table.Column<int>(type: "INTEGER", nullable: false),
                    Gender = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Bio = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    SexualOrientation = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Likes",
                columns: table => new
                {
                    FromUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsLike = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsMatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Likes", x => new { x.FromUserId, x.ToUserId });
                    table.ForeignKey(
                        name: "FK_Likes_Users_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Likes_Users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MusicProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    FavoriteGenres = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FavoriteArtists = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    FavoriteSongs = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicProfiles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserImages_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSuggestionQueues",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SuggestedUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompatibilityScore = table.Column<double>(type: "REAL", nullable: false),
                    QueuePosition = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSuggestionQueues", x => new { x.UserId, x.SuggestedUserId });
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
                name: "IX_Likes_FromUserId_ToUserId",
                table: "Likes",
                columns: new[] { "FromUserId", "ToUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Likes_ToUserId",
                table: "Likes",
                column: "ToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicProfiles_UserId",
                table: "MusicProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserImages_UserId",
                table: "UserImages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSuggestionQueues_SuggestedUserId",
                table: "UserSuggestionQueues",
                column: "SuggestedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSuggestionQueues_UserId_CompatibilityScore",
                table: "UserSuggestionQueues",
                columns: new[] { "UserId", "CompatibilityScore" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSuggestionQueues_UserId_QueuePosition",
                table: "UserSuggestionQueues",
                columns: new[] { "UserId", "QueuePosition" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Likes");

            migrationBuilder.DropTable(
                name: "MusicProfiles");

            migrationBuilder.DropTable(
                name: "UserImages");

            migrationBuilder.DropTable(
                name: "UserSuggestionQueues");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
