using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailArchive.Core.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicatesRemoved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DuplicatesRemoved",
                table: "Runs",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DuplicatesRemoved",
                table: "Runs");
        }
    }
}
