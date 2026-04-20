CREATE TABLE IF NOT EXISTS JobEvents (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    JobId        INTEGER NOT NULL,
    At           TEXT    NOT NULL,
    Kind         TEXT    NOT NULL,
    Message      TEXT,
    PayloadJson  TEXT,
    FOREIGN KEY (JobId) REFERENCES ImportJobs(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_JobEvents_JobId ON JobEvents(JobId, Id);
