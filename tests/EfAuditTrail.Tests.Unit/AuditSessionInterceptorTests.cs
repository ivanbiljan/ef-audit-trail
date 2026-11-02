using EfAuditTrail.Data.Auditing.Session;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfAuditTrail.Tests.Unit;

public sealed class AuditSessionInterceptorTests
{
    [Theory]
    [InlineData(CommandSource.SaveChanges, SessionDelivery.InStatements)]
    [InlineData(CommandSource.ExecuteUpdate, SessionDelivery.LeadingStatement)]
    [InlineData(CommandSource.ExecuteDelete, SessionDelivery.LeadingStatement)]
    [InlineData(CommandSource.ExecuteSqlRaw, SessionDelivery.SeparateCommand)]
    [InlineData(CommandSource.Unknown, SessionDelivery.SeparateCommand)]
    [InlineData(CommandSource.LinqQuery, SessionDelivery.None)]
    [InlineData(CommandSource.Migrations, SessionDelivery.None)]
    internal void Each_kind_of_command_gets_the_cheapest_safe_delivery(CommandSource source, SessionDelivery delivery)
        => AuditSessionInterceptor.Classify(source).ShouldBe(delivery);

    [Fact]
    public void Every_update_and_delete_of_a_batch_publishes_the_context()
    {
        var sql = AuditSessionInterceptor.AddPublishClause(
            """
            UPDATE products SET name = @p0
            WHERE id = @p1;
            DELETE FROM orders
            WHERE id = @p2;
            """);

        sql.Split("WHERE audit.publish(").Length.ShouldBe(3);
        sql.ShouldContain("AND id = @p1;");
        sql.ShouldContain("AND id = @p2;");
    }

    [Fact]
    public void An_insert_only_command_is_left_alone()
    {
        const string Sql = "INSERT INTO companies (name)\nVALUES (@p0)\nRETURNING id;";

        AuditSessionInterceptor.AddPublishClause(Sql).ShouldBeSameAs(Sql);
    }

    [Fact]
    public void A_write_in_an_unexpected_shape_fails_instead_of_losing_its_context()
        => Should.Throw<InvalidOperationException>(() =>
            AuditSessionInterceptor.AddPublishClause("UPDATE products SET name = @p0 WHERE id = @p1;"));
}
