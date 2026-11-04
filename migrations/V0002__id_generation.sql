-- =============================================================================
-- V0002__id_generation.sql
-- Readable, prefixed surrogate IDs: '<prefix>_<epoch-seconds>_<3 random chars>'
-- e.g. agc_1787127977_VLR. Apply ONCE per database, after V0001.
--
-- Tables opt in at design time (the implementer ASKS the user - uuid stays the
-- default; see references/schema-conventions.md, "Identifier strategy"):
--     id text NOT NULL DEFAULT public.fn_generate_id('agc'),
--     CONSTRAINT ck_<table>_id_format CHECK (id ~ '^agc_[0-9]{10}_[2-9A-HJ-NP-Z]{3}$')
--
-- SCOPE: human-scale tables only (rows created by people through a UI -
-- agencies, offices, business entities). Uniqueness within one second rests on
-- 3 random chars = 32,768 combinations per table: a burst above ~25 inserts per
-- second on one table gives a ~1% chance of a PK collision (SQLSTATE 23505 -
-- one failed insert, not corruption). The retry is the standard one: re-run the
-- whole open->work->commit unit through the writer factory (pre-commit, so
-- legal under non-negotiable #22). Machine-rate tables keep uuid.
--
-- PLACEMENT: public, deliberately. Cross-cutting id infrastructure used by
-- every business schema - the same class as extensions and the audit helpers,
-- not business logic (the placement walk does not apply). public stays off the
-- search path, so every call is schema-qualified.
-- =============================================================================

BEGIN;

-- gen_random_bytes() lives in pgcrypto (only gen_random_uuid() is core).
-- Available on RDS/Aurora; extension objects land in public, which is fine -
-- public holds no table but the migration journal and stays off the search path.
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE OR REPLACE FUNCTION public.fn_generate_id(p_prefix text)
RETURNS text
LANGUAGE plpgsql
AS $$
DECLARE
    -- Readable base32: 0/1/I/O removed so an ID can't be misread or mistyped;
    -- uppercase output keeps the random part visually distinct from the
    -- lowercase prefix. (Not Crockford - Crockford keeps 0/1 and drops L/U.)
    safe_chars   CONSTANT text := '23456789ABCDEFGHJKLMNPQRSTUVWXYZ';
    time_part    text;
    rand_part    text := '';
    random_bytes bytea;
    i            int;
BEGIN
    -- 1-4 lowercase letters only. One canonical form: the implementer
    -- normalizes case BEFORE the prefix reaches a migration, so 'off' and
    -- 'OFF' can never coexist as two spellings of the same table's prefix.
    IF p_prefix IS NULL OR p_prefix !~ '^[a-z]{1,4}$' THEN
        RAISE EXCEPTION 'invalid id prefix "%": must be 1-4 lowercase letters', p_prefix;
    END IF;

    -- 10-digit Unix epoch seconds, zero-padded: fixed width until year 2286,
    -- so lexicographic order == chronological order. clock_timestamp(), not
    -- now(): now() is frozen per transaction, which would give every row of a
    -- batch insert the same time part and lean entirely on the random chars.
    time_part := lpad((EXTRACT(EPOCH FROM clock_timestamp())::bigint)::text, 10, '0');

    -- 3 random readable-base32 chars. 256 % 32 = 0, so get_byte() % 32 draws
    -- uniformly - no modulo bias.
    random_bytes := gen_random_bytes(3);
    FOR i IN 1..3 LOOP
        rand_part := rand_part || substr(safe_chars, (get_byte(random_bytes, i - 1) % 32) + 1, 1);
    END LOOP;

    RETURN p_prefix || '_' || time_part || '_' || rand_part;
END;
$$;

COMMIT;
