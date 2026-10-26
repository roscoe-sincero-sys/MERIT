using Merit.Application;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Data.SqlServer;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// ---------------------------------------------------------------------------------------------- application layers
services.AddHttpContextAccessor();
services.AddScoped<ICurrentSession, WebCurrentSession>();
services.AddScoped<CspNonce>();
services.AddMeritApplication();
services.AddMeritSqlServer(o =>
{
    o.ConnectionString = builder.Configuration.GetConnectionString("Merit")
                         ?? throw new InvalidOperationException("ConnectionStrings:Merit is not configured.");
});
services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
services.AddSingleton<IBreachedPasswordList, CommonBreachedPasswordList>();
services.AddSingleton<AngularAssets>();
services.AddSingleton<QrCodes>();
services.AddScoped<Merit.Web.Mapping.PersonPageBuilder>();

// Data protection keys live where every web copy can read them, so any copy accepts any session cookie.
string keyPath = builder.Configuration["DataProtection:KeyPath"] is { Length: > 0 } configured
    ? configured
    : Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
services.AddDataProtection().SetApplicationName("MERIT").PersistKeysToFileSystem(new DirectoryInfo(keyPath));

// ---------------------------------------------------------------------------------------------- authentication
int idleMinutes = builder.Configuration.GetValue("Session:IdleTimeoutMinutes", 20);
services.AddAuthentication(AuthSchemes.Session)
    .AddCookie(AuthSchemes.Session, o =>
    {
        o.Cookie.Name = "__Host-merit";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(idleMinutes);
        o.SlidingExpiration = true;
        o.LoginPath = "/account/signin";
        o.LogoutPath = "/account/signout";
        o.AccessDeniedPath = "/account/denied";
        o.Events = new CookieAuthenticationEvents
        {
            // An expired session on an htmx request returns HX-Redirect to sign-in; on the API, 401.
            OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
                else if (ctx.Request.IsHtmx())
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.Headers["HX-Redirect"] = "/account/signin";
                }
                else
                {
                    ctx.Response.Redirect(ctx.RedirectUri);
                }

                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                if (!ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) && !ctx.Request.IsHtmx())
                {
                    ctx.Response.Redirect(ctx.RedirectUri);
                }

                return Task.CompletedTask;
            },
            // Re-check the session scope every five minutes: sign-out, an assignment change or an MFA reset ends it.
            OnValidatePrincipal = async ctx =>
            {
                var issued = ctx.Properties.IssuedUtc ?? DateTimeOffset.MinValue;
                if (DateTimeOffset.UtcNow - issued < TimeSpan.FromMinutes(5))
                {
                    return;
                }

                var scope = ctx.Principal?.FindFirst(MeritClaims.ScopeKey)?.Value;
                var stamp = ctx.Principal?.FindFirst(MeritClaims.SecurityStamp)?.Value;
                var account = ctx.HttpContext.RequestServices.GetRequiredService<AccountService>();
                if (!Guid.TryParse(scope, out var s) || !Guid.TryParse(stamp, out var st) || !await account.IsSessionValidAsync(s, st))
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(AuthSchemes.Session);
                }
                else
                {
                    ctx.ShouldRenew = true;
                }
            },
        };
    })
    .AddCookie(AuthSchemes.PendingMfa, o =>
    {
        o.Cookie.Name = "__Host-merit-mfa";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        o.SlidingExpiration = false;
        o.LoginPath = "/account/signin";
    });

// ---------------------------------------------------------------------------------------------- authorization
services.AddAuthorization(o =>
{
    // Every action carries a permission policy; anything without one still requires a signed-in user.
    o.FallbackPolicy = new AuthorizationPolicyBuilder(AuthSchemes.Session).RequireAuthenticatedUser().Build();
    o.AddPolicy(Permissions.SignedIn, p => p.AddAuthenticationSchemes(AuthSchemes.Session).RequireAuthenticatedUser());
    foreach (var permission in Permissions.All)
    {
        o.AddPolicy(permission, p => p.AddAuthenticationSchemes(AuthSchemes.Session).RequireAuthenticatedUser()
            .RequireClaim(MeritClaims.Permission, permission));
    }
});

// ---------------------------------------------------------------------------------------------- MVC, API, antiforgery
services.AddAntiforgery(o =>
{
    o.HeaderName = "X-XSRF-TOKEN";
    o.Cookie.Name = "__Host-merit-af";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
});
services.AddControllersWithViews(o =>
{
    o.Filters.Add<MeritExceptionFilter>();
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});
services.AddProblemDetails();
services.AddOpenApi("v1", o => o.ShouldInclude = d => d.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true);
services.AddHealthChecks();
services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("signin", w =>
    {
        w.PermitLimit = builder.Configuration.GetValue("Merit:SignInAttemptsPerMinute", 10);
        w.Window = TimeSpan.FromMinutes(1);
    });
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/home/error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// The Angular build under wwwroot/app is also served straight from disk, so a client rebuild (npm run build) is picked up
// without rebuilding the server's static asset manifest. It runs before routing because the /app/{**path} host route
// would otherwise claim the bundle URLs. The bundles hold no data; every API call they make is authorized.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Directory.CreateDirectory(Path.Combine(app.Environment.WebRootPath, "app")).FullName),
    RequestPath = "/app",
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseAuthorization();

// Stylesheets, scripts, fonts and icons hold no data, and the sign-in page needs them before anyone is signed in.
app.MapStaticAssets().AllowAnonymous();
app.MapOpenApi("/openapi/{documentName}.json").RequireAuthorization(Permissions.SignedIn);
app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();

app.Run();

/// <summary>Entry point type, visible to WebApplicationFactory in integration tests.</summary>
public partial class Program
{
}
