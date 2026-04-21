-- 004 added PdfExport to carry a per-job option string. That knob went away
-- when we collapsed downloadMode to pdf|attachments (the Kuali tenant setting
-- is the real switch for merge behavior). Drop the unused column.
ALTER TABLE ImportJobs DROP COLUMN PdfExport;
