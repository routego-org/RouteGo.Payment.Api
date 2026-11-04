-- =============================================================================
-- V0001__audit_infrastructure.sql
-- Drop-in audit infrastructure. Apply ONCE per database.
-- Then, from each table's migration, call ONE of:
--     SELECT audit.fn_enable_audit('<schema>.<table>', '<pk_column>');     -- audited: the default
--     SELECT audit.fn_exclude_from_audit('<schema>.<table>', '<reason>');  -- excluded, reason recorded
--         (the narrow, reviewed exception -- references/audit.md, "Excluding a table from the audit")
--
-- NOTE: user/role/permission provisioning is handled by the DB admin out of
-- band — it is NOT part of this script or the migration history. The objects
-- below are owned by whoever runs this script.
--
-- REQUIRED EXTERNAL COMPONENT — the audit partition scheduler (non-negotiable #28).
-- audit.change_log is partitioned by month. If the next month's partition does not
-- exist when the month begins, every write to every audited table in this database
-- fails until it is created: the capture INSERT runs inside the BEFORE trigger, so
-- the business write rolls back with it, in every application sharing the database.
-- This script ships the functions; YOU ship the thing that calls
-- audit.fn_ensure_partition_runway() on a schedule — pg_cron where the platform has
-- it (RDS, Aurora, Neon; it lives inside the database and cannot be forgotten in a
-- new environment's IaC), EventBridge -> Lambda otherwise — plus the run-always
-- migration R__audit_partition_runway.sql so every deploy tops the runway up, plus
-- an alert on the runway query in references/audit.md. A database with this schema
-- and no scheduled caller is not production-ready. The DEFAULT partition below is
-- the circuit breaker if all of that fails: writes land there instead of failing,
-- and a row in it is the alarm.
-- =============================================================================

BEGIN;

CREATE SCHEMA IF NOT EXISTS audit;

-- Where this skill's procedures live (non-negotiable #10). No other migration creates
-- `jobs`, so it is created here, in the first script. (The pipeline's migration journal is
-- not here: the runner keeps it at public.schema_versions, which needs no schema created first.)
CREATE SCHEMA IF NOT EXISTS jobs;

-- -----------------------------------------------------------------------------
-- The immutable, monthly-partitioned change log.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit.change_log (
    id          bigint      GENERATED ALWAYS AS IDENTITY,
    occurred_at timestamptz NOT NULL,
    schema_name text        NOT NULL,
    table_name  text        NOT NULL,
    operation   text        NOT NULL CHECK (operation IN ('INSERT','UPDATE','DELETE')),
    row_pk      text        NOT NULL,
    old_data    jsonb,
    new_data    jsonb,
    action      text        NOT NULL,     -- the business action the app declared (app.action GUC)
    actor_id    text        NOT NULL,     -- opaque actor id, same 64-char shape as *_by_id
    actor_email text        NOT NULL,
    actor_name  text        NOT NULL,
    db_user     text        NOT NULL,
    tenant_id   uuid,                         -- null unless the tenancy skill populates it
    txid        bigint      NOT NULL,
    reason      text,                         -- optional context (app.reason GUC); last so an upgraded
                                              -- database (audit-reason.sql) has the same column order
    PRIMARY KEY (occurred_at, id),
    CONSTRAINT ck_change_log_actor_id_len CHECK (length(actor_id) <= 64)
) PARTITION BY RANGE (occurred_at);

CREATE INDEX IF NOT EXISTS ix_change_log_row
    ON audit.change_log (schema_name, table_name, row_pk, occurred_at);

-- Immutability: nobody but the SECURITY DEFINER function (running as owner) writes here.
REVOKE ALL ON audit.change_log FROM PUBLIC;

-- Circuit breaker: rows for a month with no partition land here instead of failing the
-- write. Empty in normal operation. ANY row here means the runway ran out — alert on
--   SELECT EXISTS (SELECT 1 FROM audit.change_log_default);
-- and follow the recovery runbook in references/audit.md: fn_ensure_partition for that
-- month will refuse ("would be violated by some row") while the default holds its rows,
-- so they have to be moved into the new partition first.
CREATE TABLE IF NOT EXISTS audit.change_log_default PARTITION OF audit.change_log DEFAULT;

-- -----------------------------------------------------------------------------
-- Partition helpers. fn_ensure_partition creates one month; fn_ensure_partition_runway
-- creates the current month plus the next p_months. Both pin timezone = 'UTC': the
-- FOR VALUES bounds are date literals that Postgres converts to timestamptz using the
-- CREATING session's TimeZone, so two partitions made from sessions in different zones
-- tile with a gap (failed writes for its duration) or an overlap (the next CREATE fails).
-- Pinned, every bound is UTC midnight regardless of who runs it from where.
-- Idempotent — safe from a scheduler, the run-always migration, and a hand-run alike.
--   SELECT audit.fn_ensure_partition_runway(12);   -- what the scheduler and R__ script call
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION audit.fn_ensure_partition(p_month date)
RETURNS void
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
    v_start date := date_trunc('month', p_month)::date;
    v_end   date := (date_trunc('month', p_month) + interval '1 month')::date;
    v_name  text := format('change_log_%s', to_char(v_start, 'YYYY_MM'));
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'audit' AND c.relname = v_name
    ) THEN
        EXECUTE format(
            'CREATE TABLE audit.%I PARTITION OF audit.change_log FOR VALUES FROM (%L) TO (%L)',
            v_name, v_start, v_end);
    END IF;
END;
$$;

-- Runway helper: the current month plus the next p_months. Returns how many were missing
-- (0 is the healthy steady state — and a number a dashboard can trend). Deliberately does
-- NOT catch the failure that occurs when the DEFAULT partition holds rows for a month being
-- created: that failure IS the alarm, and the runbook in references/audit.md is the fix.
CREATE OR REPLACE FUNCTION audit.fn_ensure_partition_runway(p_months int DEFAULT 12)
RETURNS int
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
    v_created int := 0;
    v_month   date;
    v_i       int;
BEGIN
    IF p_months < 1 THEN
        RAISE EXCEPTION 'fn_ensure_partition_runway: p_months must be >= 1 (got %)', p_months
            USING ERRCODE = 'P0001';
    END IF;

    FOR v_i IN 0..p_months LOOP
        v_month := (date_trunc('month', now()) + make_interval(months => v_i))::date;
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'audit'
              AND c.relname = format('change_log_%s', to_char(v_month, 'YYYY_MM'))
        ) THEN
            PERFORM audit.fn_ensure_partition(v_month);
            v_created := v_created + 1;
        END IF;
    END LOOP;

    RETURN v_created;
END;
$$;

-- Twelve months of runway up front (an empty partition is a few KB and a catalog row). The
-- R__audit_partition_runway.sql run-always script and the scheduler keep it there.
SELECT audit.fn_ensure_partition_runway(12);

-- -----------------------------------------------------------------------------
-- The optional reason (app.reason GUC). Unlike the actor and the action, a missing
-- reason is fine -- it is null. A reason that IS given is held to the same rule the
-- writer factory applies: at most 500 characters of ASCII letters, digits, spaces and
-- . , - _ : / # ( ). Enforced here rather than by a CHECK on change_log because adding
-- a CHECK to an existing change_log scans every partition under an exclusive lock that
-- blocks every audited write -- and the capture functions are the only writers anyway.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION audit.fn_current_reason() RETURNS text
LANGUAGE plpgsql
STABLE
SET search_path = audit, pg_temp
AS $$
DECLARE
    v_reason text := nullif(btrim(current_setting('app.reason', true)), '');
BEGIN
    IF v_reason IS NOT NULL
       AND (length(v_reason) > 500 OR v_reason !~ '^[A-Za-z0-9 .,:/#()_-]+$') THEN
        RAISE EXCEPTION 'app.reason is not a valid audit reason — at most 500 characters of letters, digits, spaces and . , - _ : / # ( )'
            USING ERRCODE = 'P0001';
    END IF;

    RETURN v_reason;
END;
$$;

-- -----------------------------------------------------------------------------
-- The single stamp + capture trigger function.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION audit.fn_stamp_and_capture() RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = audit, pg_temp
AS $$
DECLARE
    v_id     text := nullif(btrim(current_setting('app.actor_id',    true)), '');
    v_email  text := nullif(current_setting('app.actor_email', true), '');
    v_name   text := nullif(current_setting('app.actor_name',  true), '');
    v_action text := nullif(btrim(current_setting('app.action',      true)), '');
    v_pkcol  text := COALESCE(TG_ARGV[0], 'id');
    v_reason text := audit.fn_current_reason();      -- optional; raises only if given and invalid
BEGIN
    IF v_id IS NULL THEN
        RAISE EXCEPTION 'app.actor_* GUCs not set — every write must run inside a request transaction'
            USING ERRCODE = 'P0001';
    END IF;

    IF v_action IS NULL THEN
        RAISE EXCEPTION 'app.action GUC not set — every write must declare the action that caused it'
            USING ERRCODE = 'P0001';
    END IF;

    -- No unbounded text: the actor and the action are capped where they enter the log, so
    -- a DELETE -- which stamps nothing on the row, so no column CHECK sees it -- cannot carry
    -- an oversize value into change_log either. Same caps as the audit columns' CHECKs.
    IF length(v_id) > 64 OR length(v_email) > 254 OR length(v_name) > 256 OR length(v_action) > 100 THEN
        RAISE EXCEPTION 'app.actor_* / app.action too long — actor id <= 64, email <= 254, name <= 256, action <= 100 characters'
            USING ERRCODE = 'P0001';
    END IF;

    IF TG_OP = 'INSERT' THEN
        NEW.created_by_id := v_id; NEW.created_by_email := v_email; NEW.created_by_name := v_name;
        NEW.created_at := now();
        NEW.updated_by_id := v_id; NEW.updated_by_email := v_email; NEW.updated_by_name := v_name;
        NEW.updated_at := NEW.created_at;
    ELSIF TG_OP = 'UPDATE' THEN
        NEW.created_by_id := OLD.created_by_id; NEW.created_by_email := OLD.created_by_email;
        NEW.created_by_name := OLD.created_by_name; NEW.created_at := OLD.created_at;
        NEW.updated_by_id := v_id; NEW.updated_by_email := v_email; NEW.updated_by_name := v_name;
        NEW.updated_at := now();
    END IF;

    INSERT INTO audit.change_log (
        occurred_at, schema_name, table_name, operation, row_pk,
        old_data, new_data, action, actor_id, actor_email, actor_name, db_user, tenant_id, txid, reason)
    VALUES (
        transaction_timestamp(), TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP,
        COALESCE(to_jsonb(NEW), to_jsonb(OLD)) ->> v_pkcol,
        CASE WHEN TG_OP <> 'INSERT' THEN to_jsonb(OLD) END,
        CASE WHEN TG_OP <> 'DELETE' THEN to_jsonb(NEW) END,
        v_action,
        v_id, v_email, v_name,
        session_user,
        nullif(current_setting('app.tenant_id', true), '')::uuid,
        txid_current(),
        v_reason);

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

-- -----------------------------------------------------------------------------
-- Split-shape functions for tables with GENERATED ... STORED columns.
-- Stored generated columns are computed AFTER the BEFORE-row triggers run, so a
-- BEFORE capture would snapshot them into new_data as null (INSERT) or stale
-- (UPDATE) — the audit record would silently disagree with the real row. For
-- those tables fn_enable_audit attaches fn_stamp (BEFORE) + fn_capture (AFTER)
-- instead of the single fn_stamp_and_capture trigger. Same transaction either
-- way: if the write later fails, the capture rolls back with it.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION audit.fn_stamp() RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    v_id     text := nullif(btrim(current_setting('app.actor_id',    true)), '');
    v_email  text := nullif(current_setting('app.actor_email', true), '');
    v_name   text := nullif(current_setting('app.actor_name',  true), '');
    v_action text := nullif(btrim(current_setting('app.action',      true)), '');
BEGIN
    IF v_id IS NULL THEN
        RAISE EXCEPTION 'app.actor_* GUCs not set — every write must run inside a request transaction'
            USING ERRCODE = 'P0001';
    END IF;

    IF v_action IS NULL THEN
        RAISE EXCEPTION 'app.action GUC not set — every write must declare the action that caused it'
            USING ERRCODE = 'P0001';
    END IF;

    IF TG_OP = 'INSERT' THEN
        NEW.created_by_id := v_id; NEW.created_by_email := v_email; NEW.created_by_name := v_name;
        NEW.created_at := now();
        NEW.updated_by_id := v_id; NEW.updated_by_email := v_email; NEW.updated_by_name := v_name;
        NEW.updated_at := NEW.created_at;
    ELSIF TG_OP = 'UPDATE' THEN
        NEW.created_by_id := OLD.created_by_id; NEW.created_by_email := OLD.created_by_email;
        NEW.created_by_name := OLD.created_by_name; NEW.created_at := OLD.created_at;
        NEW.updated_by_id := v_id; NEW.updated_by_email := v_email; NEW.updated_by_name := v_name;
        NEW.updated_at := now();
    END IF;

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

CREATE OR REPLACE FUNCTION audit.fn_capture() RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = audit, pg_temp
AS $$
DECLARE
    v_id     text := nullif(btrim(current_setting('app.actor_id',    true)), '');
    v_email  text := nullif(current_setting('app.actor_email', true), '');
    v_name   text := nullif(current_setting('app.actor_name',  true), '');
    v_action text := nullif(btrim(current_setting('app.action',      true)), '');
    v_pkcol  text := COALESCE(TG_ARGV[0], 'id');
    v_reason text := audit.fn_current_reason();      -- optional; raises only if given and invalid
BEGIN
    -- fn_stamp already validated the GUCs; re-check defensively in case this
    -- trigger is ever attached on its own.
    IF v_id IS NULL OR v_action IS NULL THEN
        RAISE EXCEPTION 'app.actor_* / app.action GUCs not set — every write must run inside a request transaction'
            USING ERRCODE = 'P0001';
    END IF;

    -- No unbounded text: the actor and the action are capped where they enter the log, so
    -- a DELETE -- which stamps nothing on the row, so no column CHECK sees it -- cannot carry
    -- an oversize value into change_log either. Same caps as the audit columns' CHECKs.
    IF length(v_id) > 64 OR length(v_email) > 254 OR length(v_name) > 256 OR length(v_action) > 100 THEN
        RAISE EXCEPTION 'app.actor_* / app.action too long — actor id <= 64, email <= 254, name <= 256, action <= 100 characters'
            USING ERRCODE = 'P0001';
    END IF;

    INSERT INTO audit.change_log (
        occurred_at, schema_name, table_name, operation, row_pk,
        old_data, new_data, action, actor_id, actor_email, actor_name, db_user, tenant_id, txid, reason)
    VALUES (
        transaction_timestamp(), TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP,
        COALESCE(to_jsonb(NEW), to_jsonb(OLD)) ->> v_pkcol,
        CASE WHEN TG_OP <> 'INSERT' THEN to_jsonb(OLD) END,
        CASE WHEN TG_OP <> 'DELETE' THEN to_jsonb(NEW) END,
        v_action,
        v_id, v_email, v_name,
        session_user,
        nullif(current_setting('app.tenant_id', true), '')::uuid,
        txid_current(),
        v_reason);

    RETURN NULL;   -- AFTER row triggers ignore the return value
END;
$$;

-- -----------------------------------------------------------------------------
-- Audit exclusions. "Every business table is audited" is the rule; a few tables
-- genuinely are not the kind of thing anyone asks "who changed this, and when?"
-- about -- a static list seeded by migrations, a machine-written append-only
-- record, a projection rebuilt from already-audited sources. This registry is how
-- such a table is declared, and it is what makes the CI gate and the checkers PASS
-- for it instead of failing.
--
-- It is a table, not a linter config, because the CI gate and
-- check_schema_conventions.py run against a database with no repo in reach -- the
-- same reason the entity-file path lives in a table COMMENT. Every row carries a
-- reason, so the database itself answers "why is this one not audited?" for an
-- operator reading it years later, with no migration history to hand.
--
-- Written only by migrations, as the owner; nothing at runtime reads it. Owner-only
-- by default, like every table -- the DBA grants SELECT to the role that runs the
-- ad-hoc checker (provisioning is out of band, never migration content).
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit.exclusions (
    schema_name text        NOT NULL,
    table_name  text        NOT NULL,
    reason      text        NOT NULL,     -- why this table needs no change history
    excluded_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_exclusions PRIMARY KEY (schema_name, table_name),
    CONSTRAINT ck_exclusions_reason_nonblank CHECK (length(btrim(reason)) > 0)
);

-- fn_exclude_from_audit: declare a table exempt from change tracking, with its reason.
-- Binary by design -- a table is fully audited or not audited at all -- so it REFUSES
-- a table that carries any of the eight audit columns or a zz_audit* trigger, and never
-- drops them for you: dropping columns is destructive (expand/contract, non-negotiable
-- #8) and belongs in a migration a reviewer can see. Idempotent; re-running updates the
-- reason. To audit the table later, call fn_enable_audit -- it retires the row.
CREATE OR REPLACE FUNCTION audit.fn_exclude_from_audit(p_qualified_table text, p_reason text)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_schema  text;
    v_relname text;
    v_found   text;
BEGIN
    IF p_reason IS NULL OR length(btrim(p_reason)) = 0 THEN
        RAISE EXCEPTION 'fn_exclude_from_audit(%): a reason is required -- it is recorded in audit.exclusions and read by whoever asks later why this table has no history',
            p_qualified_table USING ERRCODE = 'P0001';
    END IF;

    SELECT n.nspname, c.relname INTO v_schema, v_relname
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = p_qualified_table::regclass;

    SELECT string_agg(a.attname, ', ' ORDER BY a.attnum) INTO v_found
    FROM pg_attribute a
    WHERE a.attrelid = p_qualified_table::regclass AND a.attnum > 0 AND NOT a.attisdropped
      AND a.attname IN ('created_by_id', 'created_by_email', 'created_by_name', 'created_at',
                        'updated_by_id', 'updated_by_email', 'updated_by_name', 'updated_at');
    IF v_found IS NOT NULL THEN
        RAISE EXCEPTION 'fn_exclude_from_audit(%): table carries audit columns (%) -- an excluded table carries none; a table is audited or excluded, never half',
            p_qualified_table, v_found USING ERRCODE = 'P0001';
    END IF;

    SELECT string_agg(g.tgname, ', ') INTO v_found
    FROM pg_trigger g
    WHERE g.tgrelid = p_qualified_table::regclass AND NOT g.tgisinternal
      AND g.tgname IN ('zz_audit', 'zz_audit_capture');
    IF v_found IS NOT NULL THEN
        RAISE EXCEPTION 'fn_exclude_from_audit(%): table carries the audit trigger (%) -- drop it in this migration first, or keep the table audited',
            p_qualified_table, v_found USING ERRCODE = 'P0001';
    END IF;

    INSERT INTO audit.exclusions (schema_name, table_name, reason)
    VALUES (v_schema, v_relname, btrim(p_reason))
    ON CONFLICT ON CONSTRAINT pk_exclusions DO UPDATE SET reason = EXCLUDED.reason;
END;
$$;

-- -----------------------------------------------------------------------------
-- fn_enable_audit: ensure the 8 audit columns exist (NOT NULL) and attach the
-- right trigger shape — the single BEFORE stamp+capture trigger, or the split
-- BEFORE-stamp / AFTER-capture pair when the table has GENERATED ... STORED
-- columns. Idempotent — safe to call from every table migration. Re-run it
-- after adding a stored generated column to an already-audited table (the CI
-- shape check in references/testing.md catches a forgotten re-run). Retires any
-- audit exclusion the table held: audited or excluded, never both.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION audit.fn_enable_audit(p_qualified_table text, p_pk text DEFAULT 'id')
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_has_generated boolean;
    v_schema        text;
    v_relname       text;
    v_col           text;
    v_cap           int;
    v_ckname        text;
BEGIN
    -- Columns (add if missing). New tables should declare them explicitly; this backstops.
    -- The *_by_id columns are text, not uuid: the actor id is whatever the authenticating
    -- issuer minted (IdP subject claim, employee code, service-client name) and is capped
    -- at 64 chars by a CHECK below — see references/audit.md.
    EXECUTE format($f$
        ALTER TABLE %1$s
            ADD COLUMN IF NOT EXISTS created_by_id    text,
            ADD COLUMN IF NOT EXISTS created_by_email text,
            ADD COLUMN IF NOT EXISTS created_by_name  text,
            ADD COLUMN IF NOT EXISTS created_at       timestamptz,
            ADD COLUMN IF NOT EXISTS updated_by_id    text,
            ADD COLUMN IF NOT EXISTS updated_by_email text,
            ADD COLUMN IF NOT EXISTS updated_by_name  text,
            ADD COLUMN IF NOT EXISTS updated_at       timestamptz
    $f$, p_qualified_table);

    -- The length cap on each actor column: id 64, email 254 (the longest deliverable
    -- address), name 256 (the longest display name the common IdPs issue). There is no
    -- unbounded text. Postgres has no ADD CONSTRAINT IF NOT EXISTS, so guard on
    -- pg_constraint to keep this function re-runnable.
    -- Unqualified relation name, so the constraint matches the ck_{table}_{rule}
    -- convention a hand-written CREATE TABLE would produce.
    SELECT n.nspname, c.relname INTO v_schema, v_relname
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = p_qualified_table::regclass;

    -- Audited or excluded, never both: enabling audit retires any exclusion the table held.
    -- (Auditing a previously excluded table later is exactly this call.)
    DELETE FROM audit.exclusions WHERE schema_name = v_schema AND table_name = v_relname;

    FOREACH v_col IN ARRAY ARRAY['created_by_id', 'created_by_email', 'created_by_name',
                                 'updated_by_id', 'updated_by_email', 'updated_by_name'] LOOP
        v_cap := CASE WHEN v_col LIKE '%\_id'    THEN 64
                      WHEN v_col LIKE '%\_email' THEN 254
                      ELSE 256 END;
        -- left(..., 63) because Postgres truncates identifiers to 63 chars; doing it here
        -- keeps the name we look up identical to the name we create, so a long table name
        -- can't break idempotency.
        v_ckname := left(format('ck_%s_%s_len', v_relname, v_col), 63);
        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid = p_qualified_table::regclass AND conname = v_ckname
        ) THEN
            EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I CHECK (length(%I) <= %s)',
                           p_qualified_table, v_ckname, v_col, v_cap);
        END IF;
    END LOOP;

    -- Does the table have any GENERATED ... STORED columns?
    SELECT EXISTS (
        SELECT 1 FROM pg_attribute
        WHERE attrelid = p_qualified_table::regclass
          AND attgenerated = 's' AND NOT attisdropped
    ) INTO v_has_generated;

    -- Triggers (drop + recreate so the pk argument and shape stay current).
    EXECUTE format('DROP TRIGGER IF EXISTS zz_audit ON %s', p_qualified_table);
    EXECUTE format('DROP TRIGGER IF EXISTS zz_audit_capture ON %s', p_qualified_table);

    IF v_has_generated THEN
        -- Split shape: stamp BEFORE, capture AFTER so new_data sees the
        -- computed generated columns.
        EXECUTE format(
            'CREATE TRIGGER zz_audit BEFORE INSERT OR UPDATE OR DELETE ON %s '
            'FOR EACH ROW EXECUTE FUNCTION audit.fn_stamp()',
            p_qualified_table);
        EXECUTE format(
            'CREATE TRIGGER zz_audit_capture AFTER INSERT OR UPDATE OR DELETE ON %s '
            'FOR EACH ROW EXECUTE FUNCTION audit.fn_capture(%L)',
            p_qualified_table, p_pk);
    ELSE
        EXECUTE format(
            'CREATE TRIGGER zz_audit BEFORE INSERT OR UPDATE OR DELETE ON %s '
            'FOR EACH ROW EXECUTE FUNCTION audit.fn_stamp_and_capture(%L)',
            p_qualified_table, p_pk);
    END IF;
END;
$$;

-- NOTE: declare the 8 audit columns as NOT NULL in each table's own CREATE TABLE, along
-- with the two 64-char CHECK constraints on created_by_id / updated_by_id.
-- fn_enable_audit() adds the columns as nullable only as a backstop for retrofits; a fresh
-- table should ship them NOT NULL from the start (the trigger always populates them). The
-- length constraints are added by fn_enable_audit() either way, so a retrofit is capped too.
--
-- NOTE: the narrow exception is a table declared with audit.fn_exclude_from_audit() instead --
-- it declares NONE of the eight columns, and its reason is recorded in audit.exclusions. See
-- references/audit.md, "Excluding a table from the audit", for when that is legitimate.

COMMIT;
