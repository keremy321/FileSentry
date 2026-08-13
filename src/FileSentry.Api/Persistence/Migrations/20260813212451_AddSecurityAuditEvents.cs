using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileSentry.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "FileRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreviousStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    NewStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.CheckConstraint("CK_AuditEvents_DurationMilliseconds_NonNegative", "\"DurationMilliseconds\" IS NULL OR \"DurationMilliseconds\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorUserId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "ActorUserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CorrelationId",
                table: "AuditEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_FileRecordId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "FileRecordId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAtUtc",
                table: "AuditEvents",
                column: "OccurredAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "FileRecords");
        }
    }
}
