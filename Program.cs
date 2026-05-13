using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WisteriaInternAttendance.Models;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// Use PostgreSQL (Supabase) with EF Core for the DbContext only
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "wisteria_attendance";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect("/");
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<AppDatabase>();
builder.Services.AddHttpClient<IGeocodingService, NominatimGeocodingService>();
builder.Services.AddHostedService<AttendanceCloseWorker>();

var app = builder.Build();

var databaseStartupStatus = "pending";
string? databaseStartupError = null;

// Initialize DB in the background. Keep the site reachable even when the
// database is unavailable, so /api/health/db can show the real connectivity error.
_ = Task.Run(async () =>
{
    try
    {
        await WithRetryAsync(async () =>
        {
            await app.Services.GetRequiredService<AppDatabase>().InitializeAsync();
        }, maxAttempts: 3, label: "DB init");
        databaseStartupStatus = "ok";
    }
    catch (Exception ex)
    {
        databaseStartupStatus = "db_init_failed";
        databaseStartupError = ex.Message;
        app.Logger.LogError(ex, "Database initialization failed. The app will keep running, but DB-backed APIs will fail until connectivity is restored.");
    }
});

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        context.Response.StatusCode = feature?.Error is InvalidOperationException
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            message = feature?.Error.Message ?? "Something went wrong."
        });
    });
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health/db", async (AppDatabase db) =>
{
    try
    {
        await db.CheckConnectionAsync();
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

app.MapGet("/api/health/startup", () =>
    Results.Ok(new
    {
        status = databaseStartupStatus,
        databaseStartupError
    })).AllowAnonymous();

// ── Auth ──────────────────────────────────────────────────────────────────────
app.MapPost("/api/auth/login", async (LoginRequest request, AppDatabase db, HttpContext http) =>
{
    var user = await db.FindCredentialAsync(request.Username);
    if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash) || !user.IsActive)
        return Results.Unauthorized();

    await SignInAppUserAsync(http, user);
    await db.TouchLastLoginAsync(user.CredentialId);
    return Results.Ok(new AuthUser(user.DisplayName, user.Username, user.Role));
});

app.MapPost("/api/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapGet("/api/auth/me", [Authorize] (ClaimsPrincipal user) =>
    Results.Ok(new AuthUser(
        user.Identity?.Name ?? "User",
        user.FindFirstValue(ClaimTypes.Email) ?? "",
        user.FindFirstValue(ClaimTypes.Role) ?? "")));

// ── Admin ─────────────────────────────────────────────────────────────────────
app.MapGet("/api/admin/dashboard", [Authorize(Roles = "Admin")] async (AppDatabase db) =>
{
    await db.MarkMissingAbsencesAsync();
    return Results.Ok(await db.GetDashboardAsync());
});

app.MapGet("/api/admin/interns", [Authorize(Roles = "Admin")] async (AppDatabase db, string? status) =>
    Results.Ok(await db.GetInternsAsync(status)));

// Simplified create: only FullName, JoiningDate, EndDate, optional Password
app.MapPost("/api/admin/interns", [Authorize(Roles = "Admin")] async (InternCreateRequest request, AppDatabase db, ClaimsPrincipal user) =>
{
    var adminId = int.Parse(user.FindFirstValue("AdminId") ?? "0", CultureInfo.InvariantCulture);
    return Results.Ok(await db.CreateInternAsync(request, adminId));
});

app.MapPut("/api/admin/interns/{id:int}", [Authorize(Roles = "Admin")] async (int id, InternUpdateRequest request, AppDatabase db) =>
{
    await db.UpdateInternAsync(id, request);
    return Results.Ok();
});

app.MapDelete("/api/admin/interns/{id:int}", [Authorize(Roles = "Admin")] async (int id, AppDatabase db) =>
{
    await db.DeleteInternAsync(id);
    return Results.Ok();
});

app.MapPatch("/api/admin/interns/{id:int}/status", [Authorize(Roles = "Admin")] async (int id, InternStatusRequest request, AppDatabase db) =>
{
    await db.UpdateInternStatusAsync(id, request.Status, request.Remark);
    return Results.Ok();
});

app.MapPatch("/api/admin/interns/{id:int}/password", [Authorize(Roles = "Admin")] async (int id, PasswordResetRequest request, AppDatabase db) =>
    Results.Ok(await db.ResetInternPasswordAsync(id, request.NewPassword)));

app.MapPost("/api/admin/interns/{id:int}/photo", [Authorize(Roles = "Admin")] async (int id, IFormFile photo, AppDatabase db) =>
    Results.Ok(await db.SaveInternPhotoAsync(id, photo))).DisableAntiforgery();

app.MapGet("/api/admin/activities", [Authorize(Roles = "Admin")] async (int? internId, int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetWorkActivitiesAsync(internId, month ?? today.Month, year ?? today.Year));
});

app.MapGet("/api/admin/interns/{id:int}/attendance", [Authorize(Roles = "Admin")] async (int id, int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetAttendanceAsync(id, month ?? today.Month, year ?? today.Year));
});

app.MapGet("/api/admin/attendance", [Authorize(Roles = "Admin")] async (int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetAdminAttendanceAsync(month ?? today.Month, year ?? today.Year));
});

app.MapGet("/api/admin/location-logs", [Authorize(Roles = "Admin")] async (int? internId, int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetAdminLocationLogsAsync(internId, month ?? today.Month, year ?? today.Year));
});

app.MapGet("/api/admin/attendance/export", [Authorize(Roles = "Admin")] async (int? internId, int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    var workbook = await db.ExportAttendanceExcelAsync(internId, month ?? today.Month, year ?? today.Year);
    var fileName = $"wisteria-attendance-{year ?? today.Year}-{month ?? today.Month:00}.xls";
    return Results.File(Encoding.UTF8.GetBytes(workbook), "application/vnd.ms-excel", fileName);
});

// Returns all intern credentials (plain password visible to admin)
app.MapGet("/api/admin/credentials", [Authorize(Roles = "Admin")] async (AppDatabase db) =>
    Results.Ok(await db.GetAllCredentialsAsync()));

// Per-intern 90-day progress
app.MapGet("/api/admin/interns/{id:int}/progress", [Authorize(Roles = "Admin")] async (int id, AppDatabase db) =>
    Results.Ok(await db.GetInternProgressAsync(id)));

// ── Intern ────────────────────────────────────────────────────────────────────
app.MapGet("/api/intern/profile", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, AppDatabase db) =>
    Results.Ok(await db.GetInternProfileAsync(GetInternId(user))));

app.MapPut("/api/intern/profile", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, InternSelfProfileRequest request, AppDatabase db) =>
{
    await db.UpdateInternProfileByInternAsync(GetInternId(user), request);
    return Results.Ok(await db.GetInternProfileAsync(GetInternId(user)));
});

app.MapGet("/api/intern/attendance/today", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, AppDatabase db) =>
    Results.Ok(await db.GetTodayAttendanceAsync(GetInternId(user))));

app.MapGet("/api/intern/attendance/monthly", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, AppDatabase db, int? month, int? year) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetAttendanceAsync(GetInternId(user), month ?? today.Month, year ?? today.Year));
});

app.MapPost("/api/intern/attendance/clock-in", [Authorize(Roles = "Intern")] async (LocationRequest request, ClaimsPrincipal user, AppDatabase db) =>
    Results.Ok(await db.ClockInAsync(GetInternId(user), request)));

app.MapPost("/api/intern/attendance/clock-out", [Authorize(Roles = "Intern")] async (LocationRequest request, ClaimsPrincipal user, AppDatabase db) =>
    Results.Ok(await db.ClockOutAsync(GetInternId(user), request)));

app.MapPost("/api/intern/location/ping", [Authorize(Roles = "Intern")] async (LocationPingRequest request, ClaimsPrincipal user, AppDatabase db) =>
    Results.Ok(await db.LogLocationPingAsync(GetInternId(user), request)));

app.MapGet("/api/intern/location/logs", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetLocationLogsAsync(GetInternId(user), month ?? today.Month, year ?? today.Year));
});

app.MapPost("/api/intern/activities", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, HttpRequest request, AppDatabase db) =>
{
    var form = await request.ReadFormAsync();
    return Results.Ok(await db.CreateWorkActivityAsync(GetInternId(user), form["comment"].ToString(), form.Files.GetFile("file")));
}).DisableAntiforgery();

app.MapGet("/api/intern/activities", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetWorkActivitiesAsync(GetInternId(user), month ?? today.Month, year ?? today.Year));
});

app.MapFallbackToFile("index.html");
app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────
static int GetInternId(ClaimsPrincipal user) =>
    int.Parse(user.FindFirstValue("InternId") ?? "0", CultureInfo.InvariantCulture);

static async Task SignInAppUserAsync(HttpContext http, CredentialUser user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.CredentialId.ToString(CultureInfo.InvariantCulture)),
        new(ClaimTypes.Name, user.DisplayName),
        new(ClaimTypes.Email, user.Username),
        new(ClaimTypes.Role, user.Role)
    };
    if (user.InternId is not null)
        claims.Add(new("InternId", user.InternId.Value.ToString(CultureInfo.InvariantCulture)));
    if (user.AdminId is not null)
        claims.Add(new("AdminId", user.AdminId.Value.ToString(CultureInfo.InvariantCulture)));

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
        });
}

static async Task WithRetryAsync(Func<Task> action, int maxAttempts = 8, string label = "operation")
{
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await action();
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            var wait = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 30));
            Console.WriteLine($"[{label}] attempt {attempt} failed: {ex.Message}. Retrying in {wait.TotalSeconds}s...");
            await Task.Delay(wait);
        }
    }
    await action(); // final attempt — let it throw
}

// ── Request / Response records ────────────────────────────────────────────────
public sealed record LoginRequest(string Username, string Password);
public sealed record AuthUser(string Name, string Username, string Role);
public sealed record InternStatusRequest(string Status, string? Remark);
public sealed record PasswordResetRequest(string? NewPassword);

// Simplified: admin only needs FullName, JoiningDate, optional EndDate, optional password
public sealed record InternCreateRequest(
    string FullName,
    DateOnly InternshipStartDate,
    DateOnly? InternshipEndDate,
    string? InitialPassword,
    string? WorkLocationName,
    decimal? WorkLatitude,
    decimal? WorkLongitude);

public sealed record InternUpdateRequest(
    string FullName,
    string OfficeNumber,
    string PermanentNumber,
    string CollegeName,
    string StreamBranch,
    string YearSemester,
    string InternshipIn,
    string DurationOfInternship,
    DateOnly InternshipStartDate,
    DateOnly? InternshipEndDate,
    string Status,
    string? Remark,
    string? WorkLocationName,
    decimal? WorkLatitude,
    decimal? WorkLongitude);

public sealed record InternSelfProfileRequest(
    string OfficeNumber,
    string PermanentNumber,
    string CollegeName,
    string StreamBranch,
    string YearSemester,
    string InternshipIn,
    string DurationOfInternship);

public sealed record LocationRequest(
    decimal? Latitude,
    decimal? Longitude,
    decimal? AccuracyMeters,
    string? AreaName);

public sealed record LocationPingRequest(
    decimal? Latitude,
    decimal? Longitude,
    decimal? AccuracyMeters,
    string? AreaName,
    string? Source);

// ── Background worker ─────────────────────────────────────────────────────────
public sealed class AttendanceCloseWorker(IServiceProvider services, ILogger<AttendanceCloseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await services.GetRequiredService<AppDatabase>().MarkMissingAbsencesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to mark missing attendance records.");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

// ── Clock ─────────────────────────────────────────────────────────────────────
public static class AttendanceClock
{
    public static readonly TimeSpan LateAfter = new(10, 0, 0);
    public static readonly TimeSpan HalfDayBefore = new(18, 0, 0);
    public static readonly TimeSpan DayClosesAt = new(23, 54, 0);
    public static readonly TimeSpan PunchOutAfter = TimeSpan.FromHours(2);
    public static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();
    public static DateTime Now() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IndiaTimeZone);

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
    }
}

// ── Password hasher ───────────────────────────────────────────────────────────
public static class PasswordHasher
{
    private const int SaltSize = 16, KeySize = 32, Iterations = 120_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"PBKDF2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string hash)
    {
        var parts = hash.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2") return false;
        var iterations = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

// ── Geocoding ─────────────────────────────────────────────────────────────────
public interface IGeocodingService
{
    Task<string?> ReverseLookupAreaAsync(decimal latitude, decimal longitude);
}

public sealed class NominatimGeocodingService(HttpClient httpClient) : IGeocodingService
{
    public async Task<string?> ReverseLookupAreaAsync(decimal latitude, decimal longitude)
    {
        var url = $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={latitude.ToString(CultureInfo.InvariantCulture)}&lon={longitude.ToString(CultureInfo.InvariantCulture)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("WisteriaInternAttendance/1.0 (intern-attendance@wisteriaproperties.in)");
        request.Headers.Referrer = new Uri("https://wisteriaproperties.in/");
        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
    }
}

// ── AppDatabase (PostgreSQL via Npgsql) ───────────────────────────────────────
public sealed class AppDatabase(IConfiguration configuration, IWebHostEnvironment environment, IGeocodingService geocoding)
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("DefaultConnection is missing.");
    private readonly string _uploadRoot = Path.Combine(environment.WebRootPath, "uploads", "interns");
    private readonly string _activityUploadRoot = Path.Combine(environment.WebRootPath, "uploads", "activities");
    private readonly decimal _defaultGeofenceRadiusMeters = configuration.GetValue<decimal?>("Geofence:DefaultRadiusMeters") ?? 250m;
    private readonly IGeocodingService _geocoding = geocoding;

    // ── Init ──────────────────────────────────────────────────────────────────
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, SchemaSql, cancellationToken);
        Directory.CreateDirectory(_uploadRoot);
        Directory.CreateDirectory(_activityUploadRoot);
        await SeedAdminAsync(connection, cancellationToken);
    }

    public async Task CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken, maxAttempts: 1);
        _ = await ScalarAsync(connection, "SELECT 1", cancellationToken);
    }

    // ── Auth ──────────────────────────────────────────────────────────────────
    public async Task<CredentialUser?> FindCredentialAsync(string username)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT c.credentialid, c.adminid, c.internid, c.username, c.passwordhash, c.role, c.isactive,
                   COALESCE(a.fullname, i.fullname, c.username) AS displayname
            FROM usercredentials c
            LEFT JOIN admins a ON a.adminid = c.adminid
            LEFT JOIN interns i ON i.internid = c.internid
            WHERE c.username = @Username
            """, connection);
        command.Parameters.AddWithValue("@Username", username.Trim());
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new CredentialUser(
            reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetBoolean(6), reader.GetString(7));
    }

    public async Task TouchLastLoginAsync(int credentialId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE usercredentials SET lastloginat = NOW() AT TIME ZONE 'UTC' WHERE credentialid = @Id", connection);
        command.Parameters.AddWithValue("@Id", credentialId);
        await command.ExecuteNonQueryAsync();
    }

    // ── Dashboard ─────────────────────────────────────────────────────────────
    public async Task<object> GetDashboardAsync()
    {
        var today = DateOnly.FromDateTime(AttendanceClock.Now());
        await using var connection = await OpenAsync();
        var active = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM interns WHERE status = 'Active'"));
        var present = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM attendance WHERE attendancedate = @Date AND status IN ('Present','Late','Pending')", ("@Date", today)));
        var late = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM attendance WHERE attendancedate = @Date AND islate = TRUE", ("@Date", today)));
        var halfDay = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM attendance WHERE attendancedate = @Date AND ishalfday = TRUE", ("@Date", today)));
        var absent = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM attendance WHERE attendancedate = @Date AND isabsent = TRUE", ("@Date", today)));

        var recent = await QueryListAsync(connection, """
            SELECT i.fullname, a.attendancedate, a.clockintime, a.clockouttime, a.status
            FROM attendance a
            INNER JOIN interns i ON i.internid = a.internid
            ORDER BY a.attendancedate DESC, a.updatedat DESC
            LIMIT 8
            """, reader => new
        {
            name = reader.GetString(0),
            date = reader.GetFieldValue<DateOnly>(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            clockIn = reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            clockOut = reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            status = reader.GetString(4)
        });

        var chart = await QueryListAsync(connection, """
            SELECT status, COUNT(*)
            FROM attendance
            WHERE EXTRACT(MONTH FROM attendancedate) = @Month AND EXTRACT(YEAR FROM attendancedate) = @Year
            GROUP BY status
            """, reader => new { status = reader.GetString(0), count = (int)reader.GetInt64(1) },
            ("@Month", today.Month), ("@Year", today.Year));

        return new { active, present, late, halfDay, absent, recent, chart };
    }

    // ── Interns ───────────────────────────────────────────────────────────────
    public async Task<List<object>> GetInternsAsync(string? status)
    {
        await using var connection = await OpenAsync();
        var sql = """
            SELECT internid, fullname, phonenumber, permanentnumber, collegename, streambranch, yearsemester,
                   projectname, durationofinternship, internshipstartdate, internshipenddate, status, remark,
                   worklocationname, worklatitude, worklongitude, photopath
            FROM interns
            WHERE (@Status::text IS NULL OR status = @Status)
            ORDER BY createdat DESC
            """;
        return await QueryListAsync<object>(connection, sql, reader => new
        {
            internId = reader.GetInt32(0),
            fullName = reader.GetString(1),
            officeNumber = DbString(reader, 2),
            permanentNumber = DbString(reader, 3),
            collegeName = DbString(reader, 4),
            streamBranch = DbString(reader, 5),
            yearSemester = DbString(reader, 6),
            internshipIn = DbString(reader, 7),
            durationOfInternship = DbString(reader, 8),
            internshipStartDate = reader.GetFieldValue<DateOnly>(9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            internshipEndDate = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateOnly>(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            status = reader.GetString(11),
            remark = DbString(reader, 12),
            workLocationName = DbString(reader, 13),
            workLatitude = DbDecimal(reader, 14),
            workLongitude = DbDecimal(reader, 15),
            photoPath = DbString(reader, 16),
            profileCompleted = !string.IsNullOrWhiteSpace(DbString(reader, 2))
                && !string.IsNullOrWhiteSpace(DbString(reader, 3))
                && !string.IsNullOrWhiteSpace(DbString(reader, 4))
                && !string.IsNullOrWhiteSpace(DbString(reader, 5))
                && !string.IsNullOrWhiteSpace(DbString(reader, 6))
                && !string.IsNullOrWhiteSpace(DbString(reader, 7))
                && !string.IsNullOrWhiteSpace(DbString(reader, 8))
        }, ("@Status", string.IsNullOrWhiteSpace(status) ? (object)DBNull.Value : status));
    }

    public async Task<object> CreateInternAsync(InternCreateRequest request, int adminId)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new InvalidOperationException("Intern name is required.");
        if (request.InternshipEndDate is not null && request.InternshipEndDate.Value < request.InternshipStartDate)
            throw new InvalidOperationException("End date must be after joining date.");

        var username = GenerateUsername(request.FullName);
        var plainPassword = string.IsNullOrWhiteSpace(request.InitialPassword) ? GeneratePassword() : request.InitialPassword.Trim();

        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var internId = Convert.ToInt32(await ScalarAsync(connection, """
            INSERT INTO interns (fullname, internshipstartdate, internshipenddate, status,
                                 worklocationname, worklatitude, worklongitude, createdbyadminid)
            VALUES (@FullName, @StartDate, @EndDate, 'PendingProfile',
                    @WorkLocationName, @WorkLatitude, @WorkLongitude, @AdminId)
            RETURNING internid
            """, transaction,
            ("@FullName", request.FullName.Trim()),
            ("@StartDate", request.InternshipStartDate),
            ("@EndDate", DbValue(request.InternshipEndDate)),
            ("@WorkLocationName", DbValue(request.WorkLocationName)),
            ("@WorkLatitude", DbValue(request.WorkLatitude)),
            ("@WorkLongitude", DbValue(request.WorkLongitude)),
            ("@AdminId", adminId)));

        await ExecuteAsync(connection, """
            INSERT INTO usercredentials (internid, username, passwordhash, plainpassword, role, mustchangepassword)
            VALUES (@InternId, @Username, @PasswordHash, @PlainPassword, 'Intern', TRUE)
            """, transaction,
            ("@InternId", (object)internId),
            ("@Username", username),
            ("@PasswordHash", PasswordHasher.Hash(plainPassword)),
            ("@PlainPassword", plainPassword));

        await transaction.CommitAsync();
        return new { internId, username, temporaryPassword = plainPassword };
    }

    public async Task<object> ResetInternPasswordAsync(int internId, string? newPassword)
    {
        var plainPassword = string.IsNullOrWhiteSpace(newPassword) ? GeneratePassword() : newPassword.Trim();
        if (plainPassword.Length < 8)
            throw new InvalidOperationException("Password must be at least 8 characters.");

        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, """
            UPDATE usercredentials
            SET passwordhash = @PasswordHash, plainpassword = @PlainPassword,
                mustchangepassword = FALSE, isactive = TRUE
            WHERE internid = @InternId AND role = 'Intern'
            """,
            ("@InternId", internId),
            ("@PasswordHash", PasswordHasher.Hash(plainPassword)),
            ("@PlainPassword", plainPassword));

        return new { temporaryPassword = plainPassword };
    }

    public async Task UpdateInternAsync(int id, InternUpdateRequest request)
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, """
            UPDATE interns
            SET fullname = @FullName, phonenumber = @OfficeNumber, permanentnumber = @PermanentNumber,
                collegename = @CollegeName, streambranch = @StreamBranch, yearsemester = @YearSemester,
                projectname = @InternshipIn, durationofinternship = @DurationOfInternship,
                internshipstartdate = @StartDate, internshipenddate = @EndDate,
                status = @Status, remark = @Remark, worklocationname = @WorkLocationName,
                worklatitude = @WorkLatitude, worklongitude = @WorkLongitude,
                updatedat = NOW() AT TIME ZONE 'UTC'
            WHERE internid = @InternId
            """,
            ("@InternId", id),
            ("@FullName", request.FullName.Trim()),
            ("@OfficeNumber", request.OfficeNumber.Trim()),
            ("@PermanentNumber", request.PermanentNumber.Trim()),
            ("@CollegeName", request.CollegeName.Trim()),
            ("@StreamBranch", request.StreamBranch.Trim()),
            ("@YearSemester", request.YearSemester.Trim()),
            ("@InternshipIn", request.InternshipIn.Trim()),
            ("@DurationOfInternship", request.DurationOfInternship.Trim()),
            ("@StartDate", request.InternshipStartDate),
            ("@EndDate", DbValue(request.InternshipEndDate)),
            ("@Status", request.Status),
            ("@Remark", DbValue(request.Remark)),
            ("@WorkLocationName", DbValue(request.WorkLocationName)),
            ("@WorkLatitude", DbValue(request.WorkLatitude)),
            ("@WorkLongitude", DbValue(request.WorkLongitude)));
    }

    public async Task UpdateInternProfileByInternAsync(int internId, InternSelfProfileRequest request)
    {
        ValidateInternSelfProfile(request);
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, """
            UPDATE interns
            SET phonenumber = @OfficeNumber, permanentnumber = @PermanentNumber,
                collegename = @CollegeName, streambranch = @StreamBranch,
                yearsemester = @YearSemester, projectname = @InternshipIn,
                durationofinternship = @DurationOfInternship,
                updatedat = NOW() AT TIME ZONE 'UTC',
                status = CASE WHEN status = 'PendingProfile' THEN 'Active' ELSE status END
            WHERE internid = @InternId
            """,
            ("@InternId", internId),
            ("@OfficeNumber", request.OfficeNumber.Trim()),
            ("@PermanentNumber", request.PermanentNumber.Trim()),
            ("@CollegeName", request.CollegeName.Trim()),
            ("@StreamBranch", request.StreamBranch.Trim()),
            ("@YearSemester", request.YearSemester.Trim()),
            ("@InternshipIn", request.InternshipIn.Trim()),
            ("@DurationOfInternship", request.DurationOfInternship.Trim()));
    }

    public async Task UpdateInternStatusAsync(int id, string status, string? remark)
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, """
            UPDATE interns SET status = @Status, remark = @Remark, updatedat = NOW() AT TIME ZONE 'UTC' WHERE internid = @InternId;
            UPDATE usercredentials SET isactive = (@Status = 'Active') WHERE internid = @InternId;
            """, ("@InternId", id), ("@Status", status), ("@Remark", DbValue(remark)));
    }

    public async Task DeleteInternAsync(int id)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, "DELETE FROM dailylocationlogs WHERE internid = @Id", transaction, ("@Id", id));
        await ExecuteAsync(connection, "DELETE FROM dailyworkactivities WHERE internid = @Id", transaction, ("@Id", id));
        await ExecuteAsync(connection, "DELETE FROM attendance WHERE internid = @Id", transaction, ("@Id", id));
        await ExecuteAsync(connection, "DELETE FROM usercredentials WHERE internid = @Id", transaction, ("@Id", id));
        await ExecuteAsync(connection, "DELETE FROM interns WHERE internid = @Id", transaction, ("@Id", id));
        await transaction.CommitAsync();
    }

    public async Task<object> SaveInternPhotoAsync(int internId, IFormFile photo)
    {
        if (photo.Length <= 0) throw new InvalidOperationException("Please select a photo.");
        if (photo.Length > 1024 * 1024) throw new InvalidOperationException("Photo size must be 1MB or less.");
        var extension = Path.GetExtension(photo.FileName).ToLowerInvariant();
        if (!new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" }.Contains(extension))
            throw new InvalidOperationException("Only JPG, PNG, or WEBP photos are allowed.");

        Directory.CreateDirectory(_uploadRoot);
        var fileName = $"{internId}-{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(_uploadRoot, fileName);
        await using (var stream = File.Create(fullPath))
            await photo.CopyToAsync(stream);

        var photoPath = $"/uploads/interns/{fileName}";
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, """
            UPDATE interns SET photopath = @PhotoPath, photofilename = @PhotoFileName,
                updatedat = NOW() AT TIME ZONE 'UTC'
            WHERE internid = @InternId
            """,
            ("@InternId", internId), ("@PhotoPath", photoPath), ("@PhotoFileName", photo.FileName));
        return new { photoPath };
    }

    // ── 90-day progress ───────────────────────────────────────────────────────
    public async Task<object> GetInternProgressAsync(int internId)
    {
        await using var connection = await OpenAsync();
        var rows = await QueryListAsync(connection, """
            SELECT fullname, internshipstartdate, internshipenddate, status
            FROM interns WHERE internid = @Id
            """, reader => new
        {
            fullName = reader.GetString(0),
            start = reader.GetFieldValue<DateOnly>(1),
            end = reader.IsDBNull(2) ? (DateOnly?)null : reader.GetFieldValue<DateOnly>(2),
            status = reader.GetString(3)
        }, ("@Id", internId));

        var intern = rows.FirstOrDefault() ?? throw new InvalidOperationException("Intern not found.");
        var today = DateOnly.FromDateTime(AttendanceClock.Now());
        var totalDays = intern.end.HasValue
            ? (intern.end.Value.ToDateTime(TimeOnly.MinValue) - intern.start.ToDateTime(TimeOnly.MinValue)).Days
            : 90;
        totalDays = Math.Max(totalDays, 1);
        var elapsedDays = Math.Max(0, (today.ToDateTime(TimeOnly.MinValue) - intern.start.ToDateTime(TimeOnly.MinValue)).Days);
        var progressPct = Math.Min(100, (elapsedDays * 100) / totalDays);

        var presentDays = Convert.ToInt32(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM attendance WHERE internid = @Id AND status IN ('Present','Late')", ("@Id", internId)));
        var absentDays = Convert.ToInt32(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM attendance WHERE internid = @Id AND isabsent = TRUE", ("@Id", internId)));
        var halfDays = Convert.ToInt32(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM attendance WHERE internid = @Id AND ishalfday = TRUE", ("@Id", internId)));

        return new
        {
            fullName = intern.fullName,
            startDate = intern.start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = intern.end?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            totalDays,
            elapsedDays,
            remainingDays = Math.Max(0, totalDays - elapsedDays),
            progressPct,
            presentDays,
            absentDays,
            halfDays,
            status = intern.status
        };
    }

    // ── Credentials table (plain password visible to admin) ───────────────────
    public async Task<List<object>> GetAllCredentialsAsync()
    {
        await using var connection = await OpenAsync();
        return await QueryListAsync<object>(connection, """
            SELECT c.credentialid, c.username, c.plainpassword, c.role, c.isactive,
                   c.lastloginat, COALESCE(a.fullname, i.fullname, c.username) AS displayname
            FROM usercredentials c
            LEFT JOIN admins a ON a.adminid = c.adminid
            LEFT JOIN interns i ON i.internid = c.internid
            ORDER BY c.role DESC, c.credentialid
            """, reader => new
        {
            credentialId = reader.GetInt32(0),
            username = reader.GetString(1),
            password = DbString(reader, 2) ?? "(hashed only)",
            role = reader.GetString(3),
            isActive = reader.GetBoolean(4),
            lastLogin = reader.IsDBNull(5) ? null : reader.GetDateTime(5).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture),
            displayName = reader.GetString(6)
        });
    }

    // ── Work activities ───────────────────────────────────────────────────────
    public async Task<object> CreateWorkActivityAsync(int internId, string? comment, IFormFile? file)
    {
        await EnsureProfileCompletedAsync(internId);
        if (string.IsNullOrWhiteSpace(comment) && (file is null || file.Length == 0))
            throw new InvalidOperationException("Add a comment or upload a work file.");

        string? filePath = null, fileName = null, contentType = null;
        long? fileSize = null;

        if (file is not null && file.Length > 0)
        {
            if (file.Length > 5 * 1024 * 1024) throw new InvalidOperationException("Daily work upload must be 5MB or less.");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt" }.Contains(ext))
                throw new InvalidOperationException("Allowed files: image, PDF, Word, Excel, or text.");
            Directory.CreateDirectory(_activityUploadRoot);
            var storedName = $"{internId}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(_activityUploadRoot, storedName);
            await using (var stream = File.Create(fullPath)) await file.CopyToAsync(stream);
            filePath = $"/uploads/activities/{storedName}";
            fileName = file.FileName;
            contentType = file.ContentType;
            fileSize = file.Length;
        }

        var today = DateOnly.FromDateTime(AttendanceClock.Now());
        await using var connection = await OpenAsync();
        var activityId = Convert.ToInt32(await ScalarAsync(connection, """
            INSERT INTO dailyworkactivities (internid, activitydate, comment, filepath, filename, contenttype, filesizebytes)
            VALUES (@InternId, @ActivityDate, @Comment, @FilePath, @FileName, @ContentType, @FileSizeBytes)
            RETURNING activityid
            """,
            ("@InternId", internId), ("@ActivityDate", today),
            ("@Comment", DbValue(comment)), ("@FilePath", DbValue(filePath)),
            ("@FileName", DbValue(fileName)), ("@ContentType", DbValue(contentType)),
            ("@FileSizeBytes", DbValue(fileSize))));

        return new { activityId, activityDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), filePath, fileName };
    }

    public async Task<List<object>> GetWorkActivitiesAsync(int? internId, int month, int year)
    {
        await using var connection = await OpenAsync();
        return await QueryListAsync<object>(connection, """
            SELECT a.activityid, a.activitydate, i.fullname, i.projectname, a.comment, a.filepath,
                   a.filename, a.filesizebytes, a.createdat
            FROM dailyworkactivities a
            INNER JOIN interns i ON i.internid = a.internid
            WHERE (@InternId::int IS NULL OR a.internid = @InternId)
              AND EXTRACT(MONTH FROM a.activitydate) = @Month
              AND EXTRACT(YEAR FROM a.activitydate) = @Year
            ORDER BY a.activitydate DESC, a.createdat DESC
            """, reader => new
        {
            activityId = reader.GetInt32(0),
            activityDate = reader.GetFieldValue<DateOnly>(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            internName = reader.GetString(2),
            projectName = DbString(reader, 3),
            comment = DbString(reader, 4),
            filePath = DbString(reader, 5),
            fileName = DbString(reader, 6),
            fileSizeBytes = reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7),
            submittedAt = TimeZoneInfo.ConvertTimeFromUtc(reader.GetDateTime(8), AttendanceClock.IndiaTimeZone)
                .ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture)
        }, ("@InternId", internId is null ? (object)DBNull.Value : internId), ("@Month", month), ("@Year", year));
    }

    // ── Intern profile ────────────────────────────────────────────────────────
    public async Task<object> GetInternProfileAsync(int internId)
    {
        await using var connection = await OpenAsync();
        var list = await QueryListAsync(connection, """
            SELECT internid, fullname, phonenumber, permanentnumber, collegename, streambranch, yearsemester,
                   projectname, durationofinternship, internshipstartdate, internshipenddate,
                   status, remark, worklocationname, worklatitude, worklongitude, photopath
            FROM interns WHERE internid = @InternId
            """, reader => new
        {
            internId = reader.GetInt32(0),
            fullName = reader.GetString(1),
            officeNumber = DbString(reader, 2),
            permanentNumber = DbString(reader, 3),
            collegeName = DbString(reader, 4),
            streamBranch = DbString(reader, 5),
            yearSemester = DbString(reader, 6),
            internshipIn = DbString(reader, 7),
            durationOfInternship = DbString(reader, 8),
            internshipStartDate = reader.GetFieldValue<DateOnly>(9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            internshipEndDate = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateOnly>(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            status = reader.GetString(11),
            remark = DbString(reader, 12),
            workLocationName = DbString(reader, 13),
            workLatitude = DbDecimal(reader, 14),
            workLongitude = DbDecimal(reader, 15),
            photoPath = DbString(reader, 16),
            profileCompleted = !string.IsNullOrWhiteSpace(DbString(reader, 2))
                && !string.IsNullOrWhiteSpace(DbString(reader, 3))
                && !string.IsNullOrWhiteSpace(DbString(reader, 4))
                && !string.IsNullOrWhiteSpace(DbString(reader, 5))
                && !string.IsNullOrWhiteSpace(DbString(reader, 6))
                && !string.IsNullOrWhiteSpace(DbString(reader, 7))
                && !string.IsNullOrWhiteSpace(DbString(reader, 8))
        }, ("@InternId", internId));
        return list.FirstOrDefault() ?? throw new InvalidOperationException("Intern profile not found.");
    }

    // ── Attendance ────────────────────────────────────────────────────────────
    public async Task<object?> GetTodayAttendanceAsync(int internId)
    {
        var today = DateOnly.FromDateTime(AttendanceClock.Now());
        return (await GetAttendanceByDateAsync(internId, today)).FirstOrDefault();
    }

    public async Task<List<object>> GetAttendanceAsync(int internId, int month, int year)
    {
        await using var connection = await OpenAsync();
        return await QueryListAsync(connection, """
            SELECT NULL::text AS internname, attendancedate, clockintime, clockouttime, workingminutes, status, islate, ishalfday, isabsent,
                   clockinlatitude, clockinlongitude, clockinaccuracymeters, clockinareaname,
                   clockoutlatitude, clockoutlongitude, clockoutaccuracymeters, clockoutareaname
            FROM attendance
            WHERE internid = @InternId
              AND EXTRACT(MONTH FROM attendancedate) = @Month
              AND EXTRACT(YEAR FROM attendancedate) = @Year
            ORDER BY attendancedate DESC
            """, MapAttendance, ("@InternId", internId), ("@Month", month), ("@Year", year));
    }

    public async Task<List<object>> GetAdminAttendanceAsync(int month, int year)
    {
        await using var connection = await OpenAsync();
        return await QueryListAsync(connection, """
            SELECT i.fullname AS internname, a.attendancedate, a.clockintime, a.clockouttime, a.workingminutes,
                   a.status, a.islate, a.ishalfday, a.isabsent,
                   a.clockinlatitude, a.clockinlongitude, a.clockinaccuracymeters, a.clockinareaname,
                   a.clockoutlatitude, a.clockoutlongitude, a.clockoutaccuracymeters, a.clockoutareaname
            FROM attendance a
            INNER JOIN interns i ON i.internid = a.internid
            WHERE EXTRACT(MONTH FROM a.attendancedate) = @Month
              AND EXTRACT(YEAR FROM a.attendancedate) = @Year
            ORDER BY a.attendancedate DESC, i.fullname
            """, MapAttendance, ("@Month", month), ("@Year", year));
    }

    public async Task<object> ClockInAsync(int internId, LocationRequest location)
    {
        await EnsureProfileCompletedAsync(internId);
        if (location.Latitude is null || location.Longitude is null)
            throw new InvalidOperationException("Location is required for clock in.");
        ValidateCoordinates(location.Latitude.Value, location.Longitude.Value);
        var now = AttendanceClock.Now();
        var today = DateOnly.FromDateTime(now);
        var isLate = now.TimeOfDay > AttendanceClock.LateAfter;

        await using var connection = await OpenAsync();
        await ValidateGeofenceAsync(connection, internId, location.Latitude.Value, location.Longitude.Value);
        var existingRows = await QueryListAsync(connection, """
            SELECT attendanceid, clockintime
            FROM attendance
            WHERE internid = @InternId AND attendancedate = @Date
            """, reader => new
        {
            attendanceId = reader.GetInt32(0),
            clockIn = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1)
        }, ("@InternId", internId), ("@Date", today));

        var areaName = await ResolveAreaNameAsync(location.AreaName, location.Latitude.Value, location.Longitude.Value);
        var existing = existingRows.FirstOrDefault();
        if (existing?.clockIn is not null) throw new InvalidOperationException("You have already punched in today.");

        if (existing is not null)
        {
            await ExecuteAsync(connection, """
                UPDATE attendance
                SET clockintime = @ClockInTime, clockinlatitude = @Latitude, clockinlongitude = @Longitude,
                    clockinaccuracymeters = @Accuracy, clockinareaname = @Area, status = 'Pending',
                    islate = @IsLate, ishalfday = FALSE, isabsent = FALSE, updatedat = NOW() AT TIME ZONE 'UTC'
                WHERE attendanceid = @AttendanceId
                """,
                ("@AttendanceId", existing.attendanceId), ("@ClockInTime", now),
                ("@Latitude", location.Latitude), ("@Longitude", location.Longitude),
                ("@Accuracy", DbValue(location.AccuracyMeters)), ("@Area", DbValue(areaName)),
                ("@IsLate", isLate));

            return (await GetAttendanceByDateAsync(internId, today)).First();
        }

        await ExecuteAsync(connection, """
            INSERT INTO attendance (internid, attendancedate, clockintime, clockinlatitude, clockinlongitude,
                                    clockinaccuracymeters, clockinareaname, status, islate, isabsent)
            VALUES (@InternId, @Date, @ClockInTime, @Latitude, @Longitude, @Accuracy, @Area, 'Pending', @IsLate, FALSE)
            """,
            ("@InternId", internId), ("@Date", today), ("@ClockInTime", now),
            ("@Latitude", location.Latitude), ("@Longitude", location.Longitude),
            ("@Accuracy", DbValue(location.AccuracyMeters)), ("@Area", DbValue(areaName)),
            ("@IsLate", isLate));

        return (await GetAttendanceByDateAsync(internId, today)).First();
    }

    public async Task<object> ClockOutAsync(int internId, LocationRequest location)
    {
        await EnsureProfileCompletedAsync(internId);
        if (location.Latitude is null || location.Longitude is null)
            throw new InvalidOperationException("Location is required for clock out.");
        ValidateCoordinates(location.Latitude.Value, location.Longitude.Value);
        var now = AttendanceClock.Now();
        var today = DateOnly.FromDateTime(now);

        await using var connection = await OpenAsync();
        await ValidateGeofenceAsync(connection, internId, location.Latitude.Value, location.Longitude.Value);

        var rows = await QueryListAsync(connection, """
            SELECT attendanceid, clockintime, clockouttime, islate
            FROM attendance WHERE internid = @InternId AND attendancedate = @Date
            """, reader => new
        {
            attendanceId = reader.GetInt32(0),
            clockIn = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
            clockOut = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
            isLate = reader.GetBoolean(3)
        }, ("@InternId", internId), ("@Date", today));

        var att = rows.FirstOrDefault() ?? throw new InvalidOperationException("Please clock in before clocking out.");
        if (att.clockOut is not null) throw new InvalidOperationException("You have already clocked out today.");
        if (att.clockIn is null) throw new InvalidOperationException("Please clock in before clocking out.");

        var punchOutAvailableAt = att.clockIn.Value.Add(AttendanceClock.PunchOutAfter);
        if (now < punchOutAvailableAt)
            throw new InvalidOperationException($"Punch out opens after 2 hours. You can punch out at {punchOutAvailableAt:hh:mm tt}.");

        var workingMinutes = Math.Max(0, (int)(now - att.clockIn.Value).TotalMinutes);
        var isHalfDay = now.TimeOfDay < AttendanceClock.HalfDayBefore;
        var status = isHalfDay ? "HalfDay" : att.isLate ? "Late" : "Present";
        var areaName = await ResolveAreaNameAsync(location.AreaName, location.Latitude.Value, location.Longitude.Value);

        await ExecuteAsync(connection, """
            UPDATE attendance
            SET clockouttime = @ClockOutTime, clockoutlatitude = @Latitude, clockoutlongitude = @Longitude,
                clockoutaccuracymeters = @Accuracy, clockoutareaname = @Area, workingminutes = @WorkingMinutes,
                status = @Status, ishalfday = @IsHalfDay, updatedat = NOW() AT TIME ZONE 'UTC'
            WHERE attendanceid = @AttendanceId
            """,
            ("@AttendanceId", att.attendanceId), ("@ClockOutTime", now),
            ("@Latitude", location.Latitude), ("@Longitude", location.Longitude),
            ("@Accuracy", DbValue(location.AccuracyMeters)), ("@Area", DbValue(areaName)),
            ("@WorkingMinutes", workingMinutes), ("@Status", status), ("@IsHalfDay", isHalfDay));

        return (await GetAttendanceByDateAsync(internId, today)).First();
    }

    public async Task<object> LogLocationPingAsync(int internId, LocationPingRequest request)
    {
        await EnsureProfileCompletedAsync(internId);
        if (request.Latitude is null || request.Longitude is null)
            throw new InvalidOperationException("Location is required for ping.");
        ValidateCoordinates(request.Latitude.Value, request.Longitude.Value);
        var now = AttendanceClock.Now();
        var today = DateOnly.FromDateTime(now);
        await using var connection = await OpenAsync();
        await ValidateGeofenceAsync(connection, internId, request.Latitude.Value, request.Longitude.Value);
        var areaName = await ResolveAreaNameAsync(request.AreaName, request.Latitude.Value, request.Longitude.Value);

        var logId = Convert.ToInt32(await ScalarAsync(connection, """
            INSERT INTO dailylocationlogs (internid, logdate, loggedat, latitude, longitude, accuracymeters, areaname, source)
            VALUES (@InternId, @LogDate, @LoggedAt, @Latitude, @Longitude, @Accuracy, @AreaName, @Source)
            RETURNING locationlogid
            """,
            ("@InternId", internId), ("@LogDate", today), ("@LoggedAt", now),
            ("@Latitude", request.Latitude), ("@Longitude", request.Longitude),
            ("@Accuracy", DbValue(request.AccuracyMeters)), ("@AreaName", DbValue(areaName)),
            ("@Source", DbValue(request.Source))));

        return new { locationLogId = logId, logDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), loggedAt = now.ToString("hh:mm tt", CultureInfo.InvariantCulture), areaName };
    }

    public async Task<List<object>> GetLocationLogsAsync(int internId, int month, int year)
    {
        await using var connection = await OpenAsync();
        return await QueryListAsync<object>(connection, """
            SELECT logdate, loggedat, latitude, longitude, accuracymeters, areaname, source
            FROM dailylocationlogs
            WHERE internid = @InternId
              AND EXTRACT(MONTH FROM logdate) = @Month
              AND EXTRACT(YEAR FROM logdate) = @Year
            ORDER BY logdate DESC, loggedat DESC
            """, reader => new
        {
            logDate = reader.GetFieldValue<DateOnly>(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            loggedAt = reader.GetDateTime(1).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            latitude = reader.GetDecimal(2),
            longitude = reader.GetDecimal(3),
            accuracyMeters = DbDecimal(reader, 4),
            areaName = DbString(reader, 5),
            source = DbString(reader, 6)
        }, ("@InternId", internId), ("@Month", month), ("@Year", year));
    }

    public async Task<List<object>> GetAdminLocationLogsAsync(int? internId, int month, int year)
    {
        await using var connection = await OpenAsync();
        return await QueryListAsync<object>(connection, """
            SELECT l.logdate, l.loggedat, i.fullname, l.latitude, l.longitude, l.accuracymeters, l.areaname, l.source
            FROM dailylocationlogs l
            INNER JOIN interns i ON i.internid = l.internid
            WHERE (@InternId::int IS NULL OR l.internid = @InternId)
              AND EXTRACT(MONTH FROM l.logdate) = @Month
              AND EXTRACT(YEAR FROM l.logdate) = @Year
            ORDER BY l.logdate DESC, l.loggedat DESC, i.fullname
            LIMIT 500
            """, reader => new
        {
            logDate = reader.GetFieldValue<DateOnly>(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            loggedAt = reader.GetDateTime(1).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            internName = reader.GetString(2),
            latitude = reader.GetDecimal(3),
            longitude = reader.GetDecimal(4),
            accuracyMeters = DbDecimal(reader, 5),
            areaName = DbString(reader, 6),
            source = DbString(reader, 7)
        }, ("@InternId", internId is null ? (object)DBNull.Value : internId), ("@Month", month), ("@Year", year));
    }

    public async Task MarkMissingAbsencesAsync(CancellationToken cancellationToken = default)
    {
        var yesterday = DateOnly.FromDateTime(AttendanceClock.Now().Date.AddDays(-1));
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            UPDATE attendance
            SET status = 'Incomplete', ishalfday = TRUE, updatedat = NOW() AT TIME ZONE 'UTC'
            WHERE attendancedate = @Date AND clockintime IS NOT NULL AND clockouttime IS NULL;

            INSERT INTO attendance (internid, attendancedate, status, isabsent)
            SELECT i.internid, @Date, 'Absent', TRUE
            FROM interns i
            WHERE i.status = 'Active'
              AND i.internshipstartdate <= @Date
              AND (i.internshipenddate IS NULL OR i.internshipenddate >= @Date)
              AND NOT EXISTS (
                  SELECT 1 FROM attendance a WHERE a.internid = i.internid AND a.attendancedate = @Date
              )
            """, cancellationToken, ("@Date", yesterday));
    }

    public async Task<string> ExportAttendanceExcelAsync(int? internId, int month, int year)
    {
        await using var connection = await OpenAsync();
        var rows = await QueryListAsync(connection, """
            SELECT i.fullname, i.phonenumber, i.projectname, a.attendancedate, a.clockintime, a.clockouttime,
                   a.workingminutes, a.status, a.islate, a.ishalfday, a.isabsent,
                   a.clockinlatitude, a.clockinlongitude, a.clockinareaname,
                   a.clockoutlatitude, a.clockoutlongitude, a.clockoutareaname, i.remark
            FROM attendance a
            INNER JOIN interns i ON i.internid = a.internid
            WHERE (@InternId::int IS NULL OR a.internid = @InternId)
              AND EXTRACT(MONTH FROM a.attendancedate) = @Month
              AND EXTRACT(YEAR FROM a.attendancedate) = @Year
            ORDER BY i.fullname, a.attendancedate
            """, reader => new[]
        {
            reader.GetString(0), DbString(reader, 1) ?? "", DbString(reader, 2) ?? "",
            reader.GetFieldValue<DateOnly>(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? "" : reader.GetDateTime(4).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            reader.IsDBNull(5) ? "" : reader.GetDateTime(5).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? "" : $"{reader.GetInt32(6) / 60}h {reader.GetInt32(6) % 60}m",
            reader.GetString(7), reader.GetBoolean(8) ? "Yes" : "No",
            reader.GetBoolean(9) ? "Yes" : "No", reader.GetBoolean(10) ? "Yes" : "No",
            $"{DbDecimal(reader, 11)}, {DbDecimal(reader, 12)}", DbString(reader, 13) ?? "",
            $"{DbDecimal(reader, 14)}, {DbDecimal(reader, 15)}", DbString(reader, 16) ?? "",
            DbString(reader, 17) ?? ""
        }, ("@InternId", internId is null ? (object)DBNull.Value : internId), ("@Month", month), ("@Year", year));

        var workbook = new StringBuilder();
        workbook.AppendLine("""
            <?xml version="1.0"?>
            <?mso-application progid="Excel.Sheet"?>
            <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"
             xmlns:o="urn:schemas-microsoft-com:office:office"
             xmlns:x="urn:schemas-microsoft-com:office:excel"
             xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
            <Worksheet ss:Name="Attendance">
            <Table>
            """);
        AppendExcelRow(workbook, ["Intern Name", "Phone", "Project", "Date", "Clock In", "Clock Out", "Working Hours", "Status", "Late", "Half Day", "Absent", "Clock In Coordinates", "Clock In Area", "Clock Out Coordinates", "Clock Out Area", "Remark"]);
        foreach (var row in rows) AppendExcelRow(workbook, row);
        workbook.AppendLine("</Table></Worksheet></Workbook>");
        return workbook.ToString();
    }

    // ── Private helpers ───────────────────────────────────────────────────────
    private async Task<List<object>> GetAttendanceByDateAsync(int internId, DateOnly date)
    {
        await using var connection = await OpenAsync();
        return await QueryListAsync(connection, """
            SELECT NULL::text AS internname, attendancedate, clockintime, clockouttime, workingminutes, status, islate, ishalfday, isabsent,
                   clockinlatitude, clockinlongitude, clockinaccuracymeters, clockinareaname,
                   clockoutlatitude, clockoutlongitude, clockoutaccuracymeters, clockoutareaname
            FROM attendance
            WHERE internid = @InternId AND attendancedate = @Date
            """, MapAttendance, ("@InternId", internId), ("@Date", date));
    }

    private static object MapAttendance(NpgsqlDataReader reader) => new
    {
        internName = DbString(reader, 0),
        date = reader.GetFieldValue<DateOnly>(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        clockIn = reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("hh:mm tt", CultureInfo.InvariantCulture),
        clockOut = reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToString("hh:mm tt", CultureInfo.InvariantCulture),
        workingMinutes = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
        workingHours = reader.IsDBNull(4) ? null : $"{reader.GetInt32(4) / 60}h {reader.GetInt32(4) % 60}m",
        status = reader.GetString(5),
        isLate = reader.GetBoolean(6),
        isHalfDay = reader.GetBoolean(7),
        isAbsent = reader.GetBoolean(8),
        clockInLatitude = DbDecimal(reader, 9),
        clockInLongitude = DbDecimal(reader, 10),
        clockInAccuracyMeters = DbDecimal(reader, 11),
        clockInAreaName = DbString(reader, 12),
        clockOutLatitude = DbDecimal(reader, 13),
        clockOutLongitude = DbDecimal(reader, 14),
        clockOutAccuracyMeters = DbDecimal(reader, 15),
        clockOutAreaName = DbString(reader, 16),
        canPunchOut = !reader.IsDBNull(2) && reader.IsDBNull(3) && AttendanceClock.Now() >= reader.GetDateTime(2).Add(AttendanceClock.PunchOutAfter),
        punchOutAvailableAt = reader.IsDBNull(2)
            ? null
            : reader.GetDateTime(2).Add(AttendanceClock.PunchOutAfter).ToString("hh:mm tt", CultureInfo.InvariantCulture)
    };

    // Opens a connection with exponential-backoff retry (handles transient DB outages / network blips)
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default, int maxAttempts = 5)
    {
        Exception? lastEx = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastEx = ex;
                var wait = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 16));
                await Task.Delay(wait, cancellationToken);
            }
        }
        throw lastEx!;
    }

    private async Task SeedAdminAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var adminName = configuration["SeedAdmin:Name"] ?? "Wisteria HR";
        var username = configuration["SeedAdmin:Username"] ?? "hr@wisteriaproperties.in";
        var password = configuration["SeedAdmin:Password"] ?? "CHANGE_THIS_PASSWORD";

        var count = Convert.ToInt32(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM usercredentials WHERE role = 'Admin'", cancellationToken: cancellationToken));
        if (count > 0) return;

        var adminId = Convert.ToInt32(await ScalarAsync(connection, """
            INSERT INTO admins (fullname, email, passwordhash)
            VALUES (@Name, @Email, @PasswordHash)
            RETURNING adminid
            """, cancellationToken: cancellationToken,
            ("@Name", adminName), ("@Email", username),
            ("@PasswordHash", PasswordHasher.Hash(password))));

        await ExecuteAsync(connection, """
            INSERT INTO usercredentials (adminid, username, passwordhash, plainpassword, role, mustchangepassword)
            VALUES (@AdminId, @Username, @PasswordHash, @PlainPassword, 'Admin', FALSE)
            """, cancellationToken,
            ("@AdminId", adminId), ("@Username", username),
            ("@PasswordHash", PasswordHasher.Hash(password)),
            ("@PlainPassword", password));
    }

    private async Task EnsureProfileCompletedAsync(int internId)
    {
        await using var connection = await OpenAsync();
        var rows = await QueryListAsync(connection, """
            SELECT phonenumber, permanentnumber, collegename, streambranch, yearsemester, projectname, durationofinternship
            FROM interns WHERE internid = @InternId
            """, reader => new
        {
            p1 = DbString(reader, 0), p2 = DbString(reader, 1), p3 = DbString(reader, 2),
            p4 = DbString(reader, 3), p5 = DbString(reader, 4), p6 = DbString(reader, 5), p7 = DbString(reader, 6)
        }, ("@InternId", internId));

        var p = rows.FirstOrDefault() ?? throw new InvalidOperationException("Intern profile not found.");
        if (string.IsNullOrWhiteSpace(p.p1) || string.IsNullOrWhiteSpace(p.p2) || string.IsNullOrWhiteSpace(p.p3) ||
            string.IsNullOrWhiteSpace(p.p4) || string.IsNullOrWhiteSpace(p.p5) || string.IsNullOrWhiteSpace(p.p6) ||
            string.IsNullOrWhiteSpace(p.p7))
            throw new InvalidOperationException("Complete your intern profile before using attendance and work upload.");
    }

    private static void ValidateInternSelfProfile(InternSelfProfileRequest request)
    {
        if (!Regex.IsMatch(request.OfficeNumber.Trim(), @"^\+\d{1,4}\s?\d{10}$"))
            throw new InvalidOperationException("Office number must include a country code and a 10 digit number.");
        if (!Regex.IsMatch(request.PermanentNumber.Trim(), @"^\+\d{1,4}\s?\d{10}$"))
            throw new InvalidOperationException("Personal number must include a country code and a 10 digit number.");
        if (!Regex.IsMatch(request.CollegeName.Trim(), @"^[A-Za-z][A-Za-z\s.&'-]{1,119}$"))
            throw new InvalidOperationException("College name must use letters.");
        if (!Regex.IsMatch(request.InternshipIn.Trim(), @"^[A-Za-z][A-Za-z\s.&'-]{1,79}$"))
            throw new InvalidOperationException("Internship in must use letters.");
        if (!Regex.IsMatch(request.DurationOfInternship.Trim(), @"^(3 months|6 months)$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Duration of internship must be 3 months or 6 months.");
    }

    private static void ValidateCoordinates(decimal latitude, decimal longitude)
    {
        if (latitude < -90 || latitude > 90)
            throw new InvalidOperationException("Latitude must be between -90 and 90.");
        if (longitude < -180 || longitude > 180)
            throw new InvalidOperationException("Longitude must be between -180 and 180.");
    }

    private async Task ValidateGeofenceAsync(NpgsqlConnection connection, int internId, decimal latitude, decimal longitude)
    {
        var rows = await QueryListAsync(connection, """
            SELECT worklatitude, worklongitude, worklocationname FROM interns WHERE internid = @InternId
            """, reader => new
        {
            workLatitude = DbDecimal(reader, 0),
            workLongitude = DbDecimal(reader, 1),
            workLocationName = DbString(reader, 2)
        }, ("@InternId", internId));

        var loc = rows.FirstOrDefault();
        if (loc?.workLatitude is null || loc.workLongitude is null) return;

        var dist = HaversineDistanceMeters((double)loc.workLatitude.Value, (double)loc.workLongitude.Value, (double)latitude, (double)longitude);
        if (dist > (double)_defaultGeofenceRadiusMeters)
        {
            var target = string.IsNullOrWhiteSpace(loc.workLocationName) ? "assigned office/site" : loc.workLocationName;
            throw new InvalidOperationException($"You are outside the allowed geofence for {target}. Current distance: {Math.Round(dist)} meters.");
        }
    }

    private async Task<string?> ResolveAreaNameAsync(string? explicitArea, decimal latitude, decimal longitude)
    {
        if (!string.IsNullOrWhiteSpace(explicitArea)) return explicitArea.Trim();
        return await _geocoding.ReverseLookupAreaAsync(latitude, longitude);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        await ScalarAsync(connection, sql, CancellationToken.None, parameters);

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction transaction, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddParameters(command, parameters);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        await ExecuteAsync(connection, sql, CancellationToken.None, parameters);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction transaction, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<T>> QueryListAsync<T>(NpgsqlConnection connection, string sql, Func<NpgsqlDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }

    private static void AddParameters(NpgsqlCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var dbValue = value is DateOnly date ? date.ToDateTime(TimeOnly.MinValue) : value ?? DBNull.Value;
            command.Parameters.AddWithValue(name, dbValue);
        }
    }

    private static object DbValue(object? value) => value switch
    {
        null => DBNull.Value,
        string text when string.IsNullOrWhiteSpace(text) => DBNull.Value,
        _ => value
    };

    private static string? DbString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal? DbDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static double HaversineDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Pow(Math.Sin(dLon / 2), 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string GenerateUsername(string fullName)
    {
        var namePart = new string(fullName.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(12).ToArray());
        if (string.IsNullOrWhiteSpace(namePart)) namePart = "intern";
        return $"{namePart}{RandomNumberGenerator.GetInt32(100, 999)}@wisteria";
    }

    private static string GeneratePassword() => $"Wi@{RandomNumberGenerator.GetInt32(100000, 999999)}";

    private static void AppendExcelRow(StringBuilder wb, IEnumerable<string> values)
    {
        wb.AppendLine("<Row>");
        foreach (var v in values) { wb.Append("<Cell><Data ss:Type=\"String\">"); wb.Append(WebUtility.HtmlEncode(v)); wb.AppendLine("</Data></Cell>"); }
        wb.AppendLine("</Row>");
    }

    // PostgreSQL DDL (lowercase table/column names — Postgres convention)
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS admins (
            adminid SERIAL PRIMARY KEY,
            fullname VARCHAR(100) NOT NULL,
            email VARCHAR(150) NOT NULL UNIQUE,
            passwordhash VARCHAR(255) NOT NULL,
            phone VARCHAR(20),
            isactive BOOLEAN NOT NULL DEFAULT TRUE,
            createdat TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updatedat TIMESTAMPTZ
        );

        CREATE TABLE IF NOT EXISTS interns (
            internid SERIAL PRIMARY KEY,
            fullname VARCHAR(100) NOT NULL,
            phonenumber VARCHAR(20),
            permanentnumber VARCHAR(20),
            collegename VARCHAR(150),
            streambranch VARCHAR(120),
            yearsemester VARCHAR(80),
            permanentaddress VARCHAR(500),
            internshipstartdate DATE NOT NULL,
            internshipenddate DATE,
            projectname VARCHAR(150),
            durationofinternship VARCHAR(100),
            status VARCHAR(50) NOT NULL DEFAULT 'Active',
            remark VARCHAR(500),
            worklocationname VARCHAR(200),
            worklatitude DECIMAL(10,7),
            worklongitude DECIMAL(10,7),
            photopath VARCHAR(300),
            photofilename VARCHAR(255),
            createdbyadminid INT NOT NULL,
            createdat TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updatedat TIMESTAMPTZ,
            CONSTRAINT fk_interns_admins FOREIGN KEY (createdbyadminid) REFERENCES admins(adminid)
        );

        CREATE TABLE IF NOT EXISTS usercredentials (
            credentialid SERIAL PRIMARY KEY,
            internid INT,
            adminid INT,
            username VARCHAR(80) NOT NULL UNIQUE,
            passwordhash VARCHAR(255) NOT NULL,
            plainpassword VARCHAR(255),
            role VARCHAR(20) NOT NULL,
            mustchangepassword BOOLEAN NOT NULL DEFAULT TRUE,
            isactive BOOLEAN NOT NULL DEFAULT TRUE,
            lastloginat TIMESTAMPTZ,
            createdat TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            CONSTRAINT fk_creds_interns FOREIGN KEY (internid) REFERENCES interns(internid),
            CONSTRAINT fk_creds_admins FOREIGN KEY (adminid) REFERENCES admins(adminid)
        );

        CREATE TABLE IF NOT EXISTS attendance (
            attendanceid SERIAL PRIMARY KEY,
            internid INT NOT NULL,
            attendancedate DATE NOT NULL,
            clockintime TIMESTAMPTZ,
            clockouttime TIMESTAMPTZ,
            clockinlatitude DECIMAL(10,7),
            clockinlongitude DECIMAL(10,7),
            clockinaccuracymeters DECIMAL(10,2),
            clockinareaname VARCHAR(250),
            clockoutlatitude DECIMAL(10,7),
            clockoutlongitude DECIMAL(10,7),
            clockoutaccuracymeters DECIMAL(10,2),
            clockoutareaname VARCHAR(250),
            workingminutes INT,
            status VARCHAR(30) NOT NULL DEFAULT 'Pending',
            islate BOOLEAN NOT NULL DEFAULT FALSE,
            ishalfday BOOLEAN NOT NULL DEFAULT FALSE,
            isabsent BOOLEAN NOT NULL DEFAULT FALSE,
            createdat TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updatedat TIMESTAMPTZ,
            CONSTRAINT fk_att_interns FOREIGN KEY (internid) REFERENCES interns(internid),
            CONSTRAINT uq_att_intern_date UNIQUE (internid, attendancedate)
        );

        CREATE TABLE IF NOT EXISTS dailyworkactivities (
            activityid SERIAL PRIMARY KEY,
            internid INT NOT NULL,
            activitydate DATE NOT NULL,
            comment TEXT,
            filepath VARCHAR(300),
            filename VARCHAR(255),
            contenttype VARCHAR(120),
            filesizebytes BIGINT,
            createdat TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            CONSTRAINT fk_act_interns FOREIGN KEY (internid) REFERENCES interns(internid)
        );

        CREATE TABLE IF NOT EXISTS dailylocationlogs (
            locationlogid SERIAL PRIMARY KEY,
            internid INT NOT NULL,
            logdate DATE NOT NULL,
            loggedat TIMESTAMPTZ NOT NULL,
            latitude DECIMAL(10,7) NOT NULL,
            longitude DECIMAL(10,7) NOT NULL,
            accuracymeters DECIMAL(10,2),
            areaname VARCHAR(250),
            source VARCHAR(40),
            createdat TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            CONSTRAINT fk_loc_interns FOREIGN KEY (internid) REFERENCES interns(internid)
        );

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_attendance_date') THEN
                CREATE INDEX ix_attendance_date ON attendance(attendancedate);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_attendance_internid') THEN
                CREATE INDEX ix_attendance_internid ON attendance(internid);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_attendance_status') THEN
                CREATE INDEX ix_attendance_status ON attendance(status);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_interns_status') THEN
                CREATE INDEX ix_interns_status ON interns(status);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_activity_intern_date') THEN
                CREATE INDEX ix_activity_intern_date ON dailyworkactivities(internid, activitydate);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_loc_intern_date') THEN
                CREATE INDEX ix_loc_intern_date ON dailylocationlogs(internid, logdate, loggedat);
            END IF;
        END $$;

        ALTER TABLE usercredentials ADD COLUMN IF NOT EXISTS plainpassword VARCHAR(255);
        """;
}

// ── CredentialUser ────────────────────────────────────────────────────────────
public sealed record CredentialUser(
    int CredentialId, int? AdminId, int? InternId,
    string Username, string PasswordHash, string Role,
    bool IsActive, string DisplayName);
