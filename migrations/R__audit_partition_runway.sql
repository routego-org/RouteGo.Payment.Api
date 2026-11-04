-- =============================================================================
-- R__audit_partition_runway.sql   (copy verbatim into migrations/ under exactly this name)
--
-- RUN-ALWAYS. The R__ prefix routes it through DbUp as ScriptType.RunAlways
-- (the pipeline's DbUp step must route R__*.sql as RunAlways): it executes on EVERY run, after
-- all V#### scripts, and is never journaled. Every deploy therefore tops the audit
-- partition runway up to twelve months, so the scheduler (pg_cron / EventBridge) is a
-- second line of defence rather than the only one — and the Layer-1 migration-apply
-- test (references/testing.md) asserts the runway on every CI run.
--
-- This does NOT replace the scheduler. Deploys are not guaranteed monthly; the
-- scheduler is. A database with this schema and no scheduled caller of
-- audit.fn_ensure_partition_runway() is not production-ready (non-negotiable #28).
--
-- Idempotent: fn_ensure_partition_runway creates only what is missing and returns the
-- count. If it FAILS with "updated partition constraint for default partition would be
-- violated by some row", the DEFAULT partition holds rows for a month that has no
-- partition — the runway ran out at some point — and the recovery runbook in
-- references/audit.md applies before this can succeed.
-- =============================================================================

BEGIN;

SELECT audit.fn_ensure_partition_runway(12);

COMMIT;
