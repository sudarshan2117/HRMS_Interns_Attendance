# Wisteria Intern Attendance - Project Documentation

## 1. Project Purpose

Wisteria Intern Attendance is a web-based attendance and intern management system for Wisteria Properties Pvt. Ltd.

The application is built for two main users:

- Admin/HR: creates intern accounts, manages intern profiles, checks attendance, checks location logs, checks daily work uploads, exports attendance, and resets passwords.
- Intern: completes profile, manually punches in with location, manually punches out with location after the allowed time, uploads daily work, and views monthly attendance history.

The system stores attendance with location data in Decimal Degrees format:

- Latitude range: `-90` to `90`
- Longitude range: `-180` to `180`
- Example: `18.589123, 73.789456`

This format is compatible with Google Maps and other mapping systems.

## 2. Technology Stack

- Backend: ASP.NET Core Minimal API
- Target framework: `.NET 9`
- Database: PostgreSQL, configured for Supabase
- Database provider: `Npgsql`
- Frontend: Static HTML, CSS, and JavaScript inside `wwwroot`
- Authentication: Cookie authentication with role-based access
- Roles: `Admin`, `Intern`
- Location capture: Browser Geolocation API
- Reverse geocoding: OpenStreetMap Nominatim service
- Deployment support: Dockerfile and publish folder support

## 3. Main Application Flow

### Admin Flow

1. Admin logs in using HR credentials.
2. Admin dashboard shows active interns, present count, late count, half-day count, absent count, monthly status, and recent activity.
3. Admin creates intern account with name, joining date, optional end date, optional password, and optional work location coordinates.
4. System creates intern credentials.
5. Admin can view, edit, delete, reset password, and upload intern photo.
6. Admin can open Attendance to see all intern attendance or filter by intern.
7. Admin can open Location Logs to see location points captured during punch actions.
8. Admin can export attendance to Excel.
9. Admin can view daily work uploads from interns.

### Intern Flow

1. Intern logs in using credentials created by admin.
2. If the intern profile is incomplete, the profile form is shown first.
3. Intern completes profile:
   - Office number with country code and 10 digit number
   - Personal number with country code and 10 digit number
   - College name
   - Stream / branch
   - Year
   - Semester
   - Internship domain
   - Duration: `3 months` or `6 months`
4. After profile completion, attendance controls are shown.
5. Intern enters location name, for example `Head office`, `Baner site`, or `Wakad site`.
6. Intern clicks `Punch In With Location`.
7. Browser asks for location permission.
8. System stores:
   - Punch-in time
   - Latitude
   - Longitude
   - Accuracy
   - Intern-entered area/site name
   - Late flag if punch-in is after 10:00 AM
9. Punch Out remains disabled until 2 hours after punch-in.
10. After 2 hours, intern can click `Punch Out With Location`.
11. System stores:
   - Punch-out time
   - Punch-out latitude
   - Punch-out longitude
   - Punch-out accuracy
   - Area/site name
   - Working minutes
   - Final attendance status
12. Intern can upload daily work activity and view monthly attendance history.

## 4. Attendance Rules

- Attendance is manual. The system does not automatically punch in or punch out.
- Location is captured only when the intern clicks Punch In or Punch Out.
- Intern must enter a location/site name before punching.
- Punch In is allowed once per day.
- Punch Out is allowed only after Punch In.
- Punch Out is disabled for the first 2 hours after Punch In.
- Punch In after 10:00 AM is marked Late.
- Punch Out before 6:00 PM is marked Half Day.
- Working hours are calculated using actual punch-in and punch-out time.
- If the intern punches in but does not punch out, the record can be marked Incomplete by the background worker.
- If the intern does not punch in, the background worker can mark Absent.

## 5. Location Handling

The app uses browser GPS through `navigator.geolocation.getCurrentPosition`.

Stored location fields include:

- Latitude
- Longitude
- Accuracy in meters
- Area/site name

The frontend displays stored coordinates as Google Maps search links:

```text
https://www.google.com/maps/search/?api=1&query={latitude},{longitude}
```

If an area name is provided by the intern, that name is saved. If needed, the backend can resolve an area name from coordinates using reverse geocoding.

Coordinate validation is enforced on the backend:

- Latitude must be between `-90` and `90`.
- Longitude must be between `-180` and `180`.

## 6. Authentication and Authorization

The app uses cookie authentication.

Admin users can access:

- `/api/admin/*`
- Admin dashboard
- Intern management
- Attendance export
- Location logs
- Activity reports

Intern users can access:

- `/api/intern/*`
- Profile completion
- Attendance punch
- Daily work upload
- Personal monthly attendance history

## 7. Database Tables

The PostgreSQL schema is created from `Program.cs` during application initialization.

Important tables:

- `admins`: stores admin profile and login identity.
- `interns`: stores intern profile, internship dates, status, work location, photo path, and profile details.
- `usercredentials`: stores login username, password hash, plain password for admin visibility, role, active state, and last login.
- `attendance`: stores daily attendance record, punch-in/out time, status, working minutes, location coordinates, accuracy, and area names.
- `dailylocationlogs`: stores location logs captured from attendance actions.
- `dailyworkactivities`: stores intern daily work comments and uploaded file details.

## 8. Folder and File Explanation

### Root Folder

The root folder contains the main project files, configuration, documentation, and deployment files.

- `Program.cs`
  - Main backend file.
  - Contains API routes, authentication setup, database initialization, attendance logic, location logic, export logic, and helper classes.
  - This is the most important backend file in the project.

- `WisteriaInternAttendance.csproj`
  - .NET project file.
  - Defines target framework, NuGet packages, and build settings.
  - Current target framework is `net9.0`.

- `WisteriaInternAttendance.sln`
  - Visual Studio solution file.
  - Used to open the project in Visual Studio.

- `appsettings.json`
  - Main configuration file.
  - Contains connection string, seed admin settings, attendance/geofence settings, and runtime configuration.

- `appsettings.Development.json`
  - Development-specific configuration.
  - Used when running locally in development mode.

- `Dockerfile`
  - Defines container build and runtime steps.
  - Useful for deployment to container hosting platforms.

- `.dockerignore`
  - Excludes unnecessary files from Docker build context.

- `dotnet-tools.json`
  - Local tool configuration.

- `attendance.db`
  - Old/local SQLite database artifact.
  - The current code uses PostgreSQL/Supabase, so this should not be treated as the main production database.

- `README.md`
  - Existing readme.
  - Some older parts may mention SQL Server and old timing rules; this new documentation reflects the current project state.

- `read2.md`, `read3.md`
  - Earlier handoff/summary notes.
  - Useful for historical context.

### `wwwroot`

This folder contains the frontend application.

- `wwwroot/index.html`
  - Main single-page application HTML.
  - Contains login screen, admin pages, intern pages, attendance controls, profile form, and tables.

- `wwwroot/app.js`
  - Main frontend JavaScript.
  - Handles login, page navigation, API calls, intern management, attendance punch actions, location capture, profile submission, and table rendering.

- `wwwroot/styles.css`
  - Main stylesheet.
  - Controls layout, dashboard design, sidebar, tables, attendance buttons, profile form, phone fields, and responsive styles.

- `wwwroot/assets`
  - Static assets such as company logo.

- `wwwroot/uploads`
  - Uploaded files served by the app.
  - Includes intern photos and daily work uploads.

### `Models`

This folder contains model and DbContext files.

- `Models/AppDbContext.cs`
  - Entity Framework Core DbContext.
  - Used for EF integration.

- `Models/Attendance.cs`
  - Attendance model class.

- `Models/User.cs`
  - User model class.

The main live database operations are mostly handled directly in `Program.cs` using Npgsql commands.

### `Migrations`

Contains Entity Framework migration files.

These are useful if EF migrations are used, but the current application also contains manual schema creation in `Program.cs`.

### `Database`

- `Database/schema.sql`
  - SQL schema reference.
  - Useful for understanding database structure or rebuilding manually.
  - Some naming may reflect earlier SQL Server-style schema, so compare with current `Program.cs` before production use.

### `Properties`

Contains Visual Studio and publish profile settings.

- `Properties/launchSettings.json`
  - Local run profiles for Visual Studio and `dotnet run`.

- `Properties/PublishProfiles`
  - Visual Studio publish profiles.

### `.github`

Contains GitHub-specific configuration.

- `.github/workflows`
  - CI/CD workflow files if configured.

### `bin`

Build output folder.

Contains compiled `.dll`, `.exe`, dependencies, and runtime files. This folder is generated and should not usually be edited manually.

### `obj`

Intermediate build folder.

Contains temporary build cache, generated assembly files, static web asset cache, and NuGet restore artifacts. This folder is generated and should not be edited manually.

### `publish`

Published application output.

Used when deploying the app to a server, IIS, or hosting platform. This folder is generated by:

```powershell
dotnet publish -c Release -o .\publish
```

### `.vs`

Visual Studio local cache folder.

This is machine-specific and should not be committed to Git.

## 9. Important APIs

### Auth

- `POST /api/auth/login`
- `POST /api/auth/logout`
- `GET /api/auth/me`

### Admin

- `GET /api/admin/dashboard`
- `GET /api/admin/interns`
- `POST /api/admin/interns`
- `PUT /api/admin/interns/{id}`
- `DELETE /api/admin/interns/{id}`
- `PATCH /api/admin/interns/{id}/status`
- `PATCH /api/admin/interns/{id}/password`
- `POST /api/admin/interns/{id}/photo`
- `GET /api/admin/attendance`
- `GET /api/admin/interns/{id}/attendance`
- `GET /api/admin/attendance/export`
- `GET /api/admin/location-logs`
- `GET /api/admin/activities`
- `GET /api/admin/credentials`

### Intern

- `GET /api/intern/profile`
- `PUT /api/intern/profile`
- `GET /api/intern/attendance/today`
- `POST /api/intern/attendance/clock-in`
- `POST /api/intern/attendance/clock-out`
- `GET /api/intern/attendance/monthly`
- `POST /api/intern/location/ping`
- `GET /api/intern/location/logs`
- `POST /api/intern/activities`
- `GET /api/intern/activities`

## 10. Development Workflow

Run locally:

```powershell
dotnet restore
dotnet run
```

Build:

```powershell
dotnet build
```

Publish:

```powershell
dotnet publish -c Release -o .\publish
```

If build fails with file lock errors, stop the running app and shut down build servers:

```powershell
dotnet build-server shutdown
```

Then rebuild.

## 11. Deployment Notes

For production:

- Use HTTPS. Browser GPS may fail without HTTPS.
- Keep database credentials private.
- Change default seed admin password.
- Do not expose PostgreSQL directly to the public internet.
- Back up the database regularly.
- Keep uploaded files backed up if using local file storage.
- Verify location capture on mobile browser after deployment.

## 12. Suggested Improvements

Recommended next improvements:

- Move large database logic from `Program.cs` into separate service/repository classes.
- Replace plain password storage with one-time password display or secure reset flow.
- Add audit logs for admin actions.
- Add map preview links/buttons in admin attendance and location log tables.
- Add attendance correction workflow for admin.
- Add pagination and search for interns, attendance, and location logs.
- Add automated tests for attendance status calculation.
- Add backup strategy for uploaded files.
- Add environment-based configuration for production secrets.
- Update old README sections that still mention SQL Server and older attendance rules.
