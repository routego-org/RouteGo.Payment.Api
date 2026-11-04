# 06. Payment Service

| | |
|---|---|
| **Type** | API (internal) + worker |
| **Build phase** | 6 (with the Webhook Receiver, file 12) |
| **Port** | 5006 |
| **Data** | PostgreSQL `routego_payment`, user `payment_user` |
| **Instance** | `routego-secure` (its own trust zone) |
| **Repo path** | `src/Payment`, plus `tools/MockGateway` |

---

## 1. BRD: why it exists
- **BO-3: never lose a payment.** Every successful payment must end in a confirmed booking or an automatic refund.
- Gateways deliver results by asynchronous webhook, can send duplicates, and may send them while we deploy.
- Payment credentials are the most sensitive thing in the system and need the tightest access (NFR security).

**Business outcome:** money handled exactly once, with a clear trail, in an isolated trust zone.

## 2. PRD: what it offers
| Capability | Behavior |
|---|---|
| Create payment intent | Booking asks for an intent; the customer is sent to the gateway page |
| Process payment result | Webhook outcome updates the payment and notifies Booking |
| Refund | Refund full or partial amount, once |
| Payment status | Booking and support can query the state |

Rules:
- A gateway event is processed **exactly once in effect**, no matter how many times it arrives.
- The amount in the webhook must equal the intent amount; otherwise the event is rejected and flagged.
- A refund request with the same idempotency key creates one refund.
- Card or UPI details never touch our systems (hosted gateway page).

## 3. FRD: how it behaves

### 3.1 Endpoints (internal, `X-Internal-Key` required)
| Method and path | Purpose |
|---|---|
| `POST /internal/payments/intents` | `{bookingId, amount, currency, customerEmail, returnUrl}` → `{paymentId, intentId, checkoutUrl}` |
| `GET /internal/payments/by-booking/{bookingId}` | Status and gateway reference |
| `GET /health`, `/health/ready` | Probes |

No public routes. Refunds are triggered by the `RefundRequested` event, not by HTTP.

### 3.2 Data model
```
payments(id uuid pk, booking_id unique, intent_id, gateway_ref, amount numeric(12,2), currency,
         status, created_at, updated_at)
         -- status: Created | Succeeded | Failed | RefundPending | Refunded | PartiallyRefunded
payment_events(id, gateway_event_id UNIQUE, payment_id, type, payload jsonb, status, received_at, processed_at)
         -- status: Processed | Rejected
refunds(id, payment_id, amount, status, idempotency_key UNIQUE, gateway_refund_id, created_at)
outbox(id, event_type, payload jsonb, created_at, published_at)
processed_messages(message_id pk, processed_at)
```
The **unique constraint on `gateway_event_id`** is the idempotency guarantee (success criterion 3).

### 3.3 Webhook processing (worker on `payment-webhooks-q`)
In one DB transaction:
1. `INSERT INTO payment_events (gateway_event_id, ...)`; on unique violation → commit nothing, delete message, log `duplicate`.
2. Load payment by `intent_id`; compare amount; mismatch → store as `Rejected`, raise metric `PaymentAmountMismatch`, delete message.
3. Update `payments.status` (Succeeded or Failed).
4. Insert outbox row (`PaymentSucceeded` or `PaymentFailed`).
5. Commit; delete the SQS message.
Poison messages (bad JSON, unknown intent) throw; after 5 receives they go to `payment-webhooks-dlq`.

### 3.4 Refund processing (worker on `payment-refund-q`)
1. Consume `RefundRequested {bookingId, amount, reason, idempotencyKey}`.
2. `INSERT INTO refunds` with the idempotency key; duplicate → ignore.
3. Call the gateway refund API (passing the idempotency key).
4. On success: status `Completed`, outbox `RefundCompleted`; on failure: leave the message to retry; DLQ after 5.
5. Publish an `AuditRecorded` event for each refund.

### 3.5 Gateway abstraction and `MockGateway`
`IPaymentGateway { CreateIntent, Refund, VerifySignature }`. Two implementations: `SandboxGateway` (a provider test mode, if the class uses one) and `MockGateway`. `MockGateway` is a small container (`tools/MockGateway`) with:
- a hosted "pay" page with buttons Success, Fail,
- HMAC-signed webhooks sent to a configured URL (local: `sam local start-api`; AWS: the API Gateway URL),
- a **Replay** button that re-sends the last webhook N times (the idempotency demo),
- a "send webhook before the user is redirected back" option (shows why the return page must poll).
On AWS run it on `routego-edge`.

### 3.6 Events
| Direction | Event | Notes |
|---|---|---|
| Consumes | webhook messages (`payment-webhooks-q`), `RefundRequested` (`payment-refund-q`) | |
| Publishes `routego-payment-events` | `PaymentSucceeded`, `PaymentFailed`, `RefundCompleted` | Via outbox |
| Publishes `routego-audit-events` | `AuditRecorded` (refunds) | |

### 3.7 Config
| Where | Key |
|---|---|
| Secrets Manager | `routego/payment/gateway` `{apiKey, webhookSecret}`, `routego/payment/db`, `routego/internal/api-key` |
| SSM `/routego/prod/payment/` | `gateway-base-url`, `intent-expiry-minutes` (15), `webhook-queue-url`, `refund-queue-url` |

## 4. Dependencies
**Depends on (sync):** the external gateway.
**Consumes:** Webhook Lambda output, Booking's `RefundRequested`.

**Depended on by:**
| Who | For what |
|---|---|
| Booking | Create payment intent (sync); `PaymentSucceeded/Failed/RefundCompleted` (events) |
| Notification | `RefundCompleted` email |
| Reporting | Payment and refund facts |
| Audit | Refund audit records |

## 5. AWS services used and why
| AWS service | Use | Without it |
|---|---|---|
| **Secrets Manager** | Gateway key, webhook secret, DB password | Gateway keys in config files |
| **SQS + DLQ** | Webhook intake | A malformed message blocks forever; a message arriving during a deployment is lost |
| **SNS** | Publish payment events | Booking would need to be called directly and be up |
| **IAM (secure role)** | Reads only `routego/payment/*`, its queues, its topics | A compromise of any other service cannot read payment secrets |
| **Separate EC2 trust zone** | Own instance role and security group | One shared role exposes payment secrets to every container |
| **KMS** | Encrypt the secrets and queues | n/a |
| **CloudWatch** | Metrics: `PaymentAmountMismatch`, duplicate count, DLQ depth | Fraud or bug invisible |

**What students learn:** idempotency keys and unique constraints, DLQ handling and redrive, least-privilege IAM, and why payment code deserves a separate trust boundary.

## 6. Before building: AWS setup
1. `routego-secure` instance and `routego-secure-role` (Phase 0).
2. Secrets: `routego/payment/gateway` (apiKey, webhookSecret; use a long random value), `routego/payment/db`.
3. DB `routego_payment`, user, and a unique-constraint-bearing schema via migration.
4. Queues `payment-webhooks-q` (visibility timeout 60 s) and `payment-refund-q` with DLQs. Subscription: booking-events → `payment-refund-q` filter `RefundRequested`. Queue policy for `payment-webhooks-q` allows the Webhook Lambda role to send.
5. Topics `routego-payment-events`, `routego-audit-events` (Phase 0).
6. SSM parameters; ECR repos `routego/payment` and `routego/mock-gateway`; log groups.
7. Alarms: DLQ depth for both queues; custom metric `PaymentAmountMismatch` greater than 0.
8. The Webhook Receiver stack (file 12) deployed so that a public URL exists to give the gateway.

## 7. Local development
1. `docker compose up -d`.
2. Local secrets:
   ```bash
   awslocal secretsmanager create-secret --name routego/payment/gateway --secret-string '{"apiKey":"test","webhookSecret":"local-webhook-secret"}'
   ```
   Queue `payment-webhooks-q` is created by the init script.
3. Run Payment: `dotnet run --project src/Payment -- --migrate-only` then `dotnet run --project src/Payment`.
4. Run MockGateway: `dotnet run --project tools/MockGateway` (port 5099), configured to post webhooks to `http://localhost:3000/webhooks/payment` (the local Lambda).
5. Run the Webhook Lambda locally: `sam local start-api -p 3000` in `lambdas/webhook-receiver` (see file 12), configured to put messages into LocalStack's queue (SAM local needs a Docker network path to LocalStack; use the host address documented in file 12).
   Shortcut before the Lambda exists: MockGateway can push directly to `payment-webhooks-q`.
6. Create an intent with `curl -H "X-Internal-Key: local-internal-key" ...`, open `checkoutUrl`, click Success, and see `PaymentSucceeded` in `booking-payment-q`.
7. Click **Replay x5**: still one `payment_events` row for that gateway event, and still one `PaymentSucceeded`.

## 8. Deploying to AWS
1. Prerequisites above.
2. Create DB and user; secret.
3. Build and push `routego/payment` and `routego/mock-gateway`.
4. SSM parameters.
5. Migrate (one-off container on `routego-secure`).
6. Compose on `routego-secure` (Payment `5006:8080`, log group `/routego/payment`). Compose MockGateway on `routego-edge` (exposed through Nginx at `/mock-gateway/`).
7. Network: `sg-secure` allows 5006 from `sg-core` only (Booking). Nginx does not route to Payment.
8. Point the gateway (or MockGateway) at the API Gateway URL from file 12.
9. Verify:
   - Intent creation from Booking returns a checkout URL.
   - Pay, then a booking-bound `PaymentSucceeded` arrives.
   - `aws sqs get-queue-attributes` on both DLQs show 0.
10. **Break it on purpose:**
    - **Replay the same webhook 5 times** from MockGateway: one row, one event, success criterion 3 met.
    - Drop the unique constraint in a scratch DB and repeat to show duplicates, then restore.
    - Stop Payment, pay, start Payment: the payment is processed from the queue.
    - Send malformed JSON to the queue: it retries 5 times and appears in the DLQ; the alarm fires; fix and redrive.
    - From `routego-core`, try to read `routego/payment/gateway`: access denied (the point of the trust zones).

## 9. Lab checklist
- [ ] Replay x5 gives one effect.
- [ ] Amount mismatch is rejected and alarms.
- [ ] Core instance cannot read the payment secret.
- [ ] Refund idempotency key honored.
