using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileSentry.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileAuthorizationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                table: "FileRecords",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "FileRecords");
        }
    }
}
