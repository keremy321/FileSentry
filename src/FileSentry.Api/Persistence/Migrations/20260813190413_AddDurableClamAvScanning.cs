using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileSentry.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableClamAvScanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveScanAttemptId",
                table: "FileRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectionName",
                table: "FileRecords",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastScanFailureCode",
                table: "FileRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastScannerVersion",
                table: "FileRecords",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextScanAttemptAtUtc",
                table: "FileRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanAttemptCount",
                table: "FileRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScanCompletedAtUtc",
                table: "FileRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScanningStartedAtUtc",
                table: "FileRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageState",
                table: "FileRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Quarantine");

            migrationBuilder.CreateTable(
                name: "ScanAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsRetryable = table.Column<bool>(type: "boolean", nullable: false),
                    DetectionName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ScannerVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanAttempts_FileRecords_FileRecordId",
                        column: x => x.FileRecordId,
                        principalTable: "FileRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileRecords_Status_NextScanAttemptAtUtc",
                table: "FileRecords",
                columns: new[] { "Status", "NextScanAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FileRecords_Status_ScanningStartedAtUtc",
                table: "FileRecords",
                columns: new[] { "Status", "ScanningStartedAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileRecords_ScanAttemptCount_NonNegative",
                table: "FileRecords",
                sql: "\"ScanAttemptCount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_ScanAttempts_CompletedAtUtc",
                table: "ScanAttempts",
                column: "CompletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ScanAttempts_FileRecordId_AttemptNumber",
                table: "ScanAttempts",
                columns: new[] { "FileRecordId", "AttemptNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanAttempts");

            migrationBuilder.DropIndex(
                name: "IX_FileRecords_Status_NextScanAttemptAtUtc",
                table: "FileRecords");

            migrationBuilder.DropIndex(
                name: "IX_FileRecords_Status_ScanningStartedAtUtc",
                table: "FileRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FileRecords_ScanAttemptCount_NonNegative",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "ActiveScanAttemptId",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "DetectionName",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "LastScanFailureCode",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "LastScannerVersion",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "NextScanAttemptAtUtc",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "ScanAttemptCount",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "ScanCompletedAtUtc",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "ScanningStartedAtUtc",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "StorageState",
                table: "FileRecords");
        }
    }
}
