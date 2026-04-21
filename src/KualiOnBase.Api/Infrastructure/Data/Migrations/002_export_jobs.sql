CREATE TABLE IF NOT EXISTS ExportCallbacks (
    CorrelationId TEXT    PRIMARY KEY,
    DocumentId    TEXT    NOT NULL,
    Status        TEXT    NOT NULL,  -- Pending | Completed | Failed
    SignedUrl     TEXT,
    ErrorMessage  TEXT,
    CreatedAt     TEXT    NOT NULL,
    UpdatedAt     TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ExportCallbacks_CreatedAt
  ON ExportCallbacks(CreatedAt);
