using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "wisteria_attendance";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
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

await app.Services.GetRequiredService<AppDatabase>().InitializeAsync();

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

app.MapPost("/api/auth/login", async (LoginRequest request, AppDatabase db, HttpContext http) =>
{
    var user = await db.FindCredentialAsync(request.Username);
    if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash) || !user.IsActive)
    {
        return Results.Unauthorized();
    }

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
{
    return Results.Ok(new AuthUser(
        user.Identity?.Name ?? "User",
        user.FindFirstValue(ClaimTypes.Email) ?? "",
        user.FindFirstValue(ClaimTypes.Role) ?? ""));
});

app.MapGet("/api/admin/dashboard", [Authorize(Roles = "Admin")] async (AppDatabase db) =>
{
    await db.MarkMissingAbsencesAsync();
    return Results.Ok(await db.GetDashboardAsync());
});

app.MapGet("/api/admin/interns", [Authorize(Roles = "Admin")] async (AppDatabase db, string? status) =>
{
    return Results.Ok(await db.GetInternsAsync(status));
});

app.MapPost("/api/admin/interns", [Authorize(Roles = "Admin")] async (InternCreateRequest request, AppDatabase db, ClaimsPrincipal user) =>
{
    var adminId = int.Parse(user.FindFirstValue("AdminId") ?? "0", CultureInfo.InvariantCulture);
    var created = await db.CreateInternAsync(request, adminId);
    return Results.Ok(created);
});

app.MapPut("/api/admin/interns/{id:int}", [Authorize(Roles = "Admin")] async (int id, InternUpdateRequest request, AppDatabase db) =>
{
    await db.UpdateInternAsync(id, request);
    return Results.Ok();
});

app.MapPatch("/api/admin/interns/{id:int}/status", [Authorize(Roles = "Admin")] async (int id, InternStatusRequest request, AppDatabase db) =>
{
    await db.UpdateInternStatusAsync(id, request.Status, request.Remark);
    return Results.Ok();
});

app.MapPatch("/api/admin/interns/{id:int}/password", [Authorize(Roles = "Admin")] async (int id, PasswordResetRequest request, AppDatabase db) =>
{
    return Results.Ok(await db.ResetInternPasswordAsync(id, request.NewPassword));
});

app.MapPost("/api/admin/interns/{id:int}/photo", [Authorize(Roles = "Admin")] async (int id, IFormFile photo, AppDatabase db) =>
{
    return Results.Ok(await db.SaveInternPhotoAsync(id, photo));
}).DisableAntiforgery();

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

app.MapGet("/api/admin/attendance/export", [Authorize(Roles = "Admin")] async (int? internId, int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    var workbook = await db.ExportAttendanceExcelAsync(internId, month ?? today.Month, year ?? today.Year);
    var fileName = $"wisteria-attendance-{year ?? today.Year}-{month ?? today.Month:00}.xls";
    return Results.File(Encoding.UTF8.GetBytes(workbook), "application/vnd.ms-excel", fileName);
});

app.MapGet("/api/intern/profile", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, AppDatabase db) =>
{
    return Results.Ok(await db.GetInternProfileAsync(GetInternId(user)));
});

app.MapGet("/api/intern/attendance/today", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, AppDatabase db) =>
{
    return Results.Ok(await db.GetTodayAttendanceAsync(GetInternId(user)));
});

app.MapGet("/api/intern/attendance/monthly", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, AppDatabase db, int? month, int? year) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetAttendanceAsync(GetInternId(user), month ?? today.Month, year ?? today.Year));
});

app.MapPost("/api/intern/attendance/clock-in", [Authorize(Roles = "Intern")] async (LocationRequest request, ClaimsPrincipal user, AppDatabase db) =>
{
    var result = await db.ClockInAsync(GetInternId(user), request);
    return Results.Ok(result);
});

app.MapPost("/api/intern/attendance/clock-out", [Authorize(Roles = "Intern")] async (LocationRequest request, ClaimsPrincipal user, AppDatabase db) =>
{
    var result = await db.ClockOutAsync(GetInternId(user), request);
    return Results.Ok(result);
});

app.MapPost("/api/intern/location/ping", [Authorize(Roles = "Intern")] async (LocationPingRequest request, ClaimsPrincipal user, AppDatabase db) =>
{
    return Results.Ok(await db.LogLocationPingAsync(GetInternId(user), request));
});

app.MapGet("/api/intern/location/logs", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetLocationLogsAsync(GetInternId(user), month ?? today.Month, year ?? today.Year));
});

app.MapPost("/api/intern/activities", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, HttpRequest request, AppDatabase db) =>
{
    var form = await request.ReadFormAsync();
    var comment = form["comment"].ToString();
    var file = form.Files.GetFile("file");
    return Results.Ok(await db.CreateWorkActivityAsync(GetInternId(user), comment, file));
}).DisableAntiforgery();

app.MapGet("/api/intern/activities", [Authorize(Roles = "Intern")] async (ClaimsPrincipal user, int? month, int? year, AppDatabase db) =>
{
    var today = AttendanceClock.Now().Date;
    return Results.Ok(await db.GetWorkActivitiesAsync(GetInternId(user), month ?? today.Month, year ?? today.Year));
});

app.MapFallbackToFile("index.html");

app.Run();

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
    {
        claims.Add(new("InternId", user.InternId.Value.ToString(CultureInfo.InvariantCulture)));
    }

    if (user.AdminId is not null)
    {
        claims.Add(new("AdminId", user.AdminId.Value.ToString(CultureInfo.InvariantCulture)));
    }

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
}

public sealed record LoginRequest(string Username, string Password);
public sealed record AuthUser(string Name, string Username, string Role);
public sealed record InternStatusRequest(string Status, string? Remark);
public sealed record PasswordResetRequest(string? NewPassword);

public sealed record InternCreateRequest(
    string FullName,
    string PhoneNumber,
    string? PermanentAddress,
    DateOnly InternshipStartDate,
    DateOnly InternshipEndDate,
    string? ProjectName,
    string? Remark,
    string? InitialPassword,
    string? WorkLocationName,
    decimal? WorkLatitude,
    decimal? WorkLongitude);

public sealed record InternUpdateRequest(
    string FullName,
    string PhoneNumber,
    string? PermanentAddress,
    DateOnly InternshipStartDate,
    DateOnly InternshipEndDate,
    string? ProjectName,
    string Status,
    string? Remark,
    string? WorkLocationName,
    decimal? WorkLatitude,
    decimal? WorkLongitude);

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

public static class AttendanceClock
{
    public static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    public static DateTime Now() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IndiaTimeZone);

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
    }
}

public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 120_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"PBKDF2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string hash)
    {
        var parts = hash.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2")
        {
            return false;
        }

        var iterations = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

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
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        if (!document.RootElement.TryGetProperty("display_name", out var displayName))
        {
            return null;
        }

        return displayName.GetString();
    }
}

public sealed class AppDatabase(IConfiguration configuration, IWebHostEnvironment environment, IGeocodingService geocoding)
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("DefaultConnection is missing.");
    private readonly string _uploadRoot = Path.Combine(environment.WebRootPath, "uploads", "interns");
    private readonly string _activityUploadRoot = Path.Combine(environment.WebRootPath, "uploads", "activities");
    private readonly decimal _defaultGeofenceRadiusMeters = configuration.GetValue<decimal?>("Geofence:DefaultRadiusMeters") ?? 250m;
    private readonly IGeocodingService _geocoding = geocoding;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, SchemaSql, cancellationToken);
        Directory.CreateDirectory(_uploadRoot);
        Directory.CreateDirectory(_activityUploadRoot);
        await SeedAdminAsync(connection, cancellationToken);
    }

    public async Task<CredentialUser?> FindCredentialAsync(string username)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT c.CredentialId, c.AdminId, c.InternId, c.Username, c.PasswordHash, c.Role, c.IsActive,
                   COALESCE(a.FullName, i.FullName, c.Username) AS DisplayName
            FROM UserCredentials c
            LEFT JOIN Admins a ON a.AdminId = c.AdminId
            LEFT JOIN Interns i ON i.InternId = c.InternId
            WHERE c.Username = @Username
            """, connection);
        command.Parameters.AddWithValue("@Username", username.Trim());
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new CredentialUser(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetBoolean(6),
            reader.GetString(7));
    }

    public async Task TouchLastLoginAsync(int credentialId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("UPDATE UserCredentials SET LastLoginAt = SYSUTCDATETIME() WHERE CredentialId = @Id", connection);
        command.Parameters.AddWithValue("@Id", credentialId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<object> GetDashboardAsync()
    {
        var today = DateOnly.FromDateTime(AttendanceClock.Now());
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var active = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM Interns WHERE Status = 'Active'"));
        var present = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM Attendance WHERE AttendanceDate = @Date AND Status IN ('Present', 'Late')", ("@Date", today)));
        var late = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM Attendance WHERE AttendanceDate = @Date AND IsLate = 1", ("@Date", today)));
        var halfDay = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM Attendance WHERE AttendanceDate = @Date AND IsHalfDay = 1", ("@Date", today)));
        var absent = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM Attendance WHERE AttendanceDate = @Date AND IsAbsent = 1", ("@Date", today)));

        var recent = await QueryListAsync(connection, """
            SELECT TOP 8 i.FullName, a.AttendanceDate, a.ClockInTime, a.ClockOutTime, a.Status
            FROM Attendance a
            INNER JOIN Interns i ON i.InternId = a.InternId
            ORDER BY a.AttendanceDate DESC, a.UpdatedAt DESC
            """, reader => new
        {
            name = reader.GetString(0),
            date = reader.GetDateTime(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            clockIn = reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            clockOut = reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            status = reader.GetString(4)
        });

        var chart = await QueryListAsync(connection, """
            SELECT Status, COUNT(*)
            FROM Attendance
            WHERE MONTH(AttendanceDate) = @Month AND YEAR(AttendanceDate) = @Year
            GROUP BY Status
            """, reader => new
        {
            status = reader.GetString(0),
            count = reader.GetInt32(1)
        }, ("@Month", today.Month), ("@Year", today.Year));

        return new { active, present, late, halfDay, absent, recent, chart };
    }

    public async Task<List<object>> GetInternsAsync(string? status)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = """
            SELECT InternId, FullName, PhoneNumber, PermanentAddress, InternshipStartDate, InternshipEndDate,
                   ProjectName, Status, Remark, WorkLocationName, WorkLatitude, WorkLongitude, PhotoPath
            FROM Interns
            WHERE (@Status IS NULL OR Status = @Status)
            ORDER BY CreatedAt DESC
            """;

        return await QueryListAsync<object>(connection, sql, reader => new
        {
            internId = reader.GetInt32(0),
            fullName = reader.GetString(1),
            phoneNumber = reader.GetString(2),
            permanentAddress = DbString(reader, 3),
            internshipStartDate = reader.GetDateTime(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            internshipEndDate = reader.GetDateTime(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            projectName = DbString(reader, 6),
            status = reader.GetString(7),
            remark = DbString(reader, 8),
            workLocationName = DbString(reader, 9),
            workLatitude = DbDecimal(reader, 10),
            workLongitude = DbDecimal(reader, 11),
            photoPath = DbString(reader, 12)
        }, ("@Status", string.IsNullOrWhiteSpace(status) ? DBNull.Value : status));
    }

    public async Task<object> CreateInternAsync(InternCreateRequest request, int adminId)
    {
        ValidateIntern(request.FullName, request.PhoneNumber, request.InternshipStartDate, request.InternshipEndDate);

        var username = GenerateUsername(request.FullName);
        var password = string.IsNullOrWhiteSpace(request.InitialPassword) ? GeneratePassword() : request.InitialPassword.Trim();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        var internId = Convert.ToInt32(await ScalarAsync(connection, """
            INSERT INTO Interns (FullName, PhoneNumber, PermanentAddress, InternshipStartDate, InternshipEndDate, ProjectName, Remark,
                                 WorkLocationName, WorkLatitude, WorkLongitude, CreatedByAdminId)
            OUTPUT INSERTED.InternId
            VALUES (@FullName, @PhoneNumber, @PermanentAddress, @StartDate, @EndDate, @ProjectName, @Remark,
                    @WorkLocationName, @WorkLatitude, @WorkLongitude, @AdminId)
            """, transaction,
            ("@FullName", request.FullName.Trim()),
            ("@PhoneNumber", request.PhoneNumber.Trim()),
            ("@PermanentAddress", DbValue(request.PermanentAddress)),
            ("@StartDate", request.InternshipStartDate),
            ("@EndDate", request.InternshipEndDate),
            ("@ProjectName", DbValue(request.ProjectName)),
            ("@Remark", DbValue(request.Remark)),
            ("@WorkLocationName", DbValue(request.WorkLocationName)),
            ("@WorkLatitude", DbValue(request.WorkLatitude)),
            ("@WorkLongitude", DbValue(request.WorkLongitude)),
            ("@AdminId", adminId)));

        await ExecuteAsync(connection, """
            INSERT INTO UserCredentials (InternId, Username, PasswordHash, Role, MustChangePassword)
            VALUES (@InternId, @Username, @PasswordHash, 'Intern', 1)
            """, transaction,
            ("@InternId", internId),
            ("@Username", username),
            ("@PasswordHash", PasswordHasher.Hash(password)));

        await transaction.CommitAsync();
        return new { internId, username, temporaryPassword = password };
    }

    public async Task<object> ResetInternPasswordAsync(int internId, string? newPassword)
    {
        var password = string.IsNullOrWhiteSpace(newPassword) ? GeneratePassword() : newPassword.Trim();
        if (password.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var updated = Convert.ToInt32(await ScalarAsync(connection, """
            UPDATE UserCredentials
            SET PasswordHash = @PasswordHash, MustChangePassword = 0, IsActive = 1
            WHERE InternId = @InternId AND Role = 'Intern';
            SELECT @@ROWCOUNT;
            """,
            ("@InternId", internId),
            ("@PasswordHash", PasswordHasher.Hash(password))));

        if (updated == 0)
        {
            throw new InvalidOperationException("Intern login was not found.");
        }

        return new { temporaryPassword = password };
    }

    public async Task UpdateInternAsync(int id, InternUpdateRequest request)
    {
        ValidateIntern(request.FullName, request.PhoneNumber, request.InternshipStartDate, request.InternshipEndDate);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            UPDATE Interns
            SET FullName = @FullName, PhoneNumber = @PhoneNumber, PermanentAddress = @PermanentAddress,
                InternshipStartDate = @StartDate, InternshipEndDate = @EndDate, ProjectName = @ProjectName,
                Status = @Status, Remark = @Remark, WorkLocationName = @WorkLocationName,
                WorkLatitude = @WorkLatitude, WorkLongitude = @WorkLongitude, UpdatedAt = SYSUTCDATETIME()
            WHERE InternId = @InternId
            """,
            ("@InternId", id),
            ("@FullName", request.FullName.Trim()),
            ("@PhoneNumber", request.PhoneNumber.Trim()),
            ("@PermanentAddress", DbValue(request.PermanentAddress)),
            ("@StartDate", request.InternshipStartDate),
            ("@EndDate", request.InternshipEndDate),
            ("@ProjectName", DbValue(request.ProjectName)),
            ("@Status", request.Status),
            ("@Remark", DbValue(request.Remark)),
            ("@WorkLocationName", DbValue(request.WorkLocationName)),
            ("@WorkLatitude", DbValue(request.WorkLatitude)),
            ("@WorkLongitude", DbValue(request.WorkLongitude)));
    }

    public async Task UpdateInternStatusAsync(int id, string status, string? remark)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            UPDATE Interns SET Status = @Status, Remark = @Remark, UpdatedAt = SYSUTCDATETIME() WHERE InternId = @InternId;
            UPDATE UserCredentials SET IsActive = CASE WHEN @Status = 'Active' THEN 1 ELSE 0 END WHERE InternId = @InternId;
            """, ("@InternId", id), ("@Status", status), ("@Remark", DbValue(remark)));
    }

    public async Task<object> SaveInternPhotoAsync(int internId, IFormFile photo)
    {
        if (photo.Length <= 0)
        {
            throw new InvalidOperationException("Please select a photo.");
        }

        if (photo.Length > 1024 * 1024)
        {
            throw new InvalidOperationException("Photo size must be 1MB or less.");
        }

        var extension = Path.GetExtension(photo.FileName).ToLowerInvariant();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
        if (!allowed.Contains(extension))
        {
            throw new InvalidOperationException("Only JPG, PNG, or WEBP photos are allowed.");
        }

        Directory.CreateDirectory(_uploadRoot);
        var fileName = $"{internId}-{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(_uploadRoot, fileName);
        await using (var stream = File.Create(fullPath))
        {
            await photo.CopyToAsync(stream);
        }

        var photoPath = $"/uploads/interns/{fileName}";
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            UPDATE Interns
            SET PhotoPath = @PhotoPath, PhotoFileName = @PhotoFileName, UpdatedAt = SYSUTCDATETIME()
            WHERE InternId = @InternId
            """,
            ("@InternId", internId),
            ("@PhotoPath", photoPath),
            ("@PhotoFileName", photo.FileName));

        return new { photoPath };
    }

    public async Task<object> CreateWorkActivityAsync(int internId, string? comment, IFormFile? file)
    {
        if (string.IsNullOrWhiteSpace(comment) && (file is null || file.Length == 0))
        {
            throw new InvalidOperationException("Add a comment or upload a work file.");
        }

        string? filePath = null;
        string? fileName = null;
        string? contentType = null;
        long? fileSize = null;

        if (file is not null && file.Length > 0)
        {
            if (file.Length > 5 * 1024 * 1024)
            {
                throw new InvalidOperationException("Daily work upload must be 5MB or less.");
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg", ".jpeg", ".png", ".webp", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt"
            };
            if (!allowed.Contains(extension))
            {
                throw new InvalidOperationException("Allowed files: image, PDF, Word, Excel, or text.");
            }

            Directory.CreateDirectory(_activityUploadRoot);
            var storedName = $"{internId}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(_activityUploadRoot, storedName);
            await using (var stream = File.Create(fullPath))
            {
                await file.CopyToAsync(stream);
            }

            filePath = $"/uploads/activities/{storedName}";
            fileName = file.FileName;
            contentType = file.ContentType;
            fileSize = file.Length;
        }

        var today = DateOnly.FromDateTime(AttendanceClock.Now());
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var activityId = Convert.ToInt32(await ScalarAsync(connection, """
            INSERT INTO DailyWorkActivities (InternId, ActivityDate, Comment, FilePath, FileName, ContentType, FileSizeBytes)
            OUTPUT INSERTED.ActivityId
            VALUES (@InternId, @ActivityDate, @Comment, @FilePath, @FileName, @ContentType, @FileSizeBytes)
            """,
            ("@InternId", internId),
            ("@ActivityDate", today),
            ("@Comment", DbValue(comment)),
            ("@FilePath", DbValue(filePath)),
            ("@FileName", DbValue(fileName)),
            ("@ContentType", DbValue(contentType)),
            ("@FileSizeBytes", DbValue(fileSize))));

        return new { activityId, activityDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), filePath, fileName };
    }

    public async Task<List<object>> GetWorkActivitiesAsync(int? internId, int month, int year)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return await QueryListAsync<object>(connection, """
            SELECT a.ActivityId, a.ActivityDate, i.FullName, i.ProjectName, a.Comment, a.FilePath,
                   a.FileName, a.FileSizeBytes, a.CreatedAt
            FROM DailyWorkActivities a
            INNER JOIN Interns i ON i.InternId = a.InternId
            WHERE (@InternId IS NULL OR a.InternId = @InternId)
              AND MONTH(a.ActivityDate) = @Month
              AND YEAR(a.ActivityDate) = @Year
            ORDER BY a.ActivityDate DESC, a.CreatedAt DESC
            """, reader => new
        {
            activityId = reader.GetInt32(0),
            activityDate = reader.GetDateTime(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            internName = reader.GetString(2),
            projectName = DbString(reader, 3),
            comment = DbString(reader, 4),
            filePath = DbString(reader, 5),
            fileName = DbString(reader, 6),
            fileSizeBytes = reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7),
            submittedAt = TimeZoneInfo.ConvertTimeFromUtc(reader.GetDateTime(8), AttendanceClock.IndiaTimeZone).ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture)
        }, ("@InternId", internId is null ? DBNull.Value : internId), ("@Month", month), ("@Year", year));
    }

    public async Task<object> GetInternProfileAsync(int internId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var list = await QueryListAsync(connection, """
            SELECT InternId, FullName, PhoneNumber, PermanentAddress, InternshipStartDate, InternshipEndDate,
                   ProjectName, Status, Remark, WorkLocationName, WorkLatitude, WorkLongitude, PhotoPath
            FROM Interns WHERE InternId = @InternId
            """, reader => new
        {
            internId = reader.GetInt32(0),
            fullName = reader.GetString(1),
            phoneNumber = reader.GetString(2),
            permanentAddress = DbString(reader, 3),
            internshipStartDate = reader.GetDateTime(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            internshipEndDate = reader.GetDateTime(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            projectName = DbString(reader, 6),
            status = reader.GetString(7),
            remark = DbString(reader, 8),
            workLocationName = DbString(reader, 9),
            workLatitude = DbDecimal(reader, 10),
            workLongitude = DbDecimal(reader, 11),
            photoPath = DbString(reader, 12)
        }, ("@InternId", internId));

        return list.FirstOrDefault() ?? throw new InvalidOperationException("Intern profile not found.");
    }

    public async Task<object?> GetTodayAttendanceAsync(int internId)
    {
        var today = DateOnly.FromDateTime(AttendanceClock.Now());
        var rows = await GetAttendanceByDateAsync(internId, today);
        return rows.FirstOrDefault();
    }

    public async Task<List<object>> GetAttendanceAsync(int internId, int month, int year)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return await QueryListAsync(connection, """
            SELECT AttendanceDate, ClockInTime, ClockOutTime, WorkingMinutes, Status, IsLate, IsHalfDay, IsAbsent,
                   ClockInLatitude, ClockInLongitude, ClockInAccuracyMeters, ClockInAreaName,
                   ClockOutLatitude, ClockOutLongitude, ClockOutAccuracyMeters, ClockOutAreaName
            FROM Attendance
            WHERE InternId = @InternId AND MONTH(AttendanceDate) = @Month AND YEAR(AttendanceDate) = @Year
            ORDER BY AttendanceDate DESC
            """, MapAttendance, ("@InternId", internId), ("@Month", month), ("@Year", year));
    }

    public async Task<object> ClockInAsync(int internId, LocationRequest location)
    {
        if (location.Latitude is null || location.Longitude is null)
        {
            throw new InvalidOperationException("Location is required for clock in.");
        }

        var now = AttendanceClock.Now();
        var today = DateOnly.FromDateTime(now);
        if (now.TimeOfDay > new TimeSpan(10, 15, 0))
        {
            throw new InvalidOperationException("Clock-in is closed after 10:15 AM.");
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ValidateGeofenceAsync(connection, internId, location.Latitude.Value, location.Longitude.Value);
        var exists = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM Attendance WHERE InternId = @InternId AND AttendanceDate = @Date", ("@InternId", internId), ("@Date", today)));
        if (exists > 0)
        {
            throw new InvalidOperationException("You have already clocked in today.");
        }

        var areaName = await ResolveAreaNameAsync(location.AreaName, location.Latitude.Value, location.Longitude.Value);

        await ExecuteAsync(connection, """
            INSERT INTO Attendance (InternId, AttendanceDate, ClockInTime, ClockInLatitude, ClockInLongitude,
                                    ClockInAccuracyMeters, ClockInAreaName, Status, IsLate)
            VALUES (@InternId, @Date, @ClockInTime, @Latitude, @Longitude, @Accuracy, @Area, @Status, @IsLate)
            """,
            ("@InternId", internId),
            ("@Date", today),
            ("@ClockInTime", now),
            ("@Latitude", location.Latitude),
            ("@Longitude", location.Longitude),
            ("@Accuracy", DbValue(location.AccuracyMeters)),
            ("@Area", DbValue(areaName)),
            ("@Status", "Pending"),
            ("@IsLate", false));

        return (await GetAttendanceByDateAsync(internId, today)).First();
    }

    public async Task<object> ClockOutAsync(int internId, LocationRequest location)
    {
        if (location.Latitude is null || location.Longitude is null)
        {
            throw new InvalidOperationException("Location is required for clock out.");
        }

        var now = AttendanceClock.Now();
        var today = DateOnly.FromDateTime(now);
        if (now.TimeOfDay > new TimeSpan(23, 59, 59))
        {
            throw new InvalidOperationException("Clock-out must be completed before 11:59 PM on the same date.");
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ValidateGeofenceAsync(connection, internId, location.Latitude.Value, location.Longitude.Value);

        var rows = await QueryListAsync(connection, """
            SELECT AttendanceId, ClockInTime, ClockOutTime, IsLate
            FROM Attendance
            WHERE InternId = @InternId AND AttendanceDate = @Date
            """, reader => new
        {
            attendanceId = reader.GetInt32(0),
            clockIn = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
            clockOut = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
            isLate = reader.GetBoolean(3)
        }, ("@InternId", internId), ("@Date", today));

        var attendance = rows.FirstOrDefault() ?? throw new InvalidOperationException("Please clock in before clocking out.");
        if (attendance.clockOut is not null)
        {
            throw new InvalidOperationException("You have already clocked out today.");
        }

        var workingMinutes = attendance.clockIn is null ? 0 : Math.Max(0, (int)(now - attendance.clockIn.Value).TotalMinutes);
        var isHalfDay = now.TimeOfDay < new TimeSpan(19, 30, 0);
        var status = isHalfDay ? "HalfDay" : attendance.isLate ? "Late" : "Present";
        var areaName = await ResolveAreaNameAsync(location.AreaName, location.Latitude.Value, location.Longitude.Value);

        await ExecuteAsync(connection, """
            UPDATE Attendance
            SET ClockOutTime = @ClockOutTime, ClockOutLatitude = @Latitude, ClockOutLongitude = @Longitude,
                ClockOutAccuracyMeters = @Accuracy, ClockOutAreaName = @Area, WorkingMinutes = @WorkingMinutes,
                Status = @Status, IsHalfDay = @IsHalfDay, UpdatedAt = SYSUTCDATETIME()
            WHERE AttendanceId = @AttendanceId
            """,
            ("@AttendanceId", attendance.attendanceId),
            ("@ClockOutTime", now),
            ("@Latitude", location.Latitude),
            ("@Longitude", location.Longitude),
            ("@Accuracy", DbValue(location.AccuracyMeters)),
            ("@Area", DbValue(areaName)),
            ("@WorkingMinutes", workingMinutes),
            ("@Status", status),
            ("@IsHalfDay", isHalfDay));

        return (await GetAttendanceByDateAsync(internId, today)).First();
    }

    public async Task<object> LogLocationPingAsync(int internId, LocationPingRequest request)
    {
        if (request.Latitude is null || request.Longitude is null)
        {
            throw new InvalidOperationException("Location is required for ping.");
        }

        var now = AttendanceClock.Now();
        var today = DateOnly.FromDateTime(now);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ValidateGeofenceAsync(connection, internId, request.Latitude.Value, request.Longitude.Value);
        var areaName = await ResolveAreaNameAsync(request.AreaName, request.Latitude.Value, request.Longitude.Value);

        var logId = Convert.ToInt32(await ScalarAsync(connection, """
            INSERT INTO DailyLocationLogs (InternId, LogDate, LoggedAt, Latitude, Longitude, AccuracyMeters, AreaName, Source)
            OUTPUT INSERTED.LocationLogId
            VALUES (@InternId, @LogDate, @LoggedAt, @Latitude, @Longitude, @Accuracy, @AreaName, @Source)
            """,
            ("@InternId", internId),
            ("@LogDate", today),
            ("@LoggedAt", now),
            ("@Latitude", request.Latitude),
            ("@Longitude", request.Longitude),
            ("@Accuracy", DbValue(request.AccuracyMeters)),
            ("@AreaName", DbValue(areaName)),
            ("@Source", DbValue(request.Source))));

        return new
        {
            locationLogId = logId,
            logDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            loggedAt = now.ToString("hh:mm tt", CultureInfo.InvariantCulture),
            areaName
        };
    }

    public async Task<List<object>> GetLocationLogsAsync(int internId, int month, int year)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return await QueryListAsync<object>(connection, """
            SELECT LogDate, LoggedAt, Latitude, Longitude, AccuracyMeters, AreaName, Source
            FROM DailyLocationLogs
            WHERE InternId = @InternId
              AND MONTH(LogDate) = @Month
              AND YEAR(LogDate) = @Year
            ORDER BY LogDate DESC, LoggedAt DESC
            """, reader => new
        {
            logDate = reader.GetDateTime(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            loggedAt = reader.GetDateTime(1).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            latitude = reader.GetDecimal(2),
            longitude = reader.GetDecimal(3),
            accuracyMeters = DbDecimal(reader, 4),
            areaName = DbString(reader, 5),
            source = DbString(reader, 6)
        }, ("@InternId", internId), ("@Month", month), ("@Year", year));
    }

    public async Task MarkMissingAbsencesAsync(CancellationToken cancellationToken = default)
    {
        var yesterday = DateOnly.FromDateTime(AttendanceClock.Now().Date.AddDays(-1));
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            UPDATE Attendance
            SET Status = 'Incomplete',
                IsHalfDay = 1,
                UpdatedAt = SYSUTCDATETIME()
            WHERE AttendanceDate = @Date
              AND ClockInTime IS NOT NULL
              AND ClockOutTime IS NULL;

            INSERT INTO Attendance (InternId, AttendanceDate, Status, IsAbsent)
            SELECT i.InternId, @Date, 'Absent', 1
            FROM Interns i
            WHERE i.Status = 'Active'
              AND i.InternshipStartDate <= @Date
              AND i.InternshipEndDate >= @Date
              AND NOT EXISTS (
                  SELECT 1 FROM Attendance a
                  WHERE a.InternId = i.InternId AND a.AttendanceDate = @Date
              )
            """, cancellationToken, ("@Date", yesterday));
    }

    public async Task<string> ExportAttendanceExcelAsync(int? internId, int month, int year)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var rows = await QueryListAsync(connection, """
            SELECT i.FullName, i.PhoneNumber, i.ProjectName, a.AttendanceDate, a.ClockInTime, a.ClockOutTime,
                   a.WorkingMinutes, a.Status, a.IsLate, a.IsHalfDay, a.IsAbsent,
                   a.ClockInLatitude, a.ClockInLongitude, a.ClockInAreaName,
                   a.ClockOutLatitude, a.ClockOutLongitude, a.ClockOutAreaName, i.Remark
            FROM Attendance a
            INNER JOIN Interns i ON i.InternId = a.InternId
            WHERE (@InternId IS NULL OR a.InternId = @InternId)
              AND MONTH(a.AttendanceDate) = @Month
              AND YEAR(a.AttendanceDate) = @Year
            ORDER BY i.FullName, a.AttendanceDate
            """, reader => new[]
        {
            reader.GetString(0),
            reader.GetString(1),
            DbString(reader, 2) ?? "",
            reader.GetDateTime(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? "" : reader.GetDateTime(4).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            reader.IsDBNull(5) ? "" : reader.GetDateTime(5).ToString("hh:mm tt", CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? "" : $"{reader.GetInt32(6) / 60}h {reader.GetInt32(6) % 60}m",
            reader.GetString(7),
            reader.GetBoolean(8) ? "Yes" : "No",
            reader.GetBoolean(9) ? "Yes" : "No",
            reader.GetBoolean(10) ? "Yes" : "No",
            $"{DbDecimal(reader, 11)}, {DbDecimal(reader, 12)}",
            DbString(reader, 13) ?? "",
            $"{DbDecimal(reader, 14)}, {DbDecimal(reader, 15)}",
            DbString(reader, 16) ?? "",
            DbString(reader, 17) ?? ""
        }, ("@InternId", internId is null ? DBNull.Value : internId), ("@Month", month), ("@Year", year));

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
        AppendExcelRow(workbook, [
            "Intern Name", "Phone", "Project", "Date", "Clock In", "Clock Out", "Working Hours", "Status",
            "Late", "Half Day", "Absent", "Clock In Coordinates", "Clock In Area",
            "Clock Out Coordinates", "Clock Out Area", "Remark"
        ]);
        foreach (var row in rows)
        {
            AppendExcelRow(workbook, row);
        }

        workbook.AppendLine("""
            </Table>
            </Worksheet>
            </Workbook>
            """);
        return workbook.ToString();
    }

    private async Task<List<object>> GetAttendanceByDateAsync(int internId, DateOnly date)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return await QueryListAsync(connection, """
            SELECT AttendanceDate, ClockInTime, ClockOutTime, WorkingMinutes, Status, IsLate, IsHalfDay, IsAbsent,
                   ClockInLatitude, ClockInLongitude, ClockInAccuracyMeters, ClockInAreaName,
                   ClockOutLatitude, ClockOutLongitude, ClockOutAccuracyMeters, ClockOutAreaName
            FROM Attendance
            WHERE InternId = @InternId AND AttendanceDate = @Date
            """, MapAttendance, ("@InternId", internId), ("@Date", date));
    }

    private static object MapAttendance(SqlDataReader reader) => new
    {
        date = reader.GetDateTime(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        clockIn = reader.IsDBNull(1) ? null : reader.GetDateTime(1).ToString("hh:mm tt", CultureInfo.InvariantCulture),
        clockOut = reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("hh:mm tt", CultureInfo.InvariantCulture),
        workingMinutes = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
        workingHours = reader.IsDBNull(3) ? null : $"{reader.GetInt32(3) / 60}h {reader.GetInt32(3) % 60}m",
        status = reader.GetString(4),
        isLate = reader.GetBoolean(5),
        isHalfDay = reader.GetBoolean(6),
        isAbsent = reader.GetBoolean(7),
        clockInLatitude = DbDecimal(reader, 8),
        clockInLongitude = DbDecimal(reader, 9),
        clockInAccuracyMeters = DbDecimal(reader, 10),
        clockInAreaName = DbString(reader, 11),
        clockOutLatitude = DbDecimal(reader, 12),
        clockOutLongitude = DbDecimal(reader, 13),
        clockOutAccuracyMeters = DbDecimal(reader, 14),
        clockOutAreaName = DbString(reader, 15)
    };

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var databaseName = builder.InitialCatalog;
        builder.InitialCatalog = "master";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand($"""
            IF DB_ID(@DatabaseName) IS NULL
            BEGIN
                DECLARE @Sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName);
                EXEC sp_executesql @Sql;
            END
            """, connection);
        command.Parameters.AddWithValue("@DatabaseName", databaseName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SeedAdminAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var adminName = configuration["SeedAdmin:Name"] ?? "Wisteria HR";
        var username = configuration["SeedAdmin:Username"] ?? "hr@wisteriaproperties.in";
        var password = configuration["SeedAdmin:Password"] ?? "KreenaHR@2026";

        var count = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM UserCredentials WHERE Role = 'Admin'", cancellationToken: cancellationToken));
        if (count > 0)
        {
            return;
        }

        var adminId = Convert.ToInt32(await ScalarAsync(connection, """
            INSERT INTO Admins (FullName, Email, PasswordHash)
            OUTPUT INSERTED.AdminId
            VALUES (@Name, @Email, @PasswordHash)
            """, cancellationToken: cancellationToken,
            ("@Name", adminName),
            ("@Email", username),
            ("@PasswordHash", PasswordHasher.Hash(password))));

        await ExecuteAsync(connection, """
            INSERT INTO UserCredentials (AdminId, Username, PasswordHash, Role, MustChangePassword)
            VALUES (@AdminId, @Username, @PasswordHash, 'Admin', 0)
            """, cancellationToken,
            ("@AdminId", adminId),
            ("@Username", username),
            ("@PasswordHash", PasswordHasher.Hash(password)));
    }

    private static void ValidateIntern(string fullName, string phone, DateOnly startDate, DateOnly endDate)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new InvalidOperationException("Intern name is required.");
        }

        if (string.IsNullOrWhiteSpace(phone))
        {
            throw new InvalidOperationException("Phone number is required.");
        }

        if (endDate < startDate)
        {
            throw new InvalidOperationException("Internship end date must be after start date.");
        }
    }

    private async Task<object?> ScalarAsync(SqlConnection connection, string sql, SqlTransaction? transaction = null, CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        AddParameters(command, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await ExecuteAsync(connection, sql, CancellationToken.None, parameters);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, SqlTransaction transaction, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<T>> QueryListAsync<T>(SqlConnection connection, string sql, Func<SqlDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static void AddParameters(SqlCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var dbValue = value is DateOnly date
                ? date.ToDateTime(TimeOnly.MinValue)
                : value ?? DBNull.Value;
            command.Parameters.AddWithValue(name, dbValue);
        }
    }

    private static object DbValue(object? value) => value switch
    {
        null => DBNull.Value,
        string text when string.IsNullOrWhiteSpace(text) => DBNull.Value,
        _ => value
    };

    private static string? DbString(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static decimal? DbDecimal(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private async Task ValidateGeofenceAsync(SqlConnection connection, int internId, decimal latitude, decimal longitude)
    {
        var rows = await QueryListAsync(connection, """
            SELECT WorkLatitude, WorkLongitude, WorkLocationName
            FROM Interns
            WHERE InternId = @InternId
            """, reader => new
        {
            workLatitude = DbDecimal(reader, 0),
            workLongitude = DbDecimal(reader, 1),
            workLocationName = DbString(reader, 2)
        }, ("@InternId", internId));

        var location = rows.FirstOrDefault();
        if (location?.workLatitude is null || location.workLongitude is null)
        {
            return;
        }

        var distanceMeters = HaversineDistanceMeters(
            (double)location.workLatitude.Value,
            (double)location.workLongitude.Value,
            (double)latitude,
            (double)longitude);

        if (distanceMeters > (double)_defaultGeofenceRadiusMeters)
        {
            var target = string.IsNullOrWhiteSpace(location.workLocationName) ? "assigned office/site" : location.workLocationName;
            throw new InvalidOperationException(
                $"You are outside the allowed geofence for {target}. Current distance: {Math.Round(distanceMeters)} meters.");
        }
    }

    private async Task<string?> ResolveAreaNameAsync(string? explicitArea, decimal latitude, decimal longitude)
    {
        if (!string.IsNullOrWhiteSpace(explicitArea))
        {
            return explicitArea.Trim();
        }

        return await _geocoding.ReverseLookupAreaAsync(latitude, longitude);
    }

    private static double HaversineDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6371000;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2)
                + Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2))
                * Math.Pow(Math.Sin(dLon / 2), 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadius * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private async Task<object?> ScalarAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        await ScalarAsync(connection, sql, null, default, parameters);

    private async Task<object?> ScalarAsync(SqlConnection connection, string sql, SqlTransaction transaction, params (string Name, object? Value)[] parameters) =>
        await ScalarAsync(connection, sql, transaction, default, parameters);

    private async Task<object?> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters) =>
        await ScalarAsync(connection, sql, null, cancellationToken, parameters);

    private static string GenerateUsername(string fullName)
    {
        var namePart = new string(fullName.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(12).ToArray());
        if (string.IsNullOrWhiteSpace(namePart))
        {
            namePart = "intern";
        }

        return $"{namePart}{RandomNumberGenerator.GetInt32(100, 999)}@wisteria";
    }

    private static string GeneratePassword() => $"Wi@{RandomNumberGenerator.GetInt32(100000, 999999)}";

    private static string NormalizePhone(string phoneNumber) => new(phoneNumber.Where(char.IsDigit).ToArray());

    private static string MaskPhone(string phoneNumber)
    {
        if (phoneNumber.Length <= 4)
        {
            return "****";
        }

        return $"{new string('*', Math.Max(0, phoneNumber.Length - 4))}{phoneNumber[^4..]}";
    }

    private static void AppendExcelRow(StringBuilder workbook, IEnumerable<string> values)
    {
        workbook.AppendLine("<Row>");
        foreach (var value in values)
        {
            workbook.Append("<Cell><Data ss:Type=\"String\">");
            workbook.Append(WebUtility.HtmlEncode(value));
            workbook.AppendLine("</Data></Cell>");
        }

        workbook.AppendLine("</Row>");
    }

    private const string SchemaSql = """
        IF OBJECT_ID('Admins') IS NULL
        BEGIN
            CREATE TABLE Admins (
                AdminId INT IDENTITY(1,1) PRIMARY KEY,
                FullName NVARCHAR(100) NOT NULL,
                Email NVARCHAR(150) NOT NULL UNIQUE,
                PasswordHash NVARCHAR(255) NOT NULL,
                Phone NVARCHAR(20) NULL,
                IsActive BIT NOT NULL DEFAULT 1,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                UpdatedAt DATETIME2 NULL
            );
        END

        IF OBJECT_ID('Interns') IS NULL
        BEGIN
            CREATE TABLE Interns (
                InternId INT IDENTITY(1,1) PRIMARY KEY,
                FullName NVARCHAR(100) NOT NULL,
                PhoneNumber NVARCHAR(20) NOT NULL,
                PermanentAddress NVARCHAR(500) NULL,
                InternshipStartDate DATE NOT NULL,
                InternshipEndDate DATE NOT NULL,
                ProjectName NVARCHAR(150) NULL,
                Status NVARCHAR(50) NOT NULL DEFAULT 'Active',
                Remark NVARCHAR(500) NULL,
                WorkLocationName NVARCHAR(200) NULL,
                WorkLatitude DECIMAL(10, 7) NULL,
                WorkLongitude DECIMAL(10, 7) NULL,
                PhotoPath NVARCHAR(300) NULL,
                PhotoFileName NVARCHAR(255) NULL,
                CreatedByAdminId INT NOT NULL,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                UpdatedAt DATETIME2 NULL,
                CONSTRAINT FK_Interns_Admins FOREIGN KEY (CreatedByAdminId) REFERENCES Admins(AdminId)
            );
        END

        IF COL_LENGTH('Interns', 'PhotoPath') IS NULL
            ALTER TABLE Interns ADD PhotoPath NVARCHAR(300) NULL;
        IF COL_LENGTH('Interns', 'PhotoFileName') IS NULL
            ALTER TABLE Interns ADD PhotoFileName NVARCHAR(255) NULL;

        IF OBJECT_ID('UserCredentials') IS NULL
        BEGIN
            CREATE TABLE UserCredentials (
                CredentialId INT IDENTITY(1,1) PRIMARY KEY,
                InternId INT NULL,
                AdminId INT NULL,
                Username NVARCHAR(80) NOT NULL UNIQUE,
                PasswordHash NVARCHAR(255) NOT NULL,
                Role NVARCHAR(20) NOT NULL,
                MustChangePassword BIT NOT NULL DEFAULT 1,
                IsActive BIT NOT NULL DEFAULT 1,
                LastLoginAt DATETIME2 NULL,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT FK_UserCredentials_Interns FOREIGN KEY (InternId) REFERENCES Interns(InternId),
                CONSTRAINT FK_UserCredentials_Admins FOREIGN KEY (AdminId) REFERENCES Admins(AdminId)
            );
        END

        IF OBJECT_ID('Attendance') IS NULL
        BEGIN
            CREATE TABLE Attendance (
                AttendanceId INT IDENTITY(1,1) PRIMARY KEY,
                InternId INT NOT NULL,
                AttendanceDate DATE NOT NULL,
                ClockInTime DATETIME2 NULL,
                ClockOutTime DATETIME2 NULL,
                ClockInLatitude DECIMAL(10, 7) NULL,
                ClockInLongitude DECIMAL(10, 7) NULL,
                ClockInAccuracyMeters DECIMAL(10, 2) NULL,
                ClockInAreaName NVARCHAR(250) NULL,
                ClockOutLatitude DECIMAL(10, 7) NULL,
                ClockOutLongitude DECIMAL(10, 7) NULL,
                ClockOutAccuracyMeters DECIMAL(10, 2) NULL,
                ClockOutAreaName NVARCHAR(250) NULL,
                WorkingMinutes INT NULL,
                Status NVARCHAR(30) NOT NULL DEFAULT 'Pending',
                IsLate BIT NOT NULL DEFAULT 0,
                IsHalfDay BIT NOT NULL DEFAULT 0,
                IsAbsent BIT NOT NULL DEFAULT 0,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                UpdatedAt DATETIME2 NULL,
                CONSTRAINT FK_Attendance_Interns FOREIGN KEY (InternId) REFERENCES Interns(InternId),
                CONSTRAINT UQ_Attendance_Intern_Date UNIQUE (InternId, AttendanceDate)
            );
        END

        IF OBJECT_ID('DailyWorkActivities') IS NULL
        BEGIN
            CREATE TABLE DailyWorkActivities (
                ActivityId INT IDENTITY(1,1) PRIMARY KEY,
                InternId INT NOT NULL,
                ActivityDate DATE NOT NULL,
                Comment NVARCHAR(MAX) NULL,
                FilePath NVARCHAR(300) NULL,
                FileName NVARCHAR(255) NULL,
                ContentType NVARCHAR(120) NULL,
                FileSizeBytes BIGINT NULL,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT FK_DailyWorkActivities_Interns FOREIGN KEY (InternId) REFERENCES Interns(InternId)
            );
        END

        IF OBJECT_ID('DailyLocationLogs') IS NULL
        BEGIN
            CREATE TABLE DailyLocationLogs (
                LocationLogId INT IDENTITY(1,1) PRIMARY KEY,
                InternId INT NOT NULL,
                LogDate DATE NOT NULL,
                LoggedAt DATETIME2 NOT NULL,
                Latitude DECIMAL(10, 7) NOT NULL,
                Longitude DECIMAL(10, 7) NOT NULL,
                AccuracyMeters DECIMAL(10, 2) NULL,
                AreaName NVARCHAR(250) NULL,
                Source NVARCHAR(40) NULL,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT FK_DailyLocationLogs_Interns FOREIGN KEY (InternId) REFERENCES Interns(InternId)
            );
        END

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Attendance_Date')
            CREATE INDEX IX_Attendance_Date ON Attendance(AttendanceDate);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Attendance_InternId')
            CREATE INDEX IX_Attendance_InternId ON Attendance(InternId);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Attendance_Status')
            CREATE INDEX IX_Attendance_Status ON Attendance(Status);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Interns_Status')
            CREATE INDEX IX_Interns_Status ON Interns(Status);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DailyWorkActivities_Intern_Date')
            CREATE INDEX IX_DailyWorkActivities_Intern_Date ON DailyWorkActivities(InternId, ActivityDate);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DailyLocationLogs_Intern_Date')
            CREATE INDEX IX_DailyLocationLogs_Intern_Date ON DailyLocationLogs(InternId, LogDate, LoggedAt);
        """;
}

public sealed record CredentialUser(
    int CredentialId,
    int? AdminId,
    int? InternId,
    string Username,
    string PasswordHash,
    string Role,
    bool IsActive,
    string DisplayName);
