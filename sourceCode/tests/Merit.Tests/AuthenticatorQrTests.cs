using System.Xml.Linq;
using Merit.Application.Security;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;

namespace Merit.Tests;

public sealed class AuthenticatorQrTests
{
    [Theory]
    [InlineData("region3demo")]
    [InlineData("analyst+maryland@example.test")]
    public void EnrollmentQrHasAScalableViewportAndQuietZone(string account)
    {
        var start = Merit.Application.Services.AccountService.BeginEnrollment(account);
        var uri = new Uri(start.ProvisioningUri);
        Assert.Equal("otpauth", uri.Scheme);
        Assert.Equal("totp", uri.Host);
        Assert.Equal("/MERIT:" + account, Uri.UnescapeDataString(uri.AbsolutePath));
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(start.SecretBase32, query["secret"].ToString());
        Assert.Equal("MERIT", query["issuer"].ToString());
        Assert.Equal("SHA1", query["algorithm"].ToString());
        Assert.Equal("6", query["digits"].ToString());
        Assert.Equal("30", query["period"].ToString());
        Assert.Equal(20, Totp.FromBase32(start.SecretBase32).Length);

        var svg = XDocument.Parse(new QrCodes().Svg(start.ProvisioningUri)).Root!;
        var bounds = svg.Attribute("viewBox")!.Value.Split(' ').Select(int.Parse).ToArray();
        Assert.Equal([0, 0], bounds.Take(2));
        Assert.Equal(bounds[2], bounds[3]);
        // Background spans the full viewport; all dark modules leave four 5px modules clear on every side.
        var rectangles = svg.Elements().ToList();
        Assert.Equal("#ffffff", rectangles[0].Attribute("fill")!.Value);
        Assert.Equal(bounds[2], (int)rectangles[0].Attribute("width")!);
        foreach (var rectangle in rectangles.Skip(1))
        {
            int x = (int)rectangle.Attribute("x")!, y = (int)rectangle.Attribute("y")!;
            Assert.True(x >= 20 && y >= 20);
            Assert.True(x + (int)rectangle.Attribute("width")! <= bounds[2] - 20);
            Assert.True(y + (int)rectangle.Attribute("height")! <= bounds[3] - 20);
        }
    }
}
