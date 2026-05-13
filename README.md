# Wisteria Intern Attendance

ASP.NET Core + SQL Server intern attendance system for Wisteria Properties Pvt. Ltd.

## Login

Seed admin account:

- Username: `hr@wisteriaproperties.in`
- Password: set `SeedAdmin__Password` in your environment or update `appsettings.json` locally.

Intern login:

- Admin creates the intern account.
- Intern receives username and password from admin.
- Only admin can change intern profile details or reset intern password.

## Database

Default connection string:

```json
"Server=.\\SQLEXPRESS;Database=WisteriaInternAttendance;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;MultipleActiveResultSets=True"
```

The app creates the database and required tables automatically on first run.

If your active SQL instance is `RAMAKANTA`, update `appsettings.json`:

```json
"Server=.\\RAMAKANTA;Database=WisteriaInternAttendance;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;MultipleActiveResultSets=True"
```

## Run

```powershell
dotnet restore
dotnet run
```

Open the URL shown in the terminal, usually:

```text
http://localhost:5000
```

## Attendance Rules

- Clock in is allowed only up to 10:15 AM.
- Clock in after 10:15 AM is blocked.
- Clock out must be completed on the same date before 11:59 PM.
- Clock out before 7:30 PM is still marked half day.
- No clock in is marked absent by the background worker after the day ends.
- Clock-in records without clock-out after midnight are marked incomplete.
- GPS latitude, longitude, accuracy, and area/site name are stored for clock in and clock out.
- If intern does not enter area/site name, system auto-fills it from GPS reverse geocoding.
- Geofence validation checks intern distance from assigned office/site coordinates before attendance or location ping is accepted.
- Extra location pings are stored in `DailyLocationLogs` throughout the day (manual and auto every 15 minutes from intern panel).
- Intern photos can be uploaded by admin up to 1MB.
- Interns can upload a daily work activity file and comment for senior/admin review.

## Export

Admin can export attendance as `.xls` Excel format.

## Database setup checklist

1. Install SQL Server + SQL Server Management Studio (SSMS).
2. Create/login to SQL instance (for example `SQLEXPRESS`).
3. Update `ConnectionStrings:DefaultConnection` in `appsettings.json`.
4. Run app once using `dotnet run`:
   - Database `WisteriaInternAttendance` is auto-created.
   - Tables are auto-created (including `DailyLocationLogs`).
5. In admin panel, set each intern's:
   - `WorkLocationName`
   - `WorkLatitude`
   - `WorkLongitude`
   This is required for geofence enforcement.
6. Keep SQL backup job enabled (daily `.bak`).

## Deploy on internet (for tomorrow go-live)

Recommended quick path: **Windows VM + IIS + SQL Server**.

1. Provision Windows Server VM (Azure/AWS/Hostinger/Contabo).
2. Install:
   - .NET Hosting Bundle (ASP.NET Core runtime)
   - IIS (Web Server role)
   - SQL Server
3. Publish app:
   ```powershell
   dotnet publish -c Release -o .\publish
   ```
4. Create IIS site:
   - Physical path: published folder
   - App pool: `No Managed Code`
5. Update server `appsettings.json`:
   - production SQL connection string
   - strong `SeedAdmin` password (or remove after first run)
   - optional geofence radius under `Geofence:DefaultRadiusMeters`
6. DNS + SSL:
   - point domain/subdomain (e.g. `attendance.yourdomain.com`) to VM IP
   - install SSL certificate (Let's Encrypt or provider cert)
   - force HTTPS in IIS
7. Open firewall ports:
   - `80` (HTTP for redirect)
   - `443` (HTTPS)
   - keep SQL port private (no public access)
8. Start app and test externally from mobile network:
   - login
   - clock in/out with GPS permission
   - check geofence block behavior
   - check `DailyLocationLogs` inserts in DB

## Production notes

- Browser GPS requires **HTTPS** on internet; without SSL location capture may fail.
- Reverse geocoding uses OpenStreetMap Nominatim API; add reasonable usage limits.
- Replace default admin password immediately after first login.

