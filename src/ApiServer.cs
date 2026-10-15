using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DisplaySwitcher;

public sealed record LayoutRequest(string? Layout);

internal static class ApiServer
{
    public static WebApplication Build(Settings settings, LayoutSwitcher switcher, Action<SwitchResult> onApiSwitch)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{settings.Listen.Host}:{settings.Listen.Port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        var app = builder.Build();

        // WHY: without a Host check, a web page can DNS-rebind its own domain to this PC
        // (or 127.0.0.1) and call the API as same-origin, bypassing the browser's CORS
        // protection. Rebinding needs a domain name in Host, so only IP literals, localhost,
        // this PC's own names and explicitly configured names are accepted.
        var allowedHosts = AllowedHostNames(settings);
        app.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.Host.Trim('[', ']');
            if (host.Length == 0 || IPAddress.TryParse(host, out _) || allowedHosts.Contains(host))
            {
                await next();
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, status = "hostNotAllowed", message = $"Host '{host}' is not allowed; add it to listen.allowedHosts" });
        });

        if (!string.IsNullOrEmpty(settings.Token))
        {
            var expected = Encoding.UTF8.GetBytes("Bearer " + settings.Token);
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }
                var actual = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { ok = false, status = "unauthorized" });
                    return;
                }
                await next();
            });
        }

        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        app.MapGet("/status", () => Results.Ok(switcher.Status()));

        app.MapGet("/audio-devices", () => Results.Ok(AudioManager.ListActive()));

        // What a snapshot would contain right now; for checking the window filter. Off by
        // default: it lists every window title (chats, documents, browser tabs).
        if (settings.Listen.WindowsEndpoint)
            app.MapGet("/windows", () => Results.Ok(WindowManager.Capture()));

        app.MapPost("/layout", async (LayoutRequest? req) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Layout))
                return BadRequest();
            // Off the request thread: a switch blocks for several seconds while displays settle.
            var result = await Task.Run(() => switcher.Switch(req.Layout));
            onApiSwitch(result);
            return ToHttp(result);
        });

        app.MapPost("/learn", async (LayoutRequest? req) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Layout))
                return BadRequest();
            return ToHttp(await Task.Run(() => switcher.Learn(req.Layout)));
        });

        // Body optional: {"layout": "default"}; defaults to the current layout.
        app.MapPost("/restore", async (LayoutRequest? req) =>
            ToHttp(await Task.Run(() => switcher.RestoreSaved(req?.Layout))));

        app.MapPost("/validate", async (LayoutRequest? req) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Layout))
                return BadRequest();
            return ToHttp(await Task.Run(() => switcher.Validate(req.Layout)));
        });

        return app;
    }

    static HashSet<string> AllowedHostNames(Settings settings)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", Environment.MachineName, Environment.MachineName + ".local" };
        try
        {
            var ip = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            names.Add(ip.HostName);
            if (!string.IsNullOrEmpty(ip.DomainName))
                names.Add($"{ip.HostName}.{ip.DomainName}");
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }
        foreach (var h in settings.Listen.AllowedHosts)
        {
            if (!string.IsNullOrWhiteSpace(h))
                names.Add(h.Trim());
        }
        return names;
    }

    static IResult BadRequest() =>
        Results.Json(new { ok = false, status = "invalidRequest", message = "Body must be {\"layout\": \"<name>\"}" },
            statusCode: StatusCodes.Status400BadRequest);

    static IResult ToHttp(SwitchResult r)
    {
        int code = r.Status switch
        {
            SwitchStatus.Switched or SwitchStatus.Learned or SwitchStatus.AlreadyActive => StatusCodes.Status200OK,
            SwitchStatus.InvalidRequest => StatusCodes.Status400BadRequest,
            SwitchStatus.UnknownLayout => StatusCodes.Status404NotFound,
            SwitchStatus.DisplayUnavailable => StatusCodes.Status409Conflict,
            SwitchStatus.Busy => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status500InternalServerError,
        };
        return Results.Json(new
        {
            ok = r.Ok,
            status = r.Status,
            changed = r.Status == SwitchStatus.Switched,
            // Exception text can contain local paths; the log has the detail.
            message = r.Status == SwitchStatus.Failed ? "Internal error; see the Display Switcher log" : r.Message,
            from = r.From,
            layout = r.Layout,
            warnings = r.Warnings ?? [],
            monitors = r.Monitors,
            audio = r.Audio,
        }, statusCode: code);
    }
}
