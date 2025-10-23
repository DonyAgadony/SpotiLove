using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsonDemo.Migrations
{
    /// <inheritdoc />
    public partial class UpdateDatabaseSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_UserSuggestionQueues_UserId_CompatibilityScore",
                table: "UserSuggestionQueues",
                columns: new[] { "UserId", "CompatibilityScore" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Id",
                table: "Users",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Likes_FromUserId",
                table: "Likes",
                column: "FromUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSuggestionQueues_UserId_CompatibilityScore",
                table: "UserSuggestionQueues");

            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Id",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Likes_FromUserId",
                table: "Likes");
        }
    }
}
