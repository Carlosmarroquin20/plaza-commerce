using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plaza.Orders.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Orders placed before this migration get the default lifetime instead of expiring at once.
            migrationBuilder.Sql("UPDATE orders SET expires_at = created_at + interval '15 minutes'");

            migrationBuilder.CreateIndex(
                name: "ix_orders_expires_at",
                table: "orders",
                column: "expires_at",
                filter: "status IN ('Pending', 'AwaitingPayment')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_expires_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "orders");
        }
    }
}
