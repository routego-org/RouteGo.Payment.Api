#!/bin/bash
# Applies migrations/ to one database — the stand-in for a CI/CD DbUp step until a pipeline exists.
# The application never migrates; the "migrate" service in docker-compose.yml runs this before the
# app starts. Same contract as the local runner.
#
# Connection: the standard libpq variables PGHOST, PGPORT, PGDATABASE, PGUSER, PGPASSWORD.
#
# It follows the rules migrations/README.md records for any runner of this folder:
#   - V####__*.sql run once each, in sequence order, recorded in public.schema_versions
#     (the same journal table DbUp keeps, so a later pipeline continues this history);
#   - R__*.sql run on every run, after every V####, and are not recorded;
#   - every script owns its BEGIN/COMMIT, so nothing here wraps it in a transaction;
#   - no variable substitution is applied to the scripts;
#   - a missing or empty folder is an error, never a silent no-op.
set -euo pipefail

MIGRATIONS_DIR="${MIGRATIONS_DIR:-/migrations}"

fail() {
  echo "apply-migrations: $1" >&2
  exit 1
}

[ -d "$MIGRATIONS_DIR" ] || fail "no migrations folder at $MIGRATIONS_DIR"

shopt -s nullglob
versioned=( "$MIGRATIONS_DIR"/V*__*.sql )
run_always=( "$MIGRATIONS_DIR"/R__*.sql )
[ "${#versioned[@]}" -gt 0 ] || fail "no V####__*.sql scripts in $MIGRATIONS_DIR"

PSQL=(psql --no-psqlrc --quiet --set ON_ERROR_STOP=1)

"${PSQL[@]}" <<'SQL'
CREATE TABLE IF NOT EXISTS public.schema_versions (
    schemaversionsid serial PRIMARY KEY,
    scriptname varchar(255) NOT NULL,
    applied timestamp NOT NULL
);
SQL

# Sequence order: V0001, V0002, ... (zero-padded, so a version sort is the numeric order).
mapfile -t ordered < <(printf '%s\n' "${versioned[@]}" | sort -V)

applied_count=0
for script in "${ordered[@]}"; do
  name="$(basename "$script")"
  already="$("${PSQL[@]}" --tuples-only --no-align --set name="$name" \
    <<< "SELECT count(*) FROM public.schema_versions WHERE scriptname = :'name';")"
  if [ "$already" != "0" ]; then
    continue
  fi
  echo "applying $name"
  "${PSQL[@]}" --file "$script"
  "${PSQL[@]}" --set name="$name" \
    <<< "INSERT INTO public.schema_versions (scriptname, applied) VALUES (:'name', now() AT TIME ZONE 'utc');"
  applied_count=$((applied_count + 1))
done
echo "$applied_count versioned script(s) applied"

for script in "${run_always[@]}"; do
  echo "running $(basename "$script")"
  "${PSQL[@]}" --file "$script"
done
echo "done"
