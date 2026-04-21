-- NO_TRANSACTION
-- Enable incremental auto-vacuum so BackupCleanupWorker can reclaim pages
-- freed by DELETE without taking an exclusive VACUUM lock on the whole DB.
--
-- SQLite quirk: `PRAGMA auto_vacuum` only takes effect when set on an EMPTY
-- database (before any CREATE TABLE) or when followed by a full VACUUM — on
-- an existing DB, we must VACUUM once here to rewrite the page map so the
-- auto_vacuum mode "sticks". That VACUUM is acceptable: it happens exactly
-- once, at migration time, while nothing is driving load yet (this migration
-- runs during Db.Migrate at startup, before the hosted services start).
--
-- After this migration, subsequent page reclaim uses PRAGMA incremental_vacuum,
-- which is concurrent-safe and returns a small number of pages at a time
-- instead of an exclusive whole-file rewrite.

PRAGMA auto_vacuum = INCREMENTAL;
VACUUM;
