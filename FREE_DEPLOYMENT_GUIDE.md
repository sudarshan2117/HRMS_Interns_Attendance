# Free Deployment Guide - Wisteria Intern Attendance

## Recommended Free Setup

Use this combination:

- App hosting: Render Free Web Service
- Database: Supabase Free PostgreSQL
- Deployment method: GitHub repository connected to Render
- Runtime: Docker using the existing `Dockerfile`

This is the easiest free setup for this project because the app already uses PostgreSQL through `Npgsql`, and `Program.cs` already supports cloud hosting by reading the `PORT` environment variable.

## Important Free Hosting Reality

Free hosting is suitable for demo, testing, and small internal use.

It is not perfect production hosting:

- Render free web services sleep after inactivity.
- First request after sleep can take around one minute.
- Render free Postgres expires after 30 days, so use Supabase instead for the database.
- Supabase free database is limited, but good enough for a small attendance system.
- Local uploaded files on Render can disappear after restart because free web services do not provide persistent disks.

For this project, the database will be safe in Supabase, but uploaded files should later be moved to Supabase Storage, Cloudinary, or another persistent storage service.

## Current Project Readiness

Already ready:

- Dockerfile exists.
- App uses `.NET 9`.
- App supports `PORT` environment variable.
- App supports PostgreSQL/Supabase.
- Database schema is auto-created by the app.
- Health endpoint exists:

```text
/api/health/startup
```

Added for deployment:

- `render.yaml`

This file tells Render how to deploy the app as a Docker web service.

## Step 1 - Prepare Supabase Database

1. Go to Supabase.
2. Create a free account.
3. Create a new project.
4. Choose a strong database password.
5. Wait for the project to finish provisioning.
6. Open Project Settings.
7. Go to Database.
8. Copy the PostgreSQL connection string.

Use the pooler connection string if possible. It usually looks like this:

```text
Host=aws-1-ap-south-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.xxxxx;Password=YOUR_PASSWORD;SSL Mode=Require;Trust Server Certificate=true;Pooling=true;Maximum Pool Size=20;Timeout=5;Command Timeout=30
```

Keep this private.

## Step 2 - Prepare GitHub Repository

1. Push this project to GitHub.
2. Make sure these files are included:

```text
Dockerfile
render.yaml
WisteriaInternAttendance.csproj
Program.cs
wwwroot/
Models/
Migrations/
Database/
```

3. Do not commit private secrets if possible.
4. Do not commit generated folders:

```text
bin/
obj/
publish/
.vs/
```

## Step 3 - Create Render Web Service

1. Go to Render.
2. Sign in.
3. Click New.
4. Choose Blueprint if you want Render to read `render.yaml`.
5. Connect your GitHub repository.
6. Render should detect the web service from `render.yaml`.
7. Choose the Free plan.

If not using Blueprint:

1. Click New.
2. Select Web Service.
3. Connect GitHub repository.
4. Runtime: Docker.
5. Plan: Free.
6. Auto Deploy: Yes.

## Step 4 - Add Environment Variables in Render

Open the Render service settings and add:

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__DefaultConnection=YOUR_SUPABASE_CONNECTION_STRING
SeedAdmin__Name=Wisteria HR
SeedAdmin__Username=hr@wisteriaproperties.in
SeedAdmin__Password=CHANGE_THIS_STRONG_PASSWORD
Geofence__DefaultRadiusMeters=250
```

Important:

- Use double underscore `__` for nested .NET config keys.
- Do not put quotes around values in the Render dashboard.
- Change the seed admin password before deployment.

## Step 5 - Deploy

1. Click Deploy.
2. Wait for the Docker build to finish.
3. Open the Render URL.
4. The first load may be slow on the free plan.
5. Visit:

```text
https://your-render-url.onrender.com/api/health/startup
```

Expected result:

```json
{
  "status": "ok"
}
```

If status is `db_init_failed`, check the Supabase connection string.

## Step 6 - First Login

Login using the seed admin credentials from Render environment variables:

```text
Username: SeedAdmin__Username
Password: SeedAdmin__Password
```

After first login:

- Confirm admin dashboard opens.
- Create one test intern.
- Login as intern.
- Complete profile.
- Test Punch In with mobile browser.
- Confirm location permission works.
- Confirm admin attendance table shows the punch.

## Step 7 - Mobile GPS Testing

For location capture:

- Use HTTPS Render URL.
- Test on a real phone.
- Allow browser location permission.
- Keep GPS/location enabled on the device.

Browser GPS usually does not work properly on plain HTTP public URLs. Render gives HTTPS automatically, so this is good for your app.

## Step 8 - Known Limitations on Free Hosting

### Render Free Web Service

- Sleeps after inactivity.
- Slow first request after sleep.
- Limited monthly free hours.
- No persistent disk on free service.

### Supabase Free Database

- Limited database size.
- Free projects can pause after inactivity.
- No production SLA on free plan.

### Uploaded Files

The app currently stores uploads inside:

```text
wwwroot/uploads/
```

On free Render, these files can be lost when the service restarts or redeploys.

Recommended future fix:

- Store intern photos and daily work files in Supabase Storage.
- Save only the public/private file URL in PostgreSQL.

## Best Free Deployment Architecture

```text
User Browser
    |
    | HTTPS
    v
Render Free Web Service
    |
    | PostgreSQL SSL connection
    v
Supabase Free PostgreSQL
```

## Production Checklist

Before sharing with interns:

- Change default admin password.
- Use Render environment variables for secrets.
- Confirm Supabase connection works.
- Test on mobile network, not only laptop Wi-Fi.
- Test Punch In and Punch Out.
- Confirm location appears in admin panel.
- Confirm attendance export works.
- Create backup plan for database.
- Move uploads to persistent storage before relying on file uploads.

## My Recommendation

For your current project, use:

```text
Render Free Web Service + Supabase Free PostgreSQL
```

This is the best free path right now because:

- Your app is already Docker-ready.
- Your app is already PostgreSQL-ready.
- Render gives HTTPS automatically.
- HTTPS is required for reliable browser GPS.
- Supabase gives a real PostgreSQL database for free.

When the project becomes important for daily business, upgrade to:

```text
Render paid web service + Supabase Pro
```

or:

```text
Low-cost VPS + PostgreSQL + Nginx/IIS + SSL
```
