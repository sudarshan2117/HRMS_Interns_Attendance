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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DailyLocationLogs_Intern_Date')
    CREATE INDEX IX_DailyLocationLogs_Intern_Date ON DailyLocationLogs(InternId, LogDate, LoggedAt);
