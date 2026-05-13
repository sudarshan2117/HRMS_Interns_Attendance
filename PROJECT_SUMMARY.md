# Wisteria Intern Attendance - Project Summary

## Overview

Wisteria Intern Attendance is an ASP.NET Core `.NET 9` web application for managing intern attendance, location-based punch-in/out, intern profiles, daily work uploads, and HR/admin reporting.

The current project uses PostgreSQL/Supabase through `Npgsql`. The frontend is a static single-page app built with HTML, CSS, and JavaScript inside `wwwroot`.

## Users

- Admin/HR
  - Create interns
  - Manage intern profiles
  - View attendance
  - View location logs
  - Export attendance
  - Reset passwords
  - View daily work uploads

- Intern
  - Complete profile
  - Punch in manually with location
  - Punch out manually with location after 2 hours
  - Upload daily work
  - View monthly attendance history

## Current Attendance Behavior

- Punch In is manual.
- Punch Out is manual.
- Browser location is captured only when the intern clicks a punch button.
- Intern must enter a location/site name before punch.
- Coordinates are saved in Decimal Degrees format.
- Coordinates are Google Maps compatible.
- Punch Out is disabled until 2 hours after Punch In.
- Late status is calculated after 10:00 AM.
- Half Day status is calculated if Punch Out is before 6:00 PM.
- Working hours are calculated from actual Punch In and Punch Out times.

## Location Format

The app stores:

- Latitude
- Longitude
- Accuracy meters
- Area/site name

Preferred coordinate format:

```text
latitude, longitude
18.589123, 73.789456
```

Google Maps link format:

```text
https://www.google.com/maps/search/?api=1&query=18.589123,73.789456
```

## Important Files

- `Program.cs`
  - Main backend.
  - API routes, authentication, database initialization, attendance logic, profile validation, location validation, export, and background worker.

- `wwwroot/index.html`
  - Main UI structure.
  - Contains admin pages, intern pages, profile form, attendance screen, and tables.

- `wwwroot/app.js`
  - Main frontend behavior.
  - Handles API calls, login, page switching, punch actions, geolocation, profile submission, and rendering.

- `wwwroot/styles.css`
  - Main styling.
  - Handles dashboard, sidebar, tables, forms, attendance buttons, status light, and mobile layout.

- `appsettings.json`
  - Configuration and connection string.

- `WisteriaInternAttendance.csproj`
  - Project dependencies and build settings.

- `Database/schema.sql`
  - Schema reference.

- `Models`
  - Model and DbContext files.

- `Migrations`
  - Entity Framework migration history.

- `publish`
  - Published output for deployment.

## Main Database Tables

- `admins`
- `interns`
- `usercredentials`
- `attendance`
- `dailylocationlogs`
- `dailyworkactivities`

## Run Commands

Restore packages:

```powershell
dotnet restore
```

Run locally:

```powershell
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

## Production Notes

- Use HTTPS for GPS location capture.
- Keep Supabase/PostgreSQL credentials private.
- Change default admin password before production use.
- Do not commit `.vs`, `bin`, `obj`, or secret config files.
- Back up database and uploaded files.
- Test on a real mobile device after deployment.

## Suggested Next Work

- Update `README.md` to match the current PostgreSQL/Supabase setup.
- Add admin attendance correction feature.
- Add Google Maps buttons for every saved location.
- Add pagination and search filters.
- Add unit tests for attendance rules.
- Move database code out of `Program.cs` into service classes.
- Improve password handling by removing plain-password storage.
- Add audit logs for admin changes.
