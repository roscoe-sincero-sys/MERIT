using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Merit.Web.Infrastructure;

/// <summary>
/// htmx 4 request and response helpers. One action, two shapes: htmx sends <c>HX-Request-Type: partial</c> for a
/// targeted swap, and the action returns the partial view for it, or the full view with layout otherwise, from the same
/// view model.
/// </summary>
public static class Htmx
{
    public static Models.FormDraft SubmittedDraft(this Controller controller, string form)
        => new(form, controller.Request.Form.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase));

    public static bool IsHtmx(this HttpRequest request) => request.Headers["HX-Request"] == "true";

    public static bool IsPartial(this HttpRequest request)
        => request.IsHtmx() && string.Equals(request.Headers["HX-Request-Type"], "partial", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns <paramref name="partialName"/> for an htmx partial request, else <paramref name="fullView"/>.</summary>
    public static IActionResult PageOrPartial(this Controller controller, string fullView, string partialName, object model)
        => controller.Request.IsPartial() ? controller.PartialView(partialName, model) : controller.View(fullView, model);

    /// <summary>A failed validation: the form partial re-rendered with its errors, status 422, swapped in place.</summary>
    public static IActionResult Invalid(this Controller controller, string partialName, object model,
                                        Application.Errors.ValidationException? ex = null)
    {
        if (ex is not null)
        {
            controller.ModelState.AddErrors(ex);
        }

        var result = controller.PartialView(partialName, model);
        result.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return result;
    }

    /// <summary>Announces text through the page's aria-live region after the swap (handled in merit.js).</summary>
    public static void Announce(this HttpResponse response, string message)
        => Trigger(response, "merit:announce", message);

    /// <summary>Asks the page to refresh a region that listens for the event (hx-trigger="name from:body").</summary>
    public static void Refresh(this HttpResponse response, params string[] events)
    {
        foreach (var e in events)
        {
            Trigger(response, e, string.Empty);
        }
    }

    private static void Trigger(HttpResponse response, string name, string detail)
    {
        var existing = response.Headers["HX-Trigger"].ToString();
        var dict = string.IsNullOrEmpty(existing)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(existing) ?? [];
        dict[name] = detail;
        response.Headers["HX-Trigger"] = JsonSerializer.Serialize(dict);
    }

    public static string FirstError(this ModelStateDictionary state, string key)
        => state.TryGetValue(key, out var entry) && entry.Errors.Count > 0 ? entry.Errors[0].ErrorMessage : string.Empty;
}

/// <summary>
/// Per-request Content Security Policy nonce, shared by script tags, Angular (ngCspNonce) and hx-nonce. Base64url, not
/// base64: Razor writes "+" in an attribute as "&amp;#x2B;", and hx-csp matches the header's nonce against the raw text
/// of a partial response, so a nonce with "+" stripped every htmx element in about one response in three (issue #53).
/// Base64url's letters, digits, "-" and "_" pass through HTML encoding unchanged and are valid in a CSP nonce-source.
/// </summary>
public sealed class CspNonce
{
    public string Value { get; } = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
}

/// <summary>
/// Security headers on every response. script-src 'self' with a per-request nonce and no unsafe-inline or unsafe-eval;
/// htmx runs under it through the hx-csp extension, which reads the nonce from this header on each partial response.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CspNonce nonce, IAntiforgery antiforgery)
    {
        var h = context.Response.Headers;
        h.ContentSecurityPolicy =
            $"default-src 'self'; script-src 'self' 'nonce-{nonce.Value}'; style-src 'self' 'nonce-{nonce.Value}'; " +
            "img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; " +
            "form-action 'self'; frame-ancestors 'none'";
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "same-origin";
        h.XFrameOptions = "DENY";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h.CacheControl = context.Request.Path.StartsWithSegments("/lib", StringComparison.OrdinalIgnoreCase)
            ? h.CacheControl
            : "no-store";

        if ((await context.AuthenticateAsync(AuthSchemes.Session)).Properties?.ExpiresUtc is { } expires)
        {
            // A conservative deadline from the current ticket, rather than assuming every read renews the cookie.
            h["X-Merit-Session-Expires"] = expires.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Both clients send the antiforgery token in X-XSRF-TOKEN: Angular reads it from this cookie through its
        // built-in XSRF support, and merit.js does the same for htmx requests.
        if (HttpMethods.IsGet(context.Request.Method) && context.User.Identity?.IsAuthenticated == true
            && !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
            {
                HttpOnly = false,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
            });
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Stamps hx-nonce on every element that carries an htmx attribute, as the hx-csp extension requires: an htmx element
/// without the page's nonce has its htmx attributes stripped, so injected markup cannot issue requests.
/// </summary>
[HtmlTargetElement("*", Attributes = "hx-get")]
[HtmlTargetElement("*", Attributes = "hx-post")]
[HtmlTargetElement("*", Attributes = "hx-put")]
[HtmlTargetElement("*", Attributes = "hx-delete")]
[HtmlTargetElement("*", Attributes = "hx-trigger")]
[HtmlTargetElement("*", Attributes = "hx-target")]
[HtmlTargetElement("*", Attributes = "hx-swap")]
[HtmlTargetElement("*", Attributes = "hx-boost")]
[HtmlTargetElement("hx-partial")]
public sealed class HxNonceTagHelper(CspNonce nonce) : TagHelper
{
    public override int Order => int.MaxValue;

    public override void Process(TagHelperContext context, TagHelperOutput output)
        => output.Attributes.SetAttribute("hx-nonce", nonce.Value);
}

/// <summary>Adds the request's CSP nonce to script and style tags that ask for it with nonce="".</summary>
[HtmlTargetElement("script", Attributes = "merit-nonce")]
[HtmlTargetElement("style", Attributes = "merit-nonce")]
[HtmlTargetElement("link", Attributes = "merit-nonce")]
public sealed class NonceTagHelper(CspNonce nonce) : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("merit-nonce");
        output.Attributes.SetAttribute("nonce", nonce.Value);
    }
}
