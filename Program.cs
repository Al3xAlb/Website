using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using System.Threading.RateLimiting;
using SkiaSharp;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(
    builder.WebHost.GetSetting("urls") ?? "http://0.0.0.0:8080");
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
var uploadDirectory = Path.Combine(dataDirectory, "uploads");
var photoCacheDirectory = Path.Combine(dataDirectory, "photo-cache");
var localPhotosDirectory = Path.Combine(builder.Environment.ContentRootPath, "photos");
Directory.CreateDirectory(dataDirectory);
Directory.CreateDirectory(uploadDirectory);
Directory.CreateDirectory(photoCacheDirectory);
Directory.CreateDirectory(localPhotosDirectory);

var galleryStore = new GalleryStore(Path.Combine(dataDirectory, "galleries.json"));
var localPhotoStore = new LocalPhotoStore(localPhotosDirectory);
var credentialStore = new CredentialStore(Path.Combine(dataDirectory, "admin.json"));
var eventKeyStore = new PrivateGalleryKeyStore(Path.Combine(dataDirectory, "private-gallery-keys.json"));
var settingsStore = new SettingsStore(Path.Combine(dataDirectory, "settings.json"));
var legacyEventKeyPath = Path.Combine(dataDirectory, "event-key.json");
await MigrateUploadedImagesAsync(Path.Combine(builder.Environment.WebRootPath ?? "wwwroot", "uploads"), uploadDirectory);
await galleryStore.EnsureSeededAsync();
await credentialStore.InitializeAsync(
    Environment.GetEnvironmentVariable("ADMIN_EMAIL"),
    Environment.GetEnvironmentVariable("ADMIN_PASSWORD"));
await eventKeyStore.InitializeAsync();
await settingsStore.InitializeAsync();

builder.Services.AddHttpClient();
builder.Services.AddSingleton(galleryStore);
builder.Services.AddSingleton(localPhotoStore);
builder.Services.AddSingleton(credentialStore);
builder.Services.AddSingleton(eventKeyStore);
builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton<PhotoCacheService>(serviceProvider => new PhotoCacheService(
    [uploadDirectory, localPhotosDirectory],
    photoCacheDirectory,
    serviceProvider.GetRequiredService<ILogger<PhotoCacheService>>()));
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PhotoCacheService>());
builder.Services.AddSingleton<GeminiService>(serviceProvider => new GeminiService(
    serviceProvider.GetRequiredService<IHttpClientFactory>(),
    settingsStore,
    photoCacheDirectory,
    uploadDirectory,
    localPhotosDirectory,
    serviceProvider.GetRequiredService<ILogger<GeminiService>>()));
builder.Services.AddSingleton<GoogleDriveService>(serviceProvider => new GoogleDriveService(
    serviceProvider.GetRequiredService<IHttpClientFactory>(),
    settingsStore,
    uploadDirectory,
    serviceProvider.GetRequiredService<PhotoCacheService>(),
    serviceProvider.GetRequiredService<ILogger<GoogleDriveService>>()));
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 8,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("event-key", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 8,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "photo-gallery.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthentication()
    .AddCookie(EventAccessDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "photo-gallery.private-events";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = context =>
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<PrivateGalleryKeyStore>();
            var principal = context.Principal;
            var validClaims = principal?.Claims
                .Where(claim => claim.Type == EventAccessDefaults.GalleryAccessClaim)
                .Where(claim => EventAccessDefaults.TryParseGalleryAccess(claim.Value, out var galleryId, out var version) &&
                    store.IsCurrentVersion(galleryId, version))
                .ToArray() ?? [];
            var activeClaim = validClaims.LastOrDefault();
            if (activeClaim is null || principal?.Identity is not ClaimsIdentity identity)
            {
                context.RejectPrincipal();
            }
            else if (validClaims.Length != 1 ||
                principal.Claims.Count(claim => claim.Type == EventAccessDefaults.GalleryAccessClaim) != 1)
            {
                var claims = principal.Claims
                    .Where(claim => claim.Type != EventAccessDefaults.GalleryAccessClaim)
                    .Append(activeClaim);
                context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(
                    claims,
                    identity.AuthenticationType,
                    identity.NameClaimType,
                    identity.RoleClaimType)));
            }
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
if (File.Exists(legacyEventKeyPath))
{
    app.Logger.LogWarning(
        "Ignoring the legacy shared event access key. Set a separate access key for each private gallery in the studio.");
}
app.UseDefaultFiles();
app.UseResponseCompression();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var extension = Path.GetExtension(context.File.Name);
        context.Context.Response.Headers.CacheControl = extension is ".js" or ".css"
            ? "public,max-age=0,must-revalidate"
            : "no-cache";
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAntiforgery();
app.UseAuthorization();

app.MapGet("/admin", () => Results.File(
    Path.Combine(builder.Environment.WebRootPath ?? "wwwroot", "admin.html"),
    "text/html"));
app.MapGet("/events", () => Results.File(
    Path.Combine(builder.Environment.WebRootPath ?? "wwwroot", "events.html"),
    "text/html"));

app.MapGet("/api/galleries", async (
    GalleryStore store,
    LocalPhotoStore localPhotos,
    CancellationToken cancellationToken) =>
{
    return Results.Ok((await store.GetAllAsync(cancellationToken))
        .Concat(localPhotos.GetGalleries())
        .Where(gallery => !gallery.IsPrivate));
});

app.MapGet("/api/admin/galleries", async (
    GalleryStore store,
    PrivateGalleryKeyStore keys,
    CancellationToken cancellationToken) =>
{
    var galleries = await store.GetAllAsync(cancellationToken);
    return Results.Ok(galleries.Select(gallery => new
    {
        gallery.Id,
        gallery.Title,
        gallery.Category,
        gallery.Location,
        gallery.Date,
        gallery.CoverImage,
        gallery.Images,
        gallery.IsEvent,
        gallery.IsPrivate,
        gallery.Description,
        accessKeyConfigured = keys.IsConfigured(gallery.Id),
        shareLinkConfigured = keys.HasShareLink(gallery.Id)
    }));
}).RequireAuthorization();

app.MapGet("/api/events", async (
    GalleryStore store,
    CancellationToken cancellationToken) =>
{
    var galleries = (await store.GetAllAsync(cancellationToken)).Where(gallery => gallery.IsEvent);
    return Results.Ok(galleries.Where(gallery => !gallery.IsPrivate));
});

app.MapGet("/api/events/private", async (
    HttpContext context,
    GalleryStore store,
    CancellationToken cancellationToken) =>
{
    var galleries = (await store.GetAllAsync(cancellationToken)).Where(gallery => gallery.IsEvent && gallery.IsPrivate);
    var access = await context.AuthenticateAsync(EventAccessDefaults.AuthenticationScheme);
    var unlockedGalleryIds = EventAccessDefaults.GetUnlockedGalleryIds(access);
    return Results.Ok(galleries.Where(gallery => access.Succeeded && unlockedGalleryIds.Contains(gallery.Id)));
});

app.MapGet("/api/events/session", async (
    HttpContext context,
    IAntiforgery antiforgery,
    GalleryStore galleries,
    PrivateGalleryKeyStore store,
    CancellationToken cancellationToken) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    var access = await context.AuthenticateAsync(EventAccessDefaults.AuthenticationScheme);
    var privateGalleries = (await galleries.GetAllAsync(cancellationToken))
        .Where(gallery => gallery.IsPrivate);
    return Results.Ok(new
    {
        configured = privateGalleries.Any(gallery => store.IsConfigured(gallery.Id)),
        unlockedGalleryIds = EventAccessDefaults.GetUnlockedGalleryIds(access),
        token = tokens.RequestToken
    });
});

app.MapPost("/api/events/unlock", async (
    EventUnlockRequest request,
    HttpContext context,
    PrivateGalleryKeyStore store,
    GalleryStore galleries,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(new { error = "Refresh the page and try again." });
    var privateGalleryIds = (await galleries.GetAllAsync(cancellationToken))
        .Where(gallery => gallery.IsPrivate)
        .Select(gallery => gallery.Id)
        .ToHashSet();
    if (!privateGalleryIds.Any(store.IsConfigured))
        return Results.Problem("Private event access has not been configured yet.", statusCode: StatusCodes.Status503ServiceUnavailable);
    var matchedKey = request.ShareToken is not null
        ? store.VerifyShareToken(request.ShareToken)
        : await store.VerifyAndGetAccessAsync(request.Key, cancellationToken);
    if (matchedKey is null || !privateGalleryIds.Contains(matchedKey.GalleryId))
        return Results.Unauthorized();

    var currentAccess = await context.AuthenticateAsync(EventAccessDefaults.AuthenticationScheme);
    var claims = currentAccess.Succeeded
        ? currentAccess.Principal!.Claims
            .Where(claim => claim.Type != EventAccessDefaults.GalleryAccessClaim)
            .ToList()
        : [new Claim(ClaimTypes.Name, "private-event-guest")];
    claims.Add(new Claim(
        EventAccessDefaults.GalleryAccessClaim,
        EventAccessDefaults.FormatGalleryAccess(matchedKey.GalleryId, matchedKey.Version)));
    await context.SignInAsync(
        EventAccessDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            EventAccessDefaults.AuthenticationScheme)));
    return Results.Ok(new { unlocked = true, galleryId = matchedKey.GalleryId });
}).RequireRateLimiting("event-key");

app.MapPost("/api/admin/galleries/{id:guid}/share-link", async (
    Guid id,
    GalleryStore galleries,
    PrivateGalleryKeyStore keys,
    HttpContext context,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(new { error = "Refresh the page and try again." });

    var gallery = (await galleries.GetAllAsync(cancellationToken))
        .FirstOrDefault(item => item.Id == id && item.IsEvent && item.IsPrivate);
    if (gallery is null || !keys.IsConfigured(id))
        return Results.NotFound();

    var token = await keys.CreateShareTokenAsync(id, cancellationToken);
    var shareUrl = new UriBuilder(context.Request.Scheme, context.Request.Host.Host)
    {
        Port = context.Request.Host.Port ?? -1,
        Path = "/events",
        Fragment = $"share={token}"
    };
    return Results.Ok(new { url = shareUrl.Uri.ToString() });
}).RequireAuthorization();

app.MapDelete("/api/admin/galleries/{id:guid}/share-link", async (
    Guid id,
    GalleryStore galleries,
    PrivateGalleryKeyStore keys,
    HttpContext context,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(new { error = "Refresh the page and try again." });

    var gallery = (await galleries.GetAllAsync(cancellationToken))
        .FirstOrDefault(item => item.Id == id && item.IsEvent && item.IsPrivate);
    if (gallery is null)
        return Results.NotFound();

    await keys.RemoveShareTokenAsync(id, cancellationToken);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/events/lock", async (HttpContext context) =>
{
    await context.SignOutAsync(EventAccessDefaults.AuthenticationScheme);
    return Results.NoContent();
});

app.MapGet("/uploads/{fileName}", async (
    string fileName,
    HttpContext context,
    HttpRequest request,
    GalleryStore store,
    PrivateGalleryKeyStore keys,
    CancellationToken cancellationToken) =>
{
    if (Path.GetFileName(fileName) != fileName ||
        !Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _))
        return Results.NotFound();

    var imageUrl = $"/uploads/{fileName}";
    var referenced = await store.FindReferencingAsync(imageUrl, cancellationToken);
    if (referenced.Length == 0)
        return Results.NotFound();

    var privateAccess = await context.AuthenticateAsync(EventAccessDefaults.AuthenticationScheme);
    var privateGalleryIds = referenced.Where(gallery => gallery.IsPrivate).Select(gallery => gallery.Id).ToArray();
    if (privateGalleryIds.Length > 0)
    {
        context.Response.Headers.CacheControl = "private,no-store";
        var unlockedGalleryIds = EventAccessDefaults.GetUnlockedGalleryIds(privateAccess);
        if (!privateGalleryIds.Any(id => keys.IsConfigured(id) && unlockedGalleryIds.Contains(id)))
            return Results.NotFound();
    }
    else
    {
        context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
    }

    var imagePath = Path.Combine(uploadDirectory, fileName);
    if (!File.Exists(imagePath))
        return Results.NotFound();

    var cachedPath = await PhotoCache.TryGetCachedPathAsync(
        imagePath, request.Query["width"].ToString(), photoCacheDirectory, cancellationToken);

    var chosenPath = cachedPath ?? imagePath;
    var contentType = cachedPath is not null
        ? "image/webp"
        : GetImageContentType(Path.GetExtension(fileName));

    var fileInfo = new FileInfo(chosenPath);
    var lastModified = fileInfo.LastWriteTimeUtc;
    var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{fileInfo.Length:x}-{lastModified.Ticks:x}\"");

    return Results.File(
        chosenPath,
        contentType,
        lastModified: lastModified,
        entityTag: etag,
        enableRangeProcessing: true);
});

app.MapGet("/local-photos/{folderName}/{fileName}", async (
    string folderName,
    string fileName,
    HttpContext context,
    HttpRequest request,
    LocalPhotoStore localPhotos,
    CancellationToken cancellationToken) =>
{
    var imagePath = localPhotos.GetImagePath(folderName, fileName);
    if (imagePath is null)
        return Results.NotFound();

    context.Response.Headers.CacheControl = "public,max-age=86400,must-revalidate";

    var cachedPath = await PhotoCache.TryGetCachedPathAsync(
        imagePath, request.Query["width"].ToString(), photoCacheDirectory, cancellationToken);

    var chosenPath = cachedPath ?? imagePath;
    var contentType = cachedPath is not null
        ? "image/webp"
        : GetImageContentType(Path.GetExtension(fileName));

    var fileInfo = new FileInfo(chosenPath);
    var lastModified = fileInfo.LastWriteTimeUtc;
    var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{fileInfo.Length:x}-{lastModified.Ticks:x}\"");

    return Results.File(
        chosenPath,
        contentType,
        lastModified: lastModified,
        entityTag: etag,
        enableRangeProcessing: true);
});

app.MapGet("/api/auth/session", (HttpContext context) =>
    Results.Ok(new { authenticated = context.User.Identity?.IsAuthenticated == true }));

app.MapGet("/api/auth/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { token = tokens.RequestToken });
}).RequireAuthorization();

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    HttpContext context,
    CredentialStore store,
    CancellationToken cancellationToken) =>
{
    var email = request.Email?.Trim();
    var password = request.Password;
    if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password) ||
        !await store.VerifyAsync(email, password, cancellationToken))
        return Results.Unauthorized();

    var claims = new[] { new Claim(ClaimTypes.Name, email) };
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("login");

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/uploads", async (
    HttpRequest request,
    PhotoCacheService photoCache,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(request.HttpContext))
        return Results.BadRequest(new { error = "Refresh the page and try uploading again." });
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Choose an image to upload." });

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Choose an image to upload." });
    if (file.Length > 20 * 1024 * 1024)
        return Results.BadRequest(new { error = "Images must be 20 MB or smaller." });

    var extension = await DetectImageExtensionAsync(file, cancellationToken);
    if (extension is null)
        return Results.BadRequest(new { error = "Use a valid JPEG, PNG, or WebP image." });

    var fileName = $"{Guid.NewGuid():N}{extension}";
    var destination = Path.Combine(uploadDirectory, fileName);
    await using (var output = new FileStream(
        destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
    await using (var input = file.OpenReadStream())
    {
        await input.CopyToAsync(output, cancellationToken);
    }

    photoCache.QueuePhoto(destination);

    return Results.Ok(new { url = $"/uploads/{fileName}" });
})
    .RequireAuthorization()
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(21 * 1024 * 1024));

app.MapPost("/api/admin/galleries", async (
    GalleryInput input,
    GalleryStore store,
    PrivateGalleryKeyStore keys,
    HttpContext context,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(new { error = "Refresh the page and try again." });
    if (!TryCreateGallery(input, out var gallery, out var error))
        return Results.BadRequest(new { error });

    var created = gallery with { Id = Guid.NewGuid() };
    if (created.IsPrivate)
    {
        var result = await keys.SetAsync(created.Id, input.AccessKey, cancellationToken);
        if (result != KeySetResult.Saved)
            return Results.BadRequest(new { error = GetKeySetError(result) });
    }
    await store.SaveAsync(current => current.Append(created).ToArray(), cancellationToken);
    return Results.Created($"/api/galleries/{created.Id}", created);
}).RequireAuthorization();

app.MapPut("/api/admin/galleries/{id:guid}", async (
    Guid id,
    GalleryInput input,
    GalleryStore store,
    PrivateGalleryKeyStore keys,
    HttpContext context,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(new { error = "Refresh the page and try again." });
    if (!TryCreateGallery(input, out var gallery, out var error))
        return Results.BadRequest(new { error });

    var current = await store.GetAllAsync(cancellationToken);
    if (!current.Any(item => item.Id == id))
        return Results.NotFound();

    var updated = gallery with { Id = id };
    if (updated.IsPrivate)
    {
        if (!string.IsNullOrEmpty(input.AccessKey))
        {
            var result = await keys.SetAsync(id, input.AccessKey, cancellationToken);
            if (result != KeySetResult.Saved)
                return Results.BadRequest(new { error = GetKeySetError(result) });
        }
        else if (!keys.IsConfigured(id))
        {
            return Results.BadRequest(new { error = "Set an access key before making this gallery private." });
        }
    }
    await store.SaveAsync(items => items.Select(item => item.Id == id ? updated : item).ToArray(), cancellationToken);
    if (!updated.IsPrivate)
        await keys.RemoveAsync(id, cancellationToken);
    return Results.Ok(updated);
}).RequireAuthorization();

app.MapDelete("/api/admin/galleries/{id:guid}", async (
    Guid id,
    GalleryStore store,
    PrivateGalleryKeyStore keys,
    HttpContext context,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(new { error = "Refresh the page and try again." });
    var current = await store.GetAllAsync(cancellationToken);
    if (!current.Any(item => item.Id == id))
        return Results.NotFound();

    await store.SaveAsync(items => items.Where(item => item.Id != id).ToArray(), cancellationToken);
    await keys.RemoveAsync(id, cancellationToken);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/admin/settings", (SettingsStore settings) =>
{
    return Results.Ok(new
    {
        hasGeminiKey = !string.IsNullOrWhiteSpace(settings.GetGeminiApiKey()),
        hasGoogleDriveKey = !string.IsNullOrWhiteSpace(settings.GetGoogleDriveApiKey()),
        geminiKeyMasked = MaskKey(settings.GetGeminiApiKey()),
        googleDriveKeyMasked = MaskKey(settings.GetGoogleDriveApiKey())
    });
}).RequireAuthorization();

app.MapPost("/api/admin/settings", async (
    SettingsUpdateRequest request,
    SettingsStore settings,
    HttpContext context,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(new { error = "Refresh the page and try again." });

    var current = settings.GetSettings();
    var newGemini = request.GeminiApiKey is not null ? request.GeminiApiKey.Trim() : current.GeminiApiKey;
    var newDrive = request.GoogleDriveApiKey is not null ? request.GoogleDriveApiKey.Trim() : current.GoogleDriveApiKey;

    await settings.SaveAsync(new AppSettings(
        string.IsNullOrEmpty(newGemini) ? null : newGemini,
        string.IsNullOrEmpty(newDrive) ? null : newDrive), cancellationToken);

    return Results.Ok(new { saved = true });
}).RequireAuthorization();

app.MapPost("/api/admin/gemini/suggest", async (
    GeminiSuggestRequest request,
    GeminiService gemini,
    HttpContext context,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(new { error = "Refresh the page and try again." });

    if (request.Images is null || request.Images.Length == 0)
        return Results.BadRequest(new { error = "Add or upload at least one photograph to analyze." });

    try
    {
        var suggestion = await gemini.SuggestGalleryDetailsAsync(request.Images, request.Hint, cancellationToken);
        if (suggestion is null)
            return Results.Problem("Gemini could not generate suggestions. Please try again.", statusCode: StatusCodes.Status502BadGateway);

        return Results.Ok(suggestion);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Gemini analysis failed: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization();

app.MapPost("/api/admin/gdrive/import", async (
    GDriveImportRequest request,
    GoogleDriveService gdrive,
    GeminiService gemini,
    HttpContext context,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(new { error = "Refresh the page and try again." });

    if (string.IsNullOrWhiteSpace(request.Url))
        return Results.BadRequest(new { error = "Provide a valid Google Drive folder or file link." });

    try
    {
        var result = await gdrive.ImportFromDriveAsync(request.Url, cancellationToken);
        GeminiSuggestionResult? aiSuggestion = null;

        if (request.AutoDescribe)
        {
            try
            {
                aiSuggestion = await gemini.SuggestGalleryDetailsAsync(
                    result.Images, result.FolderTitle, cancellationToken);
            }
            catch
            {
                // Non-fatal if Gemini API key not yet set
            }
        }

        return Results.Ok(new
        {
            images = result.Images,
            title = aiSuggestion?.Title ?? result.FolderTitle ?? "New Event Story",
            category = aiSuggestion?.Category ?? "Gatherings",
            location = aiSuggestion?.Location ?? "",
            date = aiSuggestion?.Date ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
            description = aiSuggestion?.Description ?? "",
            isEvent = request.IsEvent
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to import from Google Drive: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization();

app.MapFallbackToFile("index.html");
app.Run();

static string? MaskKey(string? key) =>
    string.IsNullOrEmpty(key) ? null : key.Length > 8 ? $"{key[..4]}...{key[^4..]}" : "****";

static async Task MigrateUploadedImagesAsync(string legacyPath, string uploadDirectory)
{
    if (!Directory.Exists(legacyPath))
        return;

    foreach (var file in Directory.EnumerateFiles(legacyPath))
    {
        var destination = Path.Combine(uploadDirectory, Path.GetFileName(file));
        if (File.Exists(destination))
            throw new IOException($"Cannot migrate uploaded image because the destination already exists: {destination}");
        File.Move(file, destination);
    }

    if (!Directory.EnumerateFileSystemEntries(legacyPath).Any())
        Directory.Delete(legacyPath);
}

static bool TryCreateGallery(GalleryInput input, out Gallery gallery, out string error)
{
    var title = input.Title?.Trim() ?? "";
    var category = input.Category?.Trim() ?? "";
    var location = input.Location?.Trim() ?? "";
    var description = input.Description?.Trim();
    if (string.IsNullOrWhiteSpace(description)) description = null;
    var images = (input.Images ?? [])
        .Select(image => image?.Trim() ?? "")
        .Where(image => image.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .Take(81)
        .ToArray();

    if (title.Length is < 1 or > 100)
        return Fail("Give this gallery a title (up to 100 characters).", out gallery, out error);
    if (category.Length is < 1 or > 60)
        return Fail("Add a category (up to 60 characters).", out gallery, out error);
    if (location.Length > 100)
        return Fail("Location must be 100 characters or fewer.", out gallery, out error);
    if (description is not null && description.Length > 2000)
        return Fail("Description must be 2000 characters or fewer.", out gallery, out error);
    if (input.IsPrivate && !input.IsEvent)
        return Fail("Private galleries must also be marked as event galleries.", out gallery, out error);
    if (images.Length is < 1 or > 80 || images.Any(image => !IsAllowedImageUrl(image)))
        return Fail("Add between 1 and 80 valid image URLs.", out gallery, out error);
    if (input.IsPrivate && images.Any(image => !image.StartsWith("/uploads/", StringComparison.Ordinal)))
        return Fail("Private galleries can only use photos uploaded to this site.", out gallery, out error);

    var date = input.Date?.Trim() ?? "";
    if (!DateOnly.TryParse(date, out _))
        return Fail("Enter a valid gallery date.", out gallery, out error);

    gallery = new Gallery(Guid.Empty, title, category, location, date, images[0], images, input.IsEvent, input.IsPrivate, description);
    error = "";
    return true;
}

static bool Fail(string message, out Gallery gallery, out string error)
{
    gallery = default!;
    error = message;
    return false;
}

static bool IsAllowedImageUrl(string image)
{
    if (image.StartsWith("/uploads/", StringComparison.Ordinal) &&
        !image.Contains("..", StringComparison.Ordinal) &&
        image.Length <= 200)
        return true;

    return Uri.TryCreate(image, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        image.Length <= 2_000;
}

static async Task<string?> DetectImageExtensionAsync(IFormFile file, CancellationToken cancellationToken)
{
    var header = new byte[12];
    await using var stream = file.OpenReadStream();
    var count = await stream.ReadAsync(header, cancellationToken);

    if (count >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        return ".jpg";
    if (count >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        return ".png";
    if (count >= 12 &&
        header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
        header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        return ".webp";

    return null;
}

static string GetImageContentType(string extension) => extension.ToLowerInvariant() switch
{
    ".jpg" or ".jpeg" => "image/jpeg",
    ".png" => "image/png",
    ".webp" => "image/webp",
    _ => "application/octet-stream"
};

static string GetKeySetError(KeySetResult result) => result switch
{
    KeySetResult.Invalid => "Use exactly 8 letters or numbers for the access key.",
    KeySetResult.Duplicate => "Each private gallery must have a different access key.",
    _ => "The access key could not be saved."
};

record LoginRequest(string? Email, string? Password);
record EventUnlockRequest(string? Key, string? ShareToken);
record GalleryInput(
    string? Title,
    string? Category,
    string? Location,
    string? Date,
    string[]? Images,
    bool IsEvent = false,
    bool IsPrivate = false,
    string? AccessKey = null,
    string? Description = null);
record Gallery(
    Guid Id,
    string Title,
    string Category,
    string Location,
    string Date,
    string CoverImage,
    string[] Images,
    bool IsEvent = false,
    bool IsPrivate = false,
    string? Description = null);

sealed class LocalPhotoStore : IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };
    private readonly string _directory;
    private readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly object _gate = new();
    private Gallery[]? _galleries;
    private readonly FileSystemWatcher _watcher;

    public LocalPhotoStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _watcher = new FileSystemWatcher(_directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
        };
        _watcher.Changed += Invalidate;
        _watcher.Created += Invalidate;
        _watcher.Deleted += Invalidate;
        _watcher.Renamed += Invalidate;
        _watcher.Error += (_, _) => Invalidate();
        _watcher.EnableRaisingEvents = true;
    }

    public Gallery[] GetGalleries()
    {
        var galleries = Volatile.Read(ref _galleries);
        if (galleries is not null)
            return galleries;

        lock (_gate)
        {
            galleries = _galleries;
            if (galleries is not null)
                return galleries;

            galleries = Directory.EnumerateDirectories(_directory)
                .Where(folder => (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(CreateGallery)
                .Where(gallery => gallery is not null)
                .Select(gallery => gallery!)
                .ToArray();
            Volatile.Write(ref _galleries, galleries);
            return galleries;
        }
    }

    public string? GetImagePath(string folderName, string fileName)
    {
        if (Path.GetFileName(folderName) != folderName ||
            Path.GetFileName(fileName) != fileName ||
            !ImageExtensions.Contains(Path.GetExtension(fileName)))
            return null;

        var folderPath = Path.GetFullPath(Path.Combine(_directory, folderName));
        var imagePath = Path.GetFullPath(Path.Combine(folderPath, fileName));
        if (!IsChildPath(_directory, folderPath) ||
            !IsChildPath(_directory, imagePath) ||
            !Directory.Exists(folderPath) ||
            !File.Exists(imagePath) ||
            (File.GetAttributes(folderPath) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(imagePath) & FileAttributes.ReparsePoint) != 0)
            return null;

        return imagePath;
    }

    private Gallery? CreateGallery(string folderPath)
    {
        var folderName = Path.GetFileName(folderPath);
        var images = Directory.EnumerateFiles(folderPath)
            .Where(file => ImageExtensions.Contains(Path.GetExtension(file)) &&
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(file => $"/local-photos/{Uri.EscapeDataString(folderName)}/{Uri.EscapeDataString(Path.GetFileName(file))}")
            .ToArray();
        if (images.Length == 0)
            return null;

        var title = string.Join(" ", folderName
            .Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
        var date = DateOnly.FromDateTime(Directory.GetLastWriteTimeUtc(folderPath)).ToString("yyyy-MM-dd");
        return new Gallery(
            Guid.NewGuid(),
            title,
            "Local photos",
            "",
            date,
            images[0],
            images);
    }

    private bool IsChildPath(string parent, string path) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, _pathComparison);

    private void Invalidate(object? sender, FileSystemEventArgs args) => Invalidate();

    private void Invalidate()
    {
        lock (_gate)
            Volatile.Write(ref _galleries, null);
    }

    public void Dispose() => _watcher.Dispose();
}



sealed class GalleryStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private GallerySnapshot _snapshot = new([], new Dictionary<string, Gallery[]>(StringComparer.Ordinal));

    public Task<Gallery[]> GetAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _snapshot).Galleries);
    }

    public Task<Gallery[]> FindReferencingAsync(string imageUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Volatile.Read(ref _snapshot);
        return Task.FromResult(snapshot.Images.TryGetValue(imageUrl, out var galleries) ? galleries : []);
    }

    public async Task SaveAsync(Func<Gallery[], Gallery[]> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = update(Volatile.Read(ref _snapshot).Galleries);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, next, JsonOptions, cancellationToken);
                }

                File.Move(temporaryPath, path, true);
                Volatile.Write(ref _snapshot, CreateSnapshot(next));
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureSeededAsync()
    {
        if (File.Exists(path))
        {
            await using var existingStream = File.OpenRead(path);
            var existing = await JsonSerializer.DeserializeAsync<Gallery[]>(existingStream, JsonOptions) ?? [];
            Volatile.Write(ref _snapshot, CreateSnapshot(existing));
            return;
        }

        var seed = new[]
        {
            new Gallery(Guid.NewGuid(), "The quiet coast", "Travel", "Amalfi Coast, Italy", "2025-08-14",
                "https://images.unsplash.com/photo-1516483638261-f4dbaf036963?auto=format&fit=crop&w=1400&q=80",
                [
                    "https://images.unsplash.com/photo-1516483638261-f4dbaf036963?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1533105079780-92b9be482077?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1498307833015-e7b400441eb8?auto=format&fit=crop&w=1600&q=80"
                ]),
            new Gallery(Guid.NewGuid(), "Soft hours", "Portraits", "Copenhagen, Denmark", "2025-06-02",
                "https://images.unsplash.com/photo-1534528741775-53994a69daeb?auto=format&fit=crop&w=1400&q=80",
                [
                    "https://images.unsplash.com/photo-1534528741775-53994a69daeb?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1524504388940-b1c1722653e1?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1508214751196-bcfd4ca60f91?auto=format&fit=crop&w=1600&q=80"
                ]),
            new Gallery(Guid.NewGuid(), "In good company", "Stories", "New York, USA", "2025-04-19",
                "https://images.unsplash.com/photo-1511795409834-ef04bbd61622?auto=format&fit=crop&w=1400&q=80",
                [
                    "https://images.unsplash.com/photo-1511795409834-ef04bbd61622?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1519741497674-611481863552?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1511285560929-80b456fea0bc?auto=format&fit=crop&w=1600&q=80"
                ],
                true),
            new Gallery(Guid.NewGuid(), "An evening to remember", "Celebrations", "Lisbon, Portugal", "2025-05-24",
                "https://images.unsplash.com/photo-1519741497674-611481863552?auto=format&fit=crop&w=1400&q=80",
                [
                    "https://images.unsplash.com/photo-1519741497674-611481863552?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1511285560929-80b456fea0bc?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1511795409834-ef04bbd61622?auto=format&fit=crop&w=1600&q=80"
                ],
                true),
            new Gallery(Guid.NewGuid(), "A day together", "Gatherings", "Copenhagen, Denmark", "2025-03-08",
                "https://images.unsplash.com/photo-1511285560929-80b456fea0bc?auto=format&fit=crop&w=1400&q=80",
                [
                    "https://images.unsplash.com/photo-1511285560929-80b456fea0bc?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1519741497674-611481863552?auto=format&fit=crop&w=1600&q=80",
                    "https://images.unsplash.com/photo-1511795409834-ef04bbd61622?auto=format&fit=crop&w=1600&q=80"
                ],
                true)
        };
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, seed, JsonOptions);
        Volatile.Write(ref _snapshot, CreateSnapshot(seed));
    }

    private static GallerySnapshot CreateSnapshot(Gallery[] galleries) =>
        new(
            galleries,
            galleries
                .SelectMany(gallery => gallery.Images.Select(image => (image, gallery)))
                .GroupBy(item => item.image, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(item => item.gallery).ToArray(), StringComparer.Ordinal));

    private sealed record GallerySnapshot(Gallery[] Galleries, Dictionary<string, Gallery[]> Images);
}

sealed class CredentialStore(string path)
{
    private const int Iterations = 210_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task InitializeAsync(string? email, string? password)
    {
        var hasEmail = !string.IsNullOrWhiteSpace(email);
        var hasPassword = !string.IsNullOrWhiteSpace(password);
        if (hasEmail != hasPassword)
            throw new InvalidOperationException("Set both ADMIN_EMAIL and ADMIN_PASSWORD to configure the admin account.");
        if (!hasEmail)
            return;

        var credential = CreateCredential(email!.Trim(), password!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(stream, credential, JsonOptions);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<bool> VerifyAsync(string email, string password, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
                return false;

            await using var stream = File.OpenRead(path);
            var credential = await JsonSerializer.DeserializeAsync<StoredCredential>(stream, JsonOptions, cancellationToken);
            if (credential is null ||
                !string.Equals(email.Trim(), credential.Email, StringComparison.OrdinalIgnoreCase))
                return false;

            var salt = Convert.FromBase64String(credential.Salt);
            var expected = Convert.FromBase64String(credential.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static StoredCredential CreateCredential(string email, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new StoredCredential(email, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    private sealed record StoredCredential(string Email, string Salt, string PasswordHash);
}

static class EventAccessDefaults
{
    public const string AuthenticationScheme = "PrivateEvent";
    public const string GalleryAccessClaim = "private-event-gallery-access";

    public static string FormatGalleryAccess(Guid galleryId, string version) =>
        $"{galleryId:N}:{version}";

    public static bool TryParseGalleryAccess(string value, out Guid galleryId, out string version)
    {
        var separator = value.IndexOf(':');
        if (separator > 0 &&
            Guid.TryParseExact(value[..separator], "N", out galleryId) &&
            separator < value.Length - 1)
        {
            version = value[(separator + 1)..];
            return true;
        }

        galleryId = Guid.Empty;
        version = "";
        return false;
    }

    public static HashSet<Guid> GetUnlockedGalleryIds(AuthenticateResult access)
    {
        if (!access.Succeeded || access.Principal is null)
            return [];

        return access.Principal.FindAll(GalleryAccessClaim)
            .Select(claim => TryParseGalleryAccess(claim.Value, out var galleryId, out _)
                ? galleryId
                : Guid.Empty)
            .Where(galleryId => galleryId != Guid.Empty)
            .ToHashSet();
    }
}

enum KeySetResult
{
    Saved,
    Invalid,
    Duplicate
}

record GalleryKeyAccess(Guid GalleryId, string Version);

sealed class PrivateGalleryKeyStore(string path)
{
    private const int Iterations = 210_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, StoredKey> _keys = new();

    public bool IsConfigured(Guid galleryId) => _keys.ContainsKey(galleryId);
    public bool HasShareLink(Guid galleryId) =>
        _keys.TryGetValue(galleryId, out var stored) && stored.ShareTokenHash is not null;

    public bool IsCurrentVersion(Guid galleryId, string version) =>
        _keys.TryGetValue(galleryId, out var stored) &&
        string.Equals(version, stored.Version, StringComparison.Ordinal);

    public async Task InitializeAsync()
    {
        if (!File.Exists(path))
            return;

        await using var stream = File.OpenRead(path);
        var stored = await JsonSerializer.DeserializeAsync<Dictionary<Guid, StoredKey>>(stream, JsonOptions);
        if (stored is null)
            throw new InvalidDataException("The private gallery access-key file is invalid.");
        foreach (var (galleryId, credential) in stored)
        {
            if (galleryId == Guid.Empty || string.IsNullOrEmpty(credential.Version))
                throw new InvalidDataException("The private gallery access-key file is invalid.");
            _keys[galleryId] = credential;
        }
    }

    public async Task<KeySetResult> SetAsync(Guid galleryId, string? accessKey, CancellationToken cancellationToken)
    {
        if (accessKey is null || !IsValidKey(accessKey))
            return KeySetResult.Invalid;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_keys.Any(entry => entry.Key != galleryId && Matches(accessKey, entry.Value)))
                return KeySetResult.Duplicate;

            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(accessKey, salt, Iterations, HashAlgorithmName.SHA256, 32);
            var next = _keys.ToDictionary(entry => entry.Key, entry => entry.Value);
            next[galleryId] = new StoredKey(
                Convert.ToBase64String(salt),
                Convert.ToBase64String(hash),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
            await SaveCredentialsAsync(next, cancellationToken);
            _keys[galleryId] = next[galleryId];
            return KeySetResult.Saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GalleryKeyAccess?> VerifyAndGetAccessAsync(string? accessKey, CancellationToken cancellationToken)
    {
        if (accessKey is null || !IsValidKey(accessKey))
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var (galleryId, stored) in _keys.OrderBy(entry => entry.Key))
            {
                if (Matches(accessKey, stored))
                    return new GalleryKeyAccess(galleryId, stored.Version);
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> CreateShareTokenAsync(Guid galleryId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_keys.TryGetValue(galleryId, out var stored))
                throw new InvalidOperationException("A private gallery access key must be configured before sharing.");

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var updated = stored with
            {
                ShareTokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            };
            var next = _keys.ToDictionary(entry => entry.Key, entry => entry.Value);
            next[galleryId] = updated;
            await SaveCredentialsAsync(next, cancellationToken);
            _keys[galleryId] = updated;
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public GalleryKeyAccess? VerifyShareToken(string? token)
    {
        if (token is null || token.Length != 43 ||
            token.Any(character => character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            return null;

        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        foreach (var (galleryId, stored) in _keys)
        {
            if (stored.ShareTokenHash is null)
                continue;

            var expectedHash = Convert.FromBase64String(stored.ShareTokenHash);
            if (CryptographicOperations.FixedTimeEquals(tokenHash, expectedHash))
                return new GalleryKeyAccess(galleryId, stored.Version);
        }

        return null;
    }

    public async Task RemoveShareTokenAsync(Guid galleryId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_keys.TryGetValue(galleryId, out var stored) || stored.ShareTokenHash is null)
                return;

            var updated = stored with { ShareTokenHash = null };
            var next = _keys.ToDictionary(entry => entry.Key, entry => entry.Value);
            next[galleryId] = updated;
            await SaveCredentialsAsync(next, cancellationToken);
            _keys[galleryId] = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid galleryId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_keys.ContainsKey(galleryId))
                return;

            var next = _keys.ToDictionary(entry => entry.Key, entry => entry.Value);
            next.Remove(galleryId);
            await SaveCredentialsAsync(next, cancellationToken);
            _keys.TryRemove(galleryId, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static bool IsValidKey(string key) =>
        key.Length == 8 && key.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');

    private static bool Matches(string accessKey, StoredKey stored)
    {
        var salt = Convert.FromBase64String(stored.Salt);
        var expected = Convert.FromBase64String(stored.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(accessKey, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private async Task SaveCredentialsAsync(Dictionary<Guid, StoredKey> credentials, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, credentials, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record StoredKey(string Salt, string Hash, string Version, string? ShareTokenHash = null);
}

static class PhotoCache
{
    public static readonly int[] Widths = [480, 960, 1600];
    private static readonly int[] DescendingWidths = [1600, 960, 480];
    private static readonly ConcurrentDictionary<string, CacheEntry> Entries =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string GetKey(string sourcePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath)))).ToLowerInvariant();

    public static string GetFingerprint(FileInfo source) =>
        $"{source.Length}:{source.LastWriteTimeUtc.Ticks}";

    public static string GetCachePath(string sourcePath, int width, string cacheDirectory) =>
        Path.Combine(cacheDirectory, $"{GetKey(sourcePath)}-{width}.webp");

    public static string GetManifestPath(string sourcePath, string cacheDirectory) =>
        Path.Combine(cacheDirectory, $"{GetKey(sourcePath)}.cache");

    public static async Task<string?> TryGetCachedPathAsync(
        string sourcePath,
        string requestedWidth,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(requestedWidth, out var width) || !Widths.Contains(width))
            return null;

        var source = new FileInfo(sourcePath);
        if (!source.Exists)
            return null;

        var fingerprint = GetFingerprint(source);
        var fullPath = Path.GetFullPath(sourcePath);
        if (Entries.TryGetValue(fullPath, out var entry) &&
            string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            var match = DescendingWidths
                .Where(candidate => candidate <= width && entry.Paths.ContainsKey(candidate))
                .Select(candidate => entry.Paths[candidate])
                .FirstOrDefault();

            if (match is not null && File.Exists(match))
                return match;

            return entry.Paths.TryGetValue(width, out var direct) && File.Exists(direct)
                ? direct
                : null;
        }

        var manifestPath = GetManifestPath(fullPath, cacheDirectory);
        if (!File.Exists(manifestPath) ||
            !string.Equals(
                await File.ReadAllTextAsync(manifestPath, cancellationToken),
                fingerprint,
                StringComparison.Ordinal))
        {
            Entries[fullPath] = new CacheEntry(fingerprint, new Dictionary<int, string>());
            return null;
        }

        var paths = new Dictionary<int, string>();
        foreach (var candidate in DescendingWidths)
        {
            var candidatePath = GetCachePath(fullPath, candidate, cacheDirectory);
            if (File.Exists(candidatePath))
                paths[candidate] = candidatePath;
        }

        Entries[fullPath] = new CacheEntry(fingerprint, paths);
        return DescendingWidths
            .Where(candidate => candidate <= width && paths.ContainsKey(candidate))
            .Select(candidate => paths[candidate])
            .FirstOrDefault();
    }

    public static void Register(string sourcePath, string fingerprint, string cacheDirectory, IEnumerable<int> widths)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var paths = widths.ToDictionary(width => width, width => GetCachePath(fullPath, width, cacheDirectory));
        Entries[fullPath] = new CacheEntry(fingerprint, paths);
    }

    private sealed record CacheEntry(string Fingerprint, Dictionary<int, string> Paths);
}

sealed class PhotoCacheService(
    string[] sourceDirectories,
    string cacheDirectory,
    ILogger<PhotoCacheService> logger) : BackgroundService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };
    private readonly Dictionary<string, string> _processedFiles = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public void QueuePhoto(string path) => _queue.Writer.TryWrite(path);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueTask = ProcessQueueAsync(stoppingToken);
        var periodicTask = PeriodicScanAsync(stoppingToken);

        await Task.WhenAll(queueTask, periodicTask);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                while (reader.TryRead(out var photoPath))
                {
                    if (stoppingToken.IsCancellationRequested) return;
                    await ProcessPhotoAsync(photoPath, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }

    private async Task PeriodicScanAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                await ScanAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        foreach (var directory in sourceDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };

            try
            {
                foreach (var sourcePath in Directory.EnumerateFiles(directory, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ImageExtensions.Contains(Path.GetExtension(sourcePath)))
                        await ProcessPhotoAsync(sourcePath, cancellationToken);
                }
            }
            catch (IOException exception)
            {
                logger.LogError(exception, "Could not scan photo directory {PhotoDirectory}.", directory);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogError(exception, "Could not scan photo directory {PhotoDirectory}.", directory);
            }
        }
    }

    private async Task ProcessPhotoAsync(string sourcePath, CancellationToken cancellationToken)
    {
        try
        {
            var source = new FileInfo(sourcePath);
            if (!source.Exists || source.Length == 0)
                return;

            var fingerprint = PhotoCache.GetFingerprint(source);
            if (_processedFiles.TryGetValue(sourcePath, out var processedFingerprint) &&
                string.Equals(processedFingerprint, fingerprint, StringComparison.Ordinal))
                return;

            // Fast inspect dimensions from image header without decoding pixels
            int imageWidth = 0, imageHeight = 0;
            using (var codec = SKCodec.Create(sourcePath))
            {
                if (codec is not null)
                {
                    imageWidth = codec.Info.Width;
                    imageHeight = codec.Info.Height;
                }
            }

            if (imageWidth <= 0 || imageHeight <= 0)
            {
                using var fallback = SKBitmap.Decode(sourcePath);
                if (fallback is null)
                    throw new InvalidDataException("The file is not a supported image.");
                imageWidth = fallback.Width;
                imageHeight = fallback.Height;
            }

            var largestDimension = Math.Max(imageWidth, imageHeight);
            var widths = PhotoCache.Widths.Where(width => width < largestDimension).ToArray();
            var manifestPath = PhotoCache.GetManifestPath(sourcePath, cacheDirectory);
            var cacheIsCurrent = File.Exists(manifestPath) &&
                string.Equals(await File.ReadAllTextAsync(manifestPath, cancellationToken), fingerprint, StringComparison.Ordinal) &&
                widths.All(width => File.Exists(PhotoCache.GetCachePath(sourcePath, width, cacheDirectory)));

            if (!cacheIsCurrent)
            {
                // Only decode full bitmap into memory when resizing is required
                using var image = SKBitmap.Decode(sourcePath)
                    ?? throw new InvalidDataException("The image could not be decoded.");

                Directory.CreateDirectory(cacheDirectory);
                foreach (var width in widths)
                {
                    var destination = PhotoCache.GetCachePath(sourcePath, width, cacheDirectory);
                    var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
                    try
                    {
                        var scale = (double)width / largestDimension;
                        var size = new SKImageInfo(
                            Math.Max(1, (int)Math.Round(image.Width * scale)),
                            Math.Max(1, (int)Math.Round(image.Height * scale)));
                        using var resized = image.Resize(size, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                            ?? throw new InvalidDataException("The image could not be resized.");
                        using var encodedImage = SKImage.FromBitmap(resized);
                        using var encoded = encodedImage.Encode(SKEncodedImageFormat.Webp, 80)
                            ?? throw new InvalidDataException("The image could not be encoded as WebP.");

                        await using (var outputStream = new FileStream(
                            temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                        {
                            encoded.SaveTo(outputStream);
                        }
                        File.Move(temporaryPath, destination, true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                }

                foreach (var width in PhotoCache.Widths.Except(widths))
                    File.Delete(PhotoCache.GetCachePath(sourcePath, width, cacheDirectory));

                var temporaryManifest = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllTextAsync(temporaryManifest, fingerprint, cancellationToken);
                    File.Move(temporaryManifest, manifestPath, true);
                }
                finally
                {
                    if (File.Exists(temporaryManifest))
                        File.Delete(temporaryManifest);
                }
            }

            PhotoCache.Register(sourcePath, fingerprint, cacheDirectory, widths);
            _processedFiles[sourcePath] = fingerprint;
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "Skipping invalid image file {PhotoPath}.", sourcePath);
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not generate photo cache for {PhotoPath}.", sourcePath);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Could not generate photo cache for {PhotoPath}.", sourcePath);
        }
    }
}

record SettingsUpdateRequest(string? GeminiApiKey, string? GoogleDriveApiKey);
record GeminiSuggestRequest(string[]? Images, string? Hint = null);
record GeminiSuggestionResult(string? Title, string? Category, string? Location, string? Date, string? Description);
record GDriveImportRequest(string? Url, bool IsEvent = true, bool AutoDescribe = true);
record GoogleDriveImportResult(string[] Images, string? FolderTitle);

sealed class SettingsStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private AppSettings _settings = new();

    public async Task InitializeAsync()
    {
        if (!File.Exists(path)) return;
        try
        {
            await using var stream = File.OpenRead(path);
            _settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        }
        catch { }
    }

    public string? GetGeminiApiKey() =>
        Environment.GetEnvironmentVariable("GEMINI_API_KEY") ??
        Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ??
        _settings.GeminiApiKey;

    public string? GetGoogleDriveApiKey() =>
        Environment.GetEnvironmentVariable("GOOGLE_DRIVE_API_KEY") ??
        Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ??
        _settings.GoogleDriveApiKey ??
        GetGeminiApiKey();

    public AppSettings GetSettings() => _settings;

    public async Task SaveAsync(AppSettings newSettings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _settings = newSettings;
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(stream, _settings, JsonOptions, cancellationToken);
            }
            File.Move(tmp, path, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

sealed record AppSettings(string? GeminiApiKey = null, string? GoogleDriveApiKey = null);

sealed class GeminiService(
    IHttpClientFactory httpClientFactory,
    SettingsStore settings,
    string photoCacheDirectory,
    string uploadDirectory,
    string localPhotosDirectory,
    ILogger<GeminiService> logger)
{
    public async Task<GeminiSuggestionResult?> SuggestGalleryDetailsAsync(
        string[]? images,
        string? hint,
        CancellationToken cancellationToken)
    {
        var apiKey = settings.GetGeminiApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Please configure your Gemini API key in Studio Settings to enable AI auto-naming.");

        var candidateImages = (images ?? [])
            .Where(img => !string.IsNullOrWhiteSpace(img))
            .Take(4)
            .ToArray();

        var parts = new List<object>
        {
            new
            {
                text = "You are a world-class photography curator and visual storyteller. " +
                       "Analyze the attached photograph(s) from a collection or event. " +
                       (string.IsNullOrWhiteSpace(hint) ? "" : $"Context or folder hint: {hint}. ") +
                       "Provide:\n" +
                       "1. 'title': A fitting, artistic, evocative gallery title (max 50 chars, e.g. 'Golden Hour at the Cape', 'Whispers of Autumn').\n" +
                       "2. 'category': A photography genre (e.g. Travel, Portraits, Wedding, Gatherings, Celebration, Architecture, Street, Nature).\n" +
                       "3. 'location': The geographical or atmospheric location (e.g. 'Amalfi Coast, Italy' or 'Northern Pines').\n" +
                       "4. 'date': A date in YYYY-MM-DD format (if visible or inferrable, otherwise today's date).\n" +
                       "5. 'description': An evocative 1-3 sentence story or artist statement describing the atmosphere, light, and narrative of the photos.\n\n" +
                       "Return ONLY a JSON object with keys: title, category, location, date, description."
            }
        };

        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        foreach (var imageUrl in candidateImages)
        {
            var imageData = await TryGetImageDataAsync(imageUrl, httpClient, cancellationToken);
            if (imageData is not null)
            {
                parts.Add(new
                {
                    inline_data = new
                    {
                        mime_type = imageData.Value.MimeType,
                        data = Convert.ToBase64String(imageData.Value.Bytes)
                    }
                });
            }
        }

        var requestBody = new
        {
            contents = new[] { new { parts } },
            generationConfig = new
            {
                response_mime_type = "application/json",
                temperature = 0.4
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent?key={Uri.EscapeDataString(apiKey)}";
        using var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(url, jsonContent, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Gemini API call failed: {StatusCode} {Error}", response.StatusCode, err);
            throw new HttpRequestException($"Gemini API error ({response.StatusCode}): {err}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cleanJson = ExtractJson(text);
        return JsonSerializer.Deserialize<GeminiSuggestionResult>(cleanJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) trimmed = trimmed[..end];
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[3..];
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) trimmed = trimmed[..end];
        }
        return trimmed.Trim();
    }

    private async Task<(byte[] Bytes, string MimeType)?> TryGetImageDataAsync(
        string imageUrl,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            if (imageUrl.StartsWith("/uploads/", StringComparison.Ordinal))
            {
                var fileName = Path.GetFileName(imageUrl);
                var originalPath = Path.Combine(uploadDirectory, fileName);
                var cached480 = PhotoCache.GetCachePath(originalPath, 480, photoCacheDirectory);
                var cached960 = PhotoCache.GetCachePath(originalPath, 960, photoCacheDirectory);

                var chosenPath = File.Exists(cached480) ? cached480 : File.Exists(cached960) ? cached960 : originalPath;
                if (!File.Exists(chosenPath)) return null;

                var bytes = await File.ReadAllBytesAsync(chosenPath, cancellationToken);
                var mime = chosenPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg";
                return (bytes, mime);
            }
            if (imageUrl.StartsWith("/local-photos/", StringComparison.Ordinal))
            {
                var parts = imageUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var folder = Uri.UnescapeDataString(parts[1]);
                    var file = Uri.UnescapeDataString(parts[2]);
                    var originalPath = Path.Combine(localPhotosDirectory, folder, file);
                    var cached480 = PhotoCache.GetCachePath(originalPath, 480, photoCacheDirectory);
                    var chosenPath = File.Exists(cached480) ? cached480 : originalPath;
                    if (!File.Exists(chosenPath)) return null;

                    var bytes = await File.ReadAllBytesAsync(chosenPath, cancellationToken);
                    var mime = chosenPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg";
                    return (bytes, mime);
                }
            }
            if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var response = await client.GetAsync(imageUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var mime = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    return (bytes, mime);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load image data for {ImageUrl}", imageUrl);
        }
        return null;
    }
}

sealed class GoogleDriveService(
    IHttpClientFactory httpClientFactory,
    SettingsStore settings,
    string uploadDirectory,
    PhotoCacheService photoCache,
    ILogger<GoogleDriveService> logger)
{
    private static readonly Regex FolderIdRegex = new(@"(?:folders\/|id=)([a-zA-Z0-9_\-]{25,})", RegexOptions.Compiled);
    private static readonly Regex FileIdRegex = new(@"(?:file\/d\/|id=|\/d\/)([a-zA-Z0-9_\-]{25,})", RegexOptions.Compiled);
    private static readonly Regex GeneralIdRegex = new(@"^[a-zA-Z0-9_\-]{25,}$", RegexOptions.Compiled);

    public async Task<GoogleDriveImportResult> ImportFromDriveAsync(
        string urlOrId,
        CancellationToken cancellationToken)
    {
        var apiKey = settings.GetGoogleDriveApiKey();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.Timeout = TimeSpan.FromSeconds(60);

        var (folderId, fileIds, folderTitle) = await ResolveDriveTargetsAsync(urlOrId, apiKey, client, cancellationToken);

        if (fileIds.Count == 0 && string.IsNullOrEmpty(folderId))
            throw new ArgumentException("Could not identify any Google Drive folder or files from the provided link.");

        if (!string.IsNullOrEmpty(folderId) && fileIds.Count == 0)
        {
            var listResult = await ListFolderFilesAsync(folderId, apiKey, client, cancellationToken);
            fileIds.AddRange(listResult.FileIds);
            if (string.IsNullOrEmpty(folderTitle)) folderTitle = listResult.FolderTitle;
        }

        if (fileIds.Count == 0)
            throw new InvalidOperationException("No images were found in the Google Drive folder. Make sure the folder contains image files and is shared with 'Anyone with the link can view'.");

        var importedUrls = new List<string>();
        var targetFileIds = fileIds.Distinct().Take(80).ToList();

        foreach (var fileId in targetFileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localUrl = await DownloadDriveImageAsync(fileId, apiKey, client, cancellationToken);
            if (localUrl is not null)
                importedUrls.Add(localUrl);
        }

        if (importedUrls.Count == 0)
            throw new InvalidOperationException("Could not download image files from the provided Google Drive link. Check link sharing permissions.");

        return new GoogleDriveImportResult(importedUrls.ToArray(), folderTitle);
    }

    private async Task<(string? FolderId, List<string> FileIds, string? FolderTitle)> ResolveDriveTargetsAsync(
        string input,
        string? apiKey,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var trimmed = input.Trim();
        var fileIds = new List<string>();

        var folderMatch = FolderIdRegex.Match(trimmed);
        if (folderMatch.Success && trimmed.Contains("folder"))
        {
            return (folderMatch.Groups[1].Value, fileIds, null);
        }

        var fileMatch = FileIdRegex.Match(trimmed);
        if (fileMatch.Success)
        {
            fileIds.Add(fileMatch.Groups[1].Value);
            return (null, fileIds, null);
        }

        var lines = trimmed.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 1)
        {
            foreach (var line in lines)
            {
                var fm = FileIdRegex.Match(line.Trim());
                if (fm.Success) fileIds.Add(fm.Groups[1].Value);
                else if (GeneralIdRegex.IsMatch(line.Trim())) fileIds.Add(line.Trim());
            }
            if (fileIds.Count > 0) return (null, fileIds, null);
        }

        if (GeneralIdRegex.IsMatch(trimmed))
        {
            return (trimmed, fileIds, null);
        }

        return (null, fileIds, null);
    }

    private async Task<(List<string> FileIds, string? FolderTitle)> ListFolderFilesAsync(
        string folderId,
        string? apiKey,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var fileIds = new List<string>();
        string? title = null;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var apiUrl = $"https://www.googleapis.com/drive/v3/files?q='{folderId}'+in+parents+and+trashed=false&fields=files(id,name,mimeType)&pageSize=80&key={Uri.EscapeDataString(apiKey)}";
                var response = await client.GetAsync(apiUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("files", out var files))
                    {
                        foreach (var f in files.EnumerateArray())
                        {
                            var mime = f.TryGetProperty("mimeType", out var m) ? m.GetString() : "";
                            var id = f.TryGetProperty("id", out var i) ? i.GetString() : null;
                            if (id is not null && (mime?.StartsWith("image/") == true || mime == "application/octet-stream"))
                                fileIds.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Drive API folder query failed for {FolderId}", folderId);
            }
        }

        if (fileIds.Count > 0)
            return (fileIds, title);

        try
        {
            var folderUrl = $"https://drive.google.com/embeddedfolderview?id={folderId}#list";
            var response = await client.GetAsync(folderUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                var titleMatch = Regex.Match(html, @"<title>([^<]+)<\/title>", RegexOptions.IgnoreCase);
                if (titleMatch.Success && !titleMatch.Groups[1].Value.Contains("Google Drive"))
                    title = titleMatch.Groups[1].Value.Trim();

                var matches = Regex.Matches(html, @"id=""entry-([a-zA-Z0-9_\-]{25,})""|data-id=""([a-zA-Z0-9_\-]{25,})""|/file/d/([a-zA-Z0-9_\-]{25,})");
                foreach (Match match in matches)
                {
                    for (int g = 1; g < match.Groups.Count; g++)
                    {
                        if (match.Groups[g].Success && match.Groups[g].Value != folderId)
                        {
                            fileIds.Add(match.Groups[g].Value);
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Public folder scrape failed for {FolderId}", folderId);
        }

        return (fileIds, title);
    }

    private async Task<string?> DownloadDriveImageAsync(
        string fileId,
        string? apiKey,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var downloadUrls = new List<string>();
        if (!string.IsNullOrWhiteSpace(apiKey))
            downloadUrls.Add($"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media&key={Uri.EscapeDataString(apiKey)}");

        downloadUrls.Add($"https://lh3.googleusercontent.com/d/{fileId}");
        downloadUrls.Add($"https://drive.google.com/uc?export=download&id={fileId}");

        foreach (var url in downloadUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length < 12) continue;

                var extension = DetectExtension(bytes);
                if (extension is null) continue;

                var fileName = $"{Guid.NewGuid():N}{extension}";
                var destination = Path.Combine(uploadDirectory, fileName);

                await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
                photoCache.QueuePhoto(destination);

                return $"/uploads/{fileName}";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed downloading drive file {FileId} from {Url}", fileId, url);
            }
        }
        return null;
    }

    private static string? DetectExtension(byte[] header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return ".jpg";
        if (header.Length >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return ".png";
        if (header.Length >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return ".webp";

        return null;
    }
}
