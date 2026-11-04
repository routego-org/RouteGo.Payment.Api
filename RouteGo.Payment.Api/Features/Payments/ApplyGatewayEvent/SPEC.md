# CAP-PAY-003 — Apply gateway events

- Capability: CAP-PAY-003
- Canonical spec: `spec-RouteGo.Payment.API/capabilities/payments/features/payment-results.md` (v0.1.1)
- Area: Payments
- Slice: `Features/Payments/ApplyGatewayEvent/`

## Contract

`kind: worker` — no HTTP operation. Trigger `queue: payment-webhooks` (`payment-webhooks-q`, key `WebhookQueueUrl`),
read in-process by `ApplyGatewayEventService`; one event is `ApplyGatewayEventHandler.HandleAsync`.

- Message: the Webhook Receiver (`routego-webhook-receiver:CAP-WHK-001@0.1.0`, `queued-reports.md`) queues the gateway's report as received, with labels beside it as message attributes (`gatewayEventId`, `receivedAt`, `correlationId`). Payment reads facts from the body, never the labels; correlation ID = the `correlationId` label. The gateway's real field names are not fixed by any spec; until they are, the mock gateway's body is read:
  `{ "gatewayEventId": "...", "gatewayRef": "...", "outcome": "Succeeded" | "Failed", "amount": 1600, "reason": "...", "occurredAt": "2026-10-16T15:00:00Z" }`
  — `reason` and `occurredAt` optional, `amount` whole rupees. The body is kept as received in `gateway_events.payload`.
- Writes: `payments.gateway_events`, `payments.payments` (status, failure_reason), `payments.payment_events` — one transaction.
- Emits (log alerts): `PAYMENT_EVENT_REJECTED` (reason `amountMismatch`, `alreadySettled`), `PAYMENT_WEBHOOK_PARKED`
  (reason `invalidEventData`, `unknownPayment`, `repeatedFailure`; raised by the queue worker).
- Duplicates are counted as log lines containing `duplicate ignored`.

## Acceptance criteria

Each criterion maps to tests in `ApplyGatewayEvent.Tests.cs`, run against a real Postgres.

- [x] AC-1: A success settles the payment and is announced — `HandleAsync_SuccessForACreatedPayment_SettlesItAndRecordsPaymentSucceeded`
- [x] AC-2: A failure settles the payment and is announced — `HandleAsync_FailureForACreatedPayment_SettlesItAsFailedWithTheReason`, `HandleAsync_FailureWithNoReason_KeepsTheDefaultReason`
- [x] AC-3: The same event five times takes effect once — `HandleAsync_TheSameEventFiveTimes_TakesEffectOnce`
- [x] AC-4: Two copies at the same moment take effect once — `HandleAsync_TwentyCopiesOfOneEventAtTheSameMoment_TakeEffectOnce`
- [x] AC-5: A different event reporting the same outcome changes nothing — `HandleAsync_DifferentEventReportingTheSameOutcome_IsRecordedAndChangesNothing`
- [x] AC-6: An amount that differs is rejected and alarmed — `HandleAsync_AmountThatDiffers_IsRejectedAlertedAndNotRetried`
- [x] AC-7: An opposite outcome after settlement is rejected and alarmed — `HandleAsync_OppositeOutcomeAfterSettlement_IsRejectedAndAlerted`, `HandleAsync_SuccessAfterAFailure_IsRejectedAndAlerted`
- [x] AC-8: A success after the payment page validity is still honoured — `HandleAsync_SuccessLongAfterThePaymentPageLapsed_IsStillHonoured`
- [x] AC-9: Payment stopped while the customer paid — the message waits on `payment-webhooks-q` (SQS) and the worker reads it on start; not testable without SQS, covered by the queue's own semantics
- [x] AC-10: A message Payment cannot use is parked with an alert — `Read_MessageThatIsNotAUsableReport_ComesBackWithAReasonAndNoEvent` (set aside at once), `HandleAsync_PageNoPaymentHas_IsPostponedAndNothingIsRecorded` (retried, then parked by the queue's redrive)
- [x] AC-11: A failure half-way leaves nothing half-done — `HandleAsync_FaultAfterNotingTheEvent_LeavesNothingHalfDoneAndTheRedeliveryAppliesItOnce`
- [x] AC-12: Temporary failure is retried, then parked — the handler throws, the message stays, the redrive policy (5 receives) parks it and the worker logs `PAYMENT_WEBHOOK_PARKED` with reason `repeatedFailure` (`EventQueueService`)

## Invariants touched

- `gateway_events.gateway_event_id` is the primary key: the record's refusal of a repeat is absolute (ADR 0007). The payment row is locked first, so events about one payment never interleave.
- A payment settles once; decisions live in `Domain/GatewayEventRules`.
- An unknown payment page is postponed, not recorded: the page can exist at the gateway a moment before Payment commits the payment.
- Deviation from the spec text, same outcome: an unreadable message is set aside on the dead-letter queue at once rather than after five tries, because retrying garbage cannot help.
