using System.Net;
using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Tests.Notifications;

public class MailOpsPageTests : IClassFixture<AppFactory>
{
    private const string Url = "/Ops/Mail";
    private readonly AppFactory _f;
    public MailOpsPageTests(AppFactory f) => _f = f;

    private Task<NotificationOutbox> FailedRowAsync(string recipient) => _f.WithDbAsync(async db =>
    {
        var row = await NotificationSeed.OutboxRowAsync(db, recipient, _f.Clock.UtcNow);
        row.Status = NotificationStatus.Failed;
        row.Attempts = 8;
        row.LastError = "SmtpCommandException: 451 try later";
        row.LastAttemptAt = _f.Clock.UtcNow;
        await db.SaveChangesAsync();
        return row;
    });

    [Fact]
    public async Task It_sees_queue_status_and_failed_rows()
    {
        await FailedRowAsync("ops.view@example.test");

        var res = await _f.CreateClientAs(TestUser.It).GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("ops.view@example.test", html);
        Assert.Contains("451 try later", html);
        Assert.Contains("Requeue", html);
    }

    [Theory]
    [InlineData("Manager")]
    [InlineData("Admin")]
    [InlineData("Nobody")]
    public async Task Non_it_users_are_forbidden(string userKey)
    {
        var user = userKey switch { "Manager" => TestUser.Manager, "Admin" => TestUser.Admin, _ => TestUser.Nobody };
        var client = _f.CreateClientAs(user);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Url)).StatusCode);
        var post = await MailTestHelpers.PostFormAsync(client, Url + "?handler=RequeueAll", null);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Nav_link_is_shown_to_it_only()
    {
        Assert.Contains("href=\"/Ops/Mail\"", await _f.CreateClientAs(TestUser.It).GetStringAsync("/"));
        Assert.DoesNotContain("href=\"/Ops/Mail\"", await _f.CreateClientAs(TestUser.Manager).GetStringAsync("/"));
    }

    [Fact]
    public async Task It_can_requeue_a_failed_row_with_antiforgery()
    {
        var row = await FailedRowAsync("ops.requeue@example.test");
        var client = _f.CreateClientAs(TestUser.It);
        var token = await MailTestHelpers.GetAntiforgeryTokenAsync(client, Url);

        var res = await MailTestHelpers.PostFormAsync(client, $"{Url}?handler=Requeue&id={row.Id}", token);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var after = await _f.WithDbAsync(db => db.Outbox.AsNoTracking().SingleAsync(x => x.Id == row.Id));
        Assert.Equal(NotificationStatus.Pending, after.Status);
        Assert.Equal(0, after.Attempts);
    }

    [Fact]
    public async Task It_can_requeue_all_failed_rows()
    {
        var row = await FailedRowAsync("ops.all@example.test");
        var client = _f.CreateClientAs(TestUser.It);
        var token = await MailTestHelpers.GetAntiforgeryTokenAsync(client, Url);

        var res = await MailTestHelpers.PostFormAsync(client, $"{Url}?handler=RequeueAll", token);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var after = await _f.WithDbAsync(db => db.Outbox.AsNoTracking().SingleAsync(x => x.Id == row.Id));
        Assert.Equal(NotificationStatus.Pending, after.Status);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_rejected()
    {
        var row = await FailedRowAsync("ops.csrf@example.test");

        var res = await MailTestHelpers.PostFormAsync(_f.CreateClientAs(TestUser.It), $"{Url}?handler=Requeue&id={row.Id}", null);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var after = await _f.WithDbAsync(db => db.Outbox.AsNoTracking().SingleAsync(x => x.Id == row.Id));
        Assert.Equal(NotificationStatus.Failed, after.Status);
    }
}
