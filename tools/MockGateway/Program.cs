using System.Net;
using System.Text;
using MockGateway;

// A stand-in payment gateway for class and local runs. It speaks the small protocol Payment's gateway
// client uses (POST /api/pages, POST /api/refunds, both repeatable by key), hosts a pay page with
// Success and Fail buttons, and sends signed results to the Webhook Receiver or straight to the queue.
// It is not part of the product: no spec describes it.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<GatewayState>();
builder.Services.AddHttpClient<WebhookSender>();
WebApplication app = builder.Build();

string apiKey = app.Configuration["GatewayApiKey"] ?? "test";
string publicBaseUrl = (app.Configuration["PublicBaseUrl"] ?? "http://localhost:5099").TrimEnd('/');

app.MapPost("/api/pages", (HttpRequest request, PageRequest body, GatewayState state) =>
{
    if (!IsAuthorised(request, apiKey))
    {
        return Results.Unauthorized();
    }

    PaymentPage page = state.GetOrCreatePage(body.BookingId, body.Amount, body.ReturnUrl);
    return Results.Ok(new { gatewayRef = page.GatewayRef, checkoutUrl = $"{publicBaseUrl}/pay/{page.GatewayRef}" });
});

app.MapPost("/api/refunds", (HttpRequest request, RefundRequest body, GatewayState state) =>
{
    if (!IsAuthorised(request, apiKey))
    {
        return Results.Unauthorized();
    }

    if (state.IsRefundFailing)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new { gatewayRefundId = state.GetOrCreateRefund(body.RefundKey) });
});

// Classroom switch: make refunds fail, then work again ("the gateway fails once" demo).
app.MapPost("/admin/refunds-failing/{isFailing:bool}", (bool isFailing, GatewayState state) =>
{
    state.IsRefundFailing = isFailing;
    return Results.Ok(new { isRefundFailing = isFailing });
});

app.MapGet("/pay/{gatewayRef}", (string gatewayRef, GatewayState state, WebhookSender sender) =>
{
    PaymentPage? page = state.FindPage(gatewayRef);
    if (page is null)
    {
        return Results.NotFound("No such payment page.");
    }

    return Results.Content(PayPage.Render(page, sender.Destination), "text/html", Encoding.UTF8);
});

app.MapPost("/pay/{gatewayRef}/result", async (string gatewayRef, HttpRequest request, GatewayState state, WebhookSender sender, CancellationToken cancellationToken) =>
{
    PaymentPage? page = state.FindPage(gatewayRef);
    if (page is null)
    {
        return Results.NotFound("No such payment page.");
    }

    IFormCollection form = await request.ReadFormAsync(cancellationToken);
    string outcome = form["outcome"].ToString();
    string? reason = null;
    if (outcome == "Failed")
    {
        reason = "Card declined";
    }

    var report = new WebhookReport("evt_" + Guid.NewGuid().ToString("N"), page.GatewayRef, outcome, page.Amount, reason, TimeProvider.System.GetUtcNow());
    page.LastReport = report;

    // "after" sends the result a few seconds after the customer is back on the site, which is what
    // makes the return page wait ("Confirming your payment") rather than assume.
    if (form["timing"].ToString() == "after")
    {
        _ = SendLaterAsync(sender, report);
    }
    else
    {
        await sender.SendAsync(report, cancellationToken);
    }

    return Results.Redirect(page.ReturnUrl, permanent: false);
});

app.MapPost("/pay/{gatewayRef}/replay", async (string gatewayRef, int? times, GatewayState state, WebhookSender sender, CancellationToken cancellationToken) =>
{
    PaymentPage? page = state.FindPage(gatewayRef);
    if (page?.LastReport is null)
    {
        return Results.NotFound("Nothing has been sent for this page yet.");
    }

    int count = times ?? 5;
    for (int i = 0; i < count; i++)
    {
        await sender.SendAsync(page.LastReport, cancellationToken);
    }

    return Results.Ok(new { replayed = count, gatewayEventId = page.LastReport.GatewayEventId });
});

app.MapGet("/health", () => Results.Ok("healthy"));
app.Run();

static bool IsAuthorised(HttpRequest request, string apiKey)
{
    return request.Headers["X-Api-Key"].ToString() == apiKey;
}

static async Task SendLaterAsync(WebhookSender sender, WebhookReport report)
{
    await Task.Delay(TimeSpan.FromSeconds(5));
    await sender.SendAsync(report, CancellationToken.None);
}

/// <summary>The body Payment sends to prepare a payment page.</summary>
public sealed record PageRequest(string BookingId, long Amount, string ReturnUrl);

/// <summary>The body Payment sends to return money.</summary>
public sealed record RefundRequest(string RefundKey, string GatewayRef, long Amount);

/// <summary>The hosted pay page: Success and Fail buttons, when to send the result, and Replay.</summary>
public static class PayPage
{
    /// <summary>Renders the page for one payment page, escaping everything it shows.</summary>
    public static string Render(PaymentPage page, string destination)
    {
        string reference = WebUtility.HtmlEncode(page.GatewayRef);
        string amount = page.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>MockGateway</title>
            <style>body{font-family:system-ui;max-width:32rem;margin:3rem auto;padding:0 1rem}button{font-size:1rem;padding:.6rem 1.2rem;margin:.3rem}</style></head>
            <body>
            <h1>MockGateway</h1>
            <p>Pay <strong>INR {{amount}}</strong> for booking <code>{{WebUtility.HtmlEncode(page.BookingId)}}</code>.</p>
            <p>Results go to: <code>{{WebUtility.HtmlEncode(destination)}}</code></p>
            <form method="post" action="/pay/{{reference}}/result">
              <p><label><input type="radio" name="timing" value="before" checked> Send the result, then take me back</label><br>
                 <label><input type="radio" name="timing" value="after"> Take me back, send the result 5 seconds later</label></p>
              <button name="outcome" value="Succeeded">Success</button>
              <button name="outcome" value="Failed">Fail</button>
            </form>
            <form method="post" action="/pay/{{reference}}/replay?times=5"><button>Replay last result x5</button></form>
            </body></html>
            """;
    }
}
