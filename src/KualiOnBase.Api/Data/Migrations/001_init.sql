CREATE TABLE IF NOT EXISTS ImportJobs (
    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
    DocumentId        TEXT    NOT NULL,
    OnBaseDocType     TEXT    NOT NULL,
    TargetFolderPath  TEXT    NOT NULL,
    DownloadMode      TEXT    NOT NULL,
    DeleteAttachments INTEGER NOT NULL,
    DeleteDocument    INTEGER NOT NULL,
    KeywordsJson      TEXT    NULL,
    Status            TEXT    NOT NULL,
    AttemptCount      INTEGER NOT NULL DEFAULT 0,
    NextAttemptAt     TEXT    NULL,
    LastError         TEXT    NULL,
    BackupFolderPath  TEXT    NULL,
    ProducedFiles     TEXT    NULL,
    CreatedAt         TEXT    NOT NULL,
    UpdatedAt         TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_ImportJobs_Status_NextAttemptAt
    ON ImportJobs (Status, NextAttemptAt);

CREATE INDEX IF NOT EXISTS IX_ImportJobs_Status_UpdatedAt
    ON ImportJobs (Status, UpdatedAt);

CREATE TABLE IF NOT EXISTS JobEvents (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    JobId       INTEGER NOT NULL,
    At          TEXT    NOT NULL,
    Kind        TEXT    NOT NULL,
    Message     TEXT    NULL,
    PayloadJson TEXT    NULL,
    FOREIGN KEY (JobId) REFERENCES ImportJobs (Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_JobEvents_JobId_Id
    ON JobEvents (JobId, Id);

CREATE TABLE IF NOT EXISTS ExportCallbacks (
    CorrelationId TEXT    PRIMARY KEY,
    DocumentId    TEXT    NOT NULL,
    Status        TEXT    NOT NULL,
    SignedUrl     TEXT    NULL,
    ErrorMessage  TEXT    NULL,
    CreatedAt     TEXT    NOT NULL,
    UpdatedAt     TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_ExportCallbacks_CreatedAt
    ON ExportCallbacks (CreatedAt);
