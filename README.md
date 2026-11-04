# RouteGo.Payment.Api

The Payment service of RouteGo: it takes a customer's money for a booking through an outside payment
gateway, applies the gateway's results exactly once in effect, and returns money when Booking asks.
Card and UPI details never reach RouteGo. API plus background workers; no screen, no public route.

- Product specs: `spec-RouteGo.Payment.API` (the source of truth; `00-overall-architecture.md` and `06-payment.md` here are the system documents it was derived from, and the specs win where they differ — see ADRs 0002, 0008, 0009, 0010 there)
- Port 5006, database `routego_payment`, runs on `routego-secure`
- Register of what is built: `specs/INDEX.md`

## What it does

| Capability | What | How it is reached |
|---|---|---|
| CAP-PAY-001 `CreatePayment` | Start one payment per booking, get the gateway page | `POST /internal/payments` (Booking) |
| CAP-PAY-002 `GetPayment` | Read a booking's payment and refunds | `GET /internal/bookings/{bookingId}/payment` (Booking) |
| CAP-PAY-003 `ApplyGatewayEvent` | Apply each gateway result once | queue `payment-webhooks-q` |
| CAP-PAY-004 `PublishPaymentEvents` | Send recorded events, in order, at least once | every 5 seconds |
| CAP-RFD-001 `ProcessRefund` | Return money once per refund key | queue `payment-refund-q` (Booking's `RefundRequested`) |

Every HTTP operation needs `X-Internal-Key`. Health: `GET /health` (liveness), `GET /health/ready`
(database and both queues).

## Layout

```
RouteGo.Payment.Api/          the service (Features/{Area}/{Action}/ vertical slices, Domain/, Common/, Infrastructure/)
RouteGo.Payment.Api.Tests/    tests: real Postgres (Testcontainers), the real host, fakes for the gateway and the event stream
RouteGo.Payment.Api.AppHost/  dev-only Aspire dashboard
tools/MockGateway/            the classroom gateway: pay page, signed webhooks, Replay
migrations/                   raw SQL, applied by the pipeline's DbUp step (never by the app)
local/  docker-compose.yml    a complete local environment
deploy/ec2/                   deployment to routego-secure
```

## Run it locally

```bash
docker compose up -d --build        # Postgres, migrations, LocalStack, MockGateway, Payment on :5006
# when another stack already holds 4566 / 5433:   LOCALSTACK_PORT=4567 docker compose up -d
```

Start a payment, pay on the page the answer gives (`checkoutUrl`), and read the event:

```bash
curl -H "X-Internal-Key: local-internal-key" -H "Content-Type: application/json" \
     -d '{"bookingId":"7b0e3a54-1c11-4a52-9a3e-5d5a0a1f2b77","amount":1600,"currency":"INR","customerEmail":"priya@example.com"}' \
     http://localhost:5006/internal/payments
# open the checkoutUrl, click Success; or click "Replay last result x5": still one PaymentSucceeded
docker compose exec localstack awslocal sqs receive-message --queue-url http://localhost:4566/000000000000/booking-payment-q
```

Running the API from the IDE against the compose dependencies: `docker compose up -d postgres migrate localstack mock-gateway`, then set the secrets once
(`dotnet user-secrets set --id routego-platform` for `database/postgresql/routego-payment-api`, `routego/payment/gateway` and `routego/internal-key`) and `dotnet run --project RouteGo.Payment.Api`.

Class demos: replay x5 (one effect), "Take me back, send the result 5 seconds later" (why the return page polls),
`POST http://localhost:5099/admin/refunds-failing/true` (a refund the gateway fails once, then retried).

## The gateway-result message (for the Webhook Receiver)

The Webhook Receiver has no spec yet. After checking the sender, it must put exactly this JSON on `payment-webhooks-q`
(MockGateway sends the same body straight to the queue until the receiver exists):

```json
{ "gatewayEventId": "evt_123", "gatewayRef": "mock_7b0e…", "outcome": "Succeeded", "amount": 1600, "reason": null, "occurredAt": "2026-10-31T06:18:13Z" }
```

`outcome` is `Succeeded` or `Failed`; `amount` is whole rupees; `reason` and `occurredAt` are optional. `gatewayEventId` is the repeat key.

## Configuration

Flat environment variables (`Infrastructure/ConfigKeys.cs`); nothing is read from appsettings. Secrets by fixed name
(Development: user-secrets / environment; elsewhere: Secrets Manager): `database/postgresql/routego-payment-api`,
`routego/payment/gateway` (`{apiKey, webhookSecret}`), `routego/internal-key`.

## Tests and checks

```bash
dotnet test
python .agents/skills/skills-aspnetcore-project-setup/scripts/check_setup.py --root RouteGo.Payment.Api --stack api
python .agents/skills/skills-aspnetcore-api-vertical-slice/scripts/check_structure.py --root RouteGo.Payment.Api
python .agents/skills/skills-db-postgres/scripts/check_migrations.py migrations
```
