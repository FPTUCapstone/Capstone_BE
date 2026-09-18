namespace TripMate.Api.Common;

public static class WebRefreshCookie
{
    public const string Name = "tripmate_refresh";
    public const string Path = "/api/v1/auth";

    public static void Append(
        HttpContext context,
        IWebHostEnvironment environment,
        string refreshToken,
        DateTimeOffset expiresAt,
        bool keepMeSignedIn)
    {
        context.Response.Cookies.Append(Name, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps
                || !environment.IsDevelopment()
                || !context.Request.Host.Host.Equals(
                    "localhost",
                    StringComparison.OrdinalIgnoreCase),
            SameSite = SameSiteMode.Lax,
            Path = Path,
            Expires = keepMeSignedIn ? expiresAt : null
        });
    }

    public static void Delete(
        HttpContext context,
        IWebHostEnvironment environment)
    {
        context.Response.Cookies.Append(Name, string.Empty, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps
                || !environment.IsDevelopment()
                || !context.Request.Host.Host.Equals(
                    "localhost",
                    StringComparison.OrdinalIgnoreCase),
            SameSite = SameSiteMode.Lax,
            Path = Path,
            MaxAge = TimeSpan.Zero,
            Expires = DateTimeOffset.UnixEpoch
        });
    }
}