using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailArchive.Core.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    KeyType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    GmailUid = table.Column<long>(type: "bigint", nullable: true),
                    TrashUid = table.Column<long>(type: "bigint", nullable: true),
                    InternalDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FromAddress = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    IsSent = table.Column<bool>(type: "bit", nullable: false),
                    SourceSize = table.Column<long>(type: "bigint", nullable: true),
                    TargetSize = table.Column<long>(type: "bigint", nullable: true),
                    TargetFolder = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    TargetUid = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CopiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TrashedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PurgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CutoffDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Selected = table.Column<int>(type: "int", nullable: false),
                    Copied = table.Column<int>(type: "int", nullable: false),
                    AlreadyInTarget = table.Column<int>(type: "int", nullable: false),
                    Verified = table.Column<int>(type: "int", nullable: false),
                    Trashed = table.Column<int>(type: "int", nullable: false),
                    Purged = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    ExitCode = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Key",
                table: "Messages",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_State",
                table: "Messages",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Runs");
        }
    }
}
