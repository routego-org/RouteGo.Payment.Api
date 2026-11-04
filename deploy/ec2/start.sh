#!/usr/bin/env bash
# Build the image, apply migrations, start the API, then wait for /health/ready.
# Usage on the one-server layout:  sudo bash ~/payment/deploy/ec2/start.sh
# Usage (three-instance layout, on routego-secure):
#   sudo GATEWAY_BASE_URL=http://<edge-private-ip>:8081/ \
#        RETURN_URL_TEMPLATE='https://<cloudfront-domain>/bookings/{bookingId}/payment-return' \
#        bash ~/payment/deploy/ec2/start.sh
set -euo pipefail

cd "$(dirname "$0")"
REGION=ap-south-1
DB_SECRET=database/postgresql/routego-payment-api

SHARED_NETWORK=ec2_default
POSTGRES_CONTAINER=ec2-postgres-1
POSTGRES_SUPERUSER=notification_user

# One-server layout: MockGateway runs beside Payment on the shared ec2_default network and is reached
# by name; the customer reaches its pay page through the portal's own address (nginx routes /pay/ to
# it). Set a variable to override (the three-instance layout uses the edge instance's private address).
PORTAL_BASE_URL="${PORTAL_BASE_URL:-$(aws ssm get-parameter --name /routego/prod/identity/portal-base-url --region "$REGION" \
  --query Parameter.Value --output text)}"
GATEWAY_BASE_URL="${GATEWAY_BASE_URL:-http://mock-gateway:8080/}"
RETURN_PATH='/my-bookings/{bookingId}/payment-return'   # kept out of ${...:-} because its braces would end the expansion
RETURN_URL_TEMPLATE="${RETURN_URL_TEMPLATE:-${PORTAL_BASE_URL%/}$RETURN_PATH}"
PUBLIC_BASE_URL="${PUBLIC_BASE_URL:-${PORTAL_BASE_URL%/}}"

if ! docker network inspect "$SHARED_NETWORK" >/dev/null 2>&1; then
  echo "ERROR: Docker network $SHARED_NETWORK not found. Is the Postgres container ($POSTGRES_CONTAINER) running?" >&2
  exit 1
fi

# The migrate container needs the connection details; the instance role reads them from the same
# secret the API uses, so the password is never written to a file on this server.
echo ">> Reading the database connection from Secrets Manager"
secret_json="$(aws secretsmanager get-secret-value --secret-id "$DB_SECRET" --region "$REGION" \
  --query SecretString --output text)"
read_field() {
  printf '%s' "$secret_json" | python3 -c 'import json, sys; print(json.load(sys.stdin).get(sys.argv[1], sys.argv[2]))' "$1" "${2:-}"
}
PAYMENT_DB_HOST="$(read_field host)"
PAYMENT_DB_PORT="$(read_field port 5432)"
PAYMENT_DB_NAME="$(read_field database)"
PAYMENT_DB_USER="$(read_field username)"
PAYMENT_DB_PASSWORD="$(read_field password)"
PAYMENT_DB_SSLMODE="$(read_field sslmode require)"
AWS_ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text)"
export PAYMENT_DB_HOST PAYMENT_DB_PORT PAYMENT_DB_NAME PAYMENT_DB_USER PAYMENT_DB_PASSWORD PAYMENT_DB_SSLMODE
export AWS_ACCOUNT_ID GATEWAY_BASE_URL RETURN_URL_TEMPLATE PUBLIC_BASE_URL
# MockGateway must present the same API key Payment reads from the gateway secret.
GATEWAY_API_KEY="$(aws secretsmanager get-secret-value --secret-id routego/payment/gateway --region "$REGION" \
  --query SecretString --output text | python3 -c 'import json, sys; print(json.load(sys.stdin)["apiKey"])')"
export GATEWAY_API_KEY

# Payment refuses to start without its secrets, and without its queues it could not receive results,
# so stop here with a clear message instead of a container that restarts forever.
echo ">> Checking the secrets and queues the API needs"
for secret in routego/payment/gateway routego/internal-key; do
  if ! aws secretsmanager describe-secret --secret-id "$secret" --region "$REGION" >/dev/null 2>&1; then
    echo "ERROR: secret $secret is missing, or this instance role cannot read it." >&2
    exit 1
  fi
done
for queue in payment-webhooks-q payment-refund-q; do
  if ! aws sqs get-queue-url --queue-name "$queue" --region "$REGION" >/dev/null 2>&1; then
    echo "ERROR: queue $queue is missing, or this instance role cannot reach it." >&2
    exit 1
  fi
done

echo ">> Creating the database login and database (skipped when they exist)"
docker exec -i -e DB_NAME="$PAYMENT_DB_NAME" -e DB_USER="$PAYMENT_DB_USER" -e DB_PASSWORD="$PAYMENT_DB_PASSWORD" \
  "$POSTGRES_CONTAINER" psql --no-psqlrc --quiet -U "$POSTGRES_SUPERUSER" -d postgres < create-database.sql

echo ">> Building images (first time takes 5-10 minutes)"
docker build -t routego/payment:latest ../..
docker build -f ../../tools/MockGateway/Dockerfile -t routego/mock-gateway:latest ../..

echo ">> Applying migrations and starting the API"
if ! docker compose up -d; then
  echo "ERROR: migrations or API start failed. Last logs:" >&2
  docker compose ps -a
  docker compose logs --tail 50 migrate payment
  exit 1
fi

echo ">> Waiting for the API to be ready"
for _ in $(seq 1 30); do
  if curl -fs http://localhost:5006/health/ready >/dev/null 2>&1; then
    docker compose ps -a
    echo ">> DEPLOY OK — Payment API is ready on port 5006"
    exit 0
  fi
  sleep 2
done

echo "ERROR: Payment API did not become ready. Last logs:" >&2
docker compose ps -a
docker compose logs --tail 50 migrate payment
exit 1
