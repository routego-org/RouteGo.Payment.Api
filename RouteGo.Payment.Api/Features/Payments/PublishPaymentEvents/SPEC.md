# CAP-PAY-004 — Publish payment events

- Capability: CAP-PAY-004
- Canonical spec: `spec-RouteGo.Payment.API/capabilities/payments/features/payment-event-publishing.md` (v0.1.1)
- Area: Payments
- Slice: `Features/Payments/PublishPaymentEvents/`

## Contract

`kind: worker` — no HTTP operation. Trigger `schedule: every 5 seconds`, run in-process by
`PublishPaymentEventsService` (a `BackgroundService`); one run is `PublishPaymentEventsHandler.HandleAsync`.

- Reads: `payments.payment_events` rows with no `sent_at`, in recorded order (`recorded_at`, then `event_id`).
- Sends to: the payment events stream (SNS `routego-payment-events`, key `PaymentEventsTopicArn`) for
  `PaymentSucceeded`, `PaymentFailed`, `RefundCompleted`; the audit events stream (SNS `routego-audit-events`,
  key `AuditEventsTopicArn`) for `AuditRecorded`. Message attribute `eventType`; RouteGo envelope
  (`eventId`, `eventType`, `version` 1, `occurredAt` = recorded time, `correlationId`, `producer` `payment-service`, `data`).
- Writes: `sent_at` only, as the system actor with audit action `job:PublishPaymentEvents` (the table is excluded from audit).
- Emits: `PAYMENT_EVENT_DELAYED` — a worker code, one Error log line (at most one a minute) naming the oldest unsent
  event's time and the lag in seconds.

## Acceptance criteria

Each criterion maps to tests in `PublishPaymentEvents.Tests.cs`, run against a real Postgres.

- [x] AC-1: A recorded event is sent with the envelope — `HandleAsync_RecordedPaymentEvent_SendsItWithTheEnvelopeAndMarksItSent`
- [x] AC-2: Audit records go to the audit events stream — `HandleAsync_AuditRecord_GoesToTheAuditStreamAndTheRefundToThePaymentStream`
- [x] AC-3: No change without its event, even across a crash — `HandleAsync_EventRecordedBeforeAStop_IsSentByTheRunAfterTheRestart`
- [x] AC-4: Events are sent in the order recorded — `HandleAsync_TwoEventsRecordedInOrder_SendsThemInThatOrder`
- [x] AC-5: A resent event keeps its event ID — `HandleAsync_EventSentButNeverAcknowledged_IsSentAgainWithTheSameEventId`
- [x] AC-6: Payment keeps working while messaging is down — `HandleAsync_MessagingDownThenBack_HoldsEveryEventBackThenSendsAllInOrder` (the changes themselves are written by the other slices without touching messaging)
- [x] AC-7: Alert when more than 5 minutes behind — `HandleAsync_OldestUnsentEventOverFiveMinutesOld_RaisesTheDelayAlert`, `HandleAsync_OldestUnsentEventUnderFiveMinutesOld_RaisesNoAlert`

## Invariants touched

- One publisher at a time across every running copy: a transaction-scoped advisory lock; a copy that does not get it does nothing.
- A send that fails holds back every later event (nothing is skipped).
- Ordering across different payments is not promised; within a payment, each event is stamped at its own insert (`clock_timestamp()`).
