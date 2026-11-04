# CAP-RFD-001 — Process refund

- Capability: CAP-RFD-001
- Canonical spec: `spec-RouteGo.Payment.API/capabilities/refunds/features/refund-processing.md` (v0.1.1)
- Area: Refunds
- Slice: `Features/Refunds/ProcessRefund/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: refund-requested`: `payment-refund-q` (key `RefundQueueUrl`), subscribed to
`routego-booking-events` with filter `RefundRequested`, read in-process by `ProcessRefundService`.

- Event data (Booking's `RefundRequested`): `bookingId`, `paymentId`, `amount` (whole rupees, more than 0), `currency`,
  `reason` (`Cancellation`, `LatePayment`, `SeatsLost`, `TripCancelled`), `idempotencyKey` — Booking's wire name for the refund key.
- Writes: `refunds.refunds`, `payments.payments` (status, refunded_amount), `payments.payment_events` (`RefundCompleted`, then `AuditRecorded`).
- Gateway: `IPaymentGateway.RefundAsync` with the refund key.
- Emits (log alerts, by the queue worker): `REFUND_REQUEST_PARKED` with reason `unknownPayment`, `bookingMismatch`,
  `invalidRequest`, `paymentNotPaid`, `amountExceedsPayment`, `repeatedFailure`, naming booking, payment and key.

## Acceptance criteria

Each criterion maps to tests in `ProcessRefund.Tests.cs`, run against a real Postgres.

- [x] AC-1: A full refund of a late payment — `HandleAsync_FullRefundOfALatePayment_CompletesItRefundsThePaymentAndAnnouncesIt`
- [x] AC-2: A partial refund of a cancellation — `HandleAsync_PartialRefundOfACancellation_LeavesThePaymentPartiallyRefunded`
- [x] AC-3: The same request five times returns the money once — `HandleAsync_TheSameRequestFiveTimes_ReturnsTheMoneyOnceAndAnnouncesOnce`
- [x] AC-4: A refund the gateway failed once is retried — `HandleAsync_GatewayFailsOnce_LeavesItPendingThenCompletesItExactlyOnce`
- [x] AC-5: A refund that keeps failing is parked with an alert — `HandleAsync_GatewayKeepsFailing_StaysPendingAndASuccessAfterTheFixCompletesIt` (postponed with reason `repeatedFailure`; the queue parks it after its last delivery)
- [x] AC-6: Refunds never exceed the payment — `HandleAsync_RefundsTogetherOverThePayment_AreRejectedAndTheGatewayIsNotAsked`
- [x] AC-7: Only a paid payment can be refunded — `HandleAsync_PaymentThatWasNotPaid_IsRejectedAndNothingIsRecorded`
- [x] AC-8: A request Payment cannot match — `HandleAsync_PaymentThatDoesNotExist_IsRejectedAsUnknown`, `HandleAsync_BookingThatIsNotThePaymentsBooking_IsRejectedAsAMismatch`, `HandleAsync_RequestThatIsNotValid_IsRejectedAndNothingIsRecorded`, `HandleAsync_RequestThatCannotBeRead_IsRejectedAsInvalid`
- [x] AC-9: Payment stopped when the request arrived — the request waits on `payment-refund-q` and the worker reads it on start; covered by the queue's semantics
- [x] AC-10: Every completed refund is audited by the system actor — `HandleAsync_CompletedRefund_IsAuditedByTheSystemActorAfterItsOwnAnnouncement`
- [x] AC-11: A later refund after a partial one completes the payment's refunds — `HandleAsync_LaterRefundAfterAPartialOne_CompletesThePaymentsRefunds`
- [x] NFR: Two refunds at the same moment never exceed the payment — `HandleAsync_TwoRefundsTogetherOverThePaymentAtTheSameMoment_NeverRefundMoreThanWasPaid`

## Invariants touched

- One refund per refund key (`uq_refunds_refund_key`): Completed is ignored, Pending is retried at the gateway with the same key (ADR 0008).
- The cap counts Pending and Completed refunds together, read under the payment row's lock.
- Recording the refund Pending, asking the gateway, and completing (refund, payment status and refunded amount, the two events) are three steps; the last is one transaction.
- A payment with another Pending refund stays RefundPending when one completes.
