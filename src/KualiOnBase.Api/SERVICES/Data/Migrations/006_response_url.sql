-- Kuali Build's "long-running integration" pattern: the initial workflow POST
-- carries an X-Response-URL header. After we return 202, the workflow pauses on
-- the integration step until we POST a terminal status to that URL. We capture
-- the URL on enqueue and stamp KualiNotifiedAt the first time we successfully
-- post the callback so we never double-fire.
ALTER TABLE ImportJobs ADD COLUMN ResponseUrl TEXT;
ALTER TABLE ImportJobs ADD COLUMN KualiNotifiedAt TEXT;
