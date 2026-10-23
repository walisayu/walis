using System.Security.Cryptography;
using System.Text;
using Walis.Core.Models;

namespace Walis.Api.Services;

/// <summary>The shared contract for the browser session cookie and the double-submit CSRF token.</summary>
public static class SessionCookieService
{
    private const int CsrfPartBytes = 32;
    private const int CsrfPartCharacters = CsrfPartBytes * 2;
    public const int CsrfTokenLength = CsrfPartCharacters * 2 + 1;
    public const string SessionCookieName = "walis_session";
    public const string CsrfCookieName = "walis_csrf";
    public const string CsrfHeaderName = "X-Walis-CSRF";

    public static void Issue(HttpContext context, AuthSession session)
    {
        var expires = new DateTimeOffset(DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc));
        var secure = ShouldUseSecure(context);
        var common = new CookieOptions
        {
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
            IsEssential = true
        };

        context.Response.Cookies.Append(
            SessionCookieName,
            session.Token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = common.Secure,
                SameSite = common.SameSite,
                Path = common.Path,
                Expires = common.Expires,
                IsEssential = common.IsEssential
            });

        context.Response.Cookies.Append(
            CsrfCookieName,
            CreateCsrfToken(session.Token),
            new CookieOptions
            {
                HttpOnly = false,
                Secure = common.Secure,
                SameSite = common.SameSite,
                Path = common.Path,
                Expires = common.Expires,
                IsEssential = common.IsEssential
            });
    }

    public static void Clear(HttpContext context)
    {
        var options = new CookieOptions
        {
            Secure = ShouldUseSecure(context),
            SameSite = SameSiteMode.Strict,
            Path = "/"
        };
        context.Response.Cookies.Delete(SessionCookieName, options);
        context.Response.Cookies.Delete(CsrfCookieName, options);
    }

    public static bool HasValidCsrf(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(SessionCookieName, out var sessionToken) ||
            !request.Cookies.TryGetValue(CsrfCookieName, out var cookie) ||
            !request.Headers.TryGetValue(CsrfHeaderName, out var presented) ||
            string.IsNullOrEmpty(sessionToken) || sessionToken.Length > 256 ||
            cookie.Length != CsrfTokenLength || presented.Count != 1 ||
            presented[0]?.Length != CsrfTokenLength)
            return false;

        // Keep the double-submit header==cookie condition first, then verify the cookie was really signed from the current HttpOnly
        // session token. Fixed lengths avoid arbitrary allocation; constant-time comparison avoids per-character leaks.
        Span<byte> cookieBytes = stackalloc byte[CsrfTokenLength];
        Span<byte> presentedBytes = stackalloc byte[CsrfTokenLength];
        Encoding.ASCII.GetBytes(cookie, cookieBytes);
        Encoding.ASCII.GetBytes(presented[0]!, presentedBytes);

        return CryptographicOperations.FixedTimeEquals(cookieBytes, presentedBytes) &&
               IsCsrfTokenBoundToSession(sessionToken, cookie);
    }

    public static string CreateCsrfToken(string sessionToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(CsrfPartBytes);
        var signature = SignCsrfNonce(sessionToken, nonce);

        try
        {
            return
                $"{Convert.ToHexString(nonce).ToLowerInvariant()}.{Convert.ToHexString(signature).ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public static bool IsCsrfTokenBoundToSession(string sessionToken, string token)
    {
        if (string.IsNullOrEmpty(sessionToken) || sessionToken.Length > 256 ||
            token.Length != CsrfTokenLength || token[CsrfPartCharacters] != '.')
            return false;

        Span<byte> nonce = stackalloc byte[CsrfPartBytes];
        Span<byte> presentedSignature = stackalloc byte[CsrfPartBytes];
        if (!TryDecodeHex(token.AsSpan(0, CsrfPartCharacters), nonce) ||
            !TryDecodeHex(token.AsSpan(CsrfPartCharacters + 1), presentedSignature))
            return false;

        var expectedSignature = SignCsrfNonce(sessionToken, nonce);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedSignature, presentedSignature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedSignature);
        }
    }

    private static byte[] SignCsrfNonce(string sessionToken, ReadOnlySpan<byte> nonce)
    {
        var key = Encoding.UTF8.GetBytes($"walis:csrf\0{sessionToken}");
        try
        {
            return HMACSHA256.HashData(key, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool TryDecodeHex(ReadOnlySpan<char> value, Span<byte> destination)
    {
        if (value.Length != destination.Length * 2) return false;

        for (var index = 0; index < destination.Length; index++)
        {
            var high = HexValue(value[index * 2]);
            var low = HexValue(value[index * 2 + 1]);
            if (high < 0 || low < 0) return false;

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static int HexValue(char value)
    {
        return value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1
        };
    }

    /// <summary>
    ///     The Secure flag follows the transport the browser really used: a fresh install is reachable over plain HTTP, and a browser
    ///     drops a Secure cookie it received that way, which would make sign-in impossible. Once the deployment requires HTTPS
    ///     (Security:RequireHttps), or declares that TLS terminates in front of it (WALIS_SECURE_COOKIES=true or
    ///     Security:ForceSecureCookies), every cookie is Secure.
    /// </summary>
    public static bool ShouldUseSecure(HttpContext context)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var configured = configuration["WALIS_SECURE_COOKIES"] ??
                         configuration["Security:ForceSecureCookies"];

        if (bool.TryParse(configured, out var forced) && forced) return true;

        return context.Request.IsHttps || RequiresHttps(configuration);
    }

    /// <summary>
    ///     Whether the deployment turned on Security:RequireHttps. Off by default so the first connection from another machine
    ///     works; with it on, a remote request that did not arrive over TLS is refused and cookies are always Secure.
    /// </summary>
    public static bool RequiresHttps(IConfiguration configuration)
    {
        return bool.TryParse(configuration["Security:RequireHttps"], out var required) && required;
    }

    /// <summary>Whether the API knows the external transport is protected by TLS; shared by transport protection and HSTS.</summary>
    public static bool IsExternallySecure(HttpContext context)
    {
        if (context.Request.IsHttps) return true;

        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var configured = configuration["WALIS_SECURE_COOKIES"] ??
                         configuration["Security:ForceSecureCookies"];
        return bool.TryParse(configured, out var forced) && forced;
    }
}