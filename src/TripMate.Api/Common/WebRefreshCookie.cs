namespace TripMate.Api.Common;

public static class WebRefreshCookie
{
    public const string Name = "tripmate_refresh";
    // The Next.js BFF consumes this HttpOnly cookie on same-site /api routes,
    // not only on the Backend's refresh route. A narrow auth-only path prevents
    // it reaching those BFF routes and leaves an authenticated user stranded.
    public const string Path = "/";
    private const string LegacyPath = "/api/v1/auth";

    public static void Append(
        HttpContext context,
        IWebHostEnvironment environment,
        string refreshToken,
        DateTimeOffset expiresAt,
        bool keepMeSignedIn)
    {
        // Expire the old, narrower cookie during the migration. Cookies with
        // identical names but different paths can otherwise be sent together.
        Expire(context, environment, LegacyPath);
        context.Response.Cookies.Append(Name, refreshToken, CreateOptions(
            context, environment, Path, keepMeSignedIn ? expiresAt : null));
    }

    public static void Delete(
        HttpContext context,
        IWebHostEnvironment environment)
    {
        Expire(context, environment, Path);
        Expire(context, environment, LegacyPath);
    }

    private static void Expire(HttpContext context, IWebHostEnvironment environment, string path)
    {
        var options = CreateOptions(context, environment, path, DateTimeOffset.UnixEpoch);
        options.MaxAge = TimeSpan.Zero;
        context.Response.Cookies.Append(Name, string.Empty, options);
    }

    private static CookieOptions CreateOptions(
        HttpContext context,
        IWebHostEnvironment environment,
        string path,
        DateTimeOffset? expires) => new()
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps
            || !environment.IsDevelopment()
            || !context.Request.Host.Host.Equals(
                "localhost",
                StringComparison.OrdinalIgnoreCase),
            SameSite = SameSiteMode.Lax,
            Path = path,
            Expires = expires
        };
}