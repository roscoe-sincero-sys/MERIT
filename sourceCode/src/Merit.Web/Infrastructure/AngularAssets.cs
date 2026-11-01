using System.Text.RegularExpressions;
using QRCoder;

namespace Merit.Web.Infrastructure;

/// <summary>
/// Reads the Angular build's index.html once to learn its fingerprinted script and stylesheet names, so the Razor host
/// view can emit them with the request's CSP nonce. The build is wwwroot/app (see ClientApp/angular.json).
/// </summary>
public sealed partial class AngularAssets(IWebHostEnvironment env)
{
    private readonly object gate = new();
    private (DateTime Stamp, IReadOnlyList<string> Scripts, IReadOnlyList<string> Styles) assets = (DateTime.MinValue, [], []);

    public bool IsBuilt => Current.Scripts.Count > 0;

    public IReadOnlyList<string> Scripts => Current.Scripts;

    public IReadOnlyList<string> Styles => Current.Styles;

    /// <summary>Re-reads index.html only when the build has written a new one, so a client rebuild needs no restart.</summary>
    private (DateTime Stamp, IReadOnlyList<string> Scripts, IReadOnlyList<string> Styles) Current
    {
        get
        {
            var index = Path.Combine(env.WebRootPath, "app", "index.html");
            var stamp = File.Exists(index) ? File.GetLastWriteTimeUtc(index) : DateTime.MinValue;
            lock (gate)
            {
                if (stamp != assets.Stamp)
                {
                    var (scripts, styles) = Load(index);
                    assets = (stamp, scripts, styles);
                }

                return assets;
            }
        }
    }

    private static (IReadOnlyList<string>, IReadOnlyList<string>) Load(string index)
    {
        if (!File.Exists(index))
        {
            return ([], []);
        }

        string html = File.ReadAllText(index);
        var scripts = ScriptSrc().Matches(html).Select(m => "/app/" + m.Groups[1].Value.TrimStart('/')).ToList();
        var styles = StyleHref().Matches(html).Select(m => "/app/" + m.Groups[1].Value.TrimStart('/')).ToList();
        return (scripts, styles);
    }

    [GeneratedRegex("<script[^>]+src=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSrc();

    [GeneratedRegex("<link[^>]+rel=\"stylesheet\"[^>]+href=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex StyleHref();
}

/// <summary>QR codes for authenticator enrollment, rendered as inline SVG markup (no image request, no script).</summary>
public sealed class QrCodes
{
    public string Svg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var svg = new SvgQRCode(data);
        // CSS resizes the enrollment image. Without a viewBox the browser clips the modules
        // to that size instead of scaling them, leaving an unreadable QR code (issue #141).
        return svg.GetGraphic(5, "#000000", "#ffffff", drawQuietZones: true,
            sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
