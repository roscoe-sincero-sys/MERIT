using System.Text.Encodings.Web;
using Merit.Web.Infrastructure;

namespace Merit.Tests;

/// <summary>Issue #53: the nonce is written into hx-nonce attributes by Razor and matched verbatim by hx-csp.</summary>
public sealed class CspNonceTests
{
    [Fact]
    public void TheNonceIsUnchangedByHtmlEncoding()
    {
        // 18 random bytes: a base64 nonce had a "+" or "/" in most draws of a few hundred.
        for (int i = 0; i < 500; i++)
        {
            string nonce = new CspNonce().Value;
            Assert.Matches("^[A-Za-z0-9_-]{24}$", nonce);
            Assert.Equal(nonce, HtmlEncoder.Default.Encode(nonce));
        }
    }

    [Fact]
    public void EachRequestGetsItsOwnNonce() => Assert.NotEqual(new CspNonce().Value, new CspNonce().Value);
}
