using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsonDemo.Migrations
{
    /// <inheritdoc />
    public partial class ModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Likes_Users_FromUserId",
                table: "Likes");

            migrationBuilder.DropForeignKey(
                name: "FK_Likes_Users_ToUserId",
                table: "Likes");

            migrationBuilder.DropForeignKey(
                name: "FK_UserSuggestionQueues_Users_SuggestedUserId",
                table: "UserSuggestionQueues");

            migrationBuilder.DropIndex(
                name: "IX_UserSuggestionQueues_UserId_CompatibilityScore",
                table: "UserSuggestionQueues");

            migrationBuilder.DropIndex(
                name: "IX_UserSuggestionQueues_UserId_QueuePosition",
                table: "UserSuggestionQueues");

            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Likes_FromUserId_ToUserId",
                table: "Likes");

            migrationBuilder.AlterColumn<int>(
                name: "SuggestedUserId",
                table: "UserSuggestionQueues",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .OldAnnotation("Relational:ColumnOrder", 1);

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "UserSuggestionQueues",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .OldAnnotation("Relational:ColumnOrder", 0);

            migrationBuilder.AlterColumn<int>(
                name: "ToUserId",
                table: "Likes",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .OldAnnotation("Relational:ColumnOrder", 1);

            migrationBuilder.AlterColumn<int>(
                name: "FromUserId",
                table: "Likes",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .OldAnnotation("Relational:ColumnOrder", 0);

            migrationBuilder.AddForeignKey(
                name: "FK_Likes_Users_FromUserId",
                table: "Likes",
                column: "FromUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Likes_Users_ToUserId",
                table: "Likes",
                column: "ToUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UserSuggestionQueues_Users_SuggestedUserId",
                table: "UserSuggestionQueues",
                column: "SuggestedUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Likes_Users_FromUserId",
                table: "Likes");

            migrationBuilder.DropForeignKey(
                name: "FK_Likes_Users_ToUserId",
                table: "Likes");

            migrationBuilder.DropForeignKey(
                name: "FK_UserSuggestionQueues_Users_SuggestedUserId",
                table: "UserSuggestionQueues");

            migrationBuilder.AlterColumn<int>(
                name: "SuggestedUserId",
                table: "UserSuggestionQueues",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .Annotation("Relational:ColumnOrder", 1);

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "UserSuggestionQueues",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .Annotation("Relational:ColumnOrder", 0);

            migrationBuilder.AlterColumn<int>(
                name: "ToUserId",
                table: "Likes",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .Annotation("Relational:ColumnOrder", 1);

            migrationBuilder.AlterColumn<int>(
                name: "FromUserId",
                table: "Likes",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .Annotation("Relational:ColumnOrder", 0);

            migrationBuilder.CreateIndex(
                name: "IX_UserSuggestionQueues_UserId_CompatibilityScore",
                table: "UserSuggestionQueues",
                columns: new[] { "UserId", "CompatibilityScore" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSuggestionQueues_UserId_QueuePosition",
                table: "UserSuggestionQueues",
                columns: new[] { "UserId", "QueuePosition" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Likes_FromUserId_ToUserId",
                table: "Likes",
                columns: new[] { "FromUserId", "ToUserId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Likes_Users_FromUserId",
                table: "Likes",
                column: "FromUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Likes_Users_ToUserId",
                table: "Likes",
                column: "ToUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UserSuggestionQueues_Users_SuggestedUserId",
                table: "UserSuggestionQueues",
                column: "SuggestedUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
