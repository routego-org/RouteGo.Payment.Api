# CAP-PAY-002 — Get payment

- Capability: CAP-PAY-002
- Canonical spec: `spec-RouteGo.Payment.API/capabilities/payments/features/payment-status.md` (v0.1.1)
- Area: Payments
- Slice: `Features/Payments/GetPayment/`

## Contract

- Operation: `getPayment` in `contracts/payments/openapi.yaml` (entity file `payment.yaml`)
- Route / verb: `GET /internal/bookings/{bookingId}/payment`, internal key only
- Response: `200` `Payment` with its `refunds`; never the customer email
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `NOT_FOUND` (404)

## Acceptance criteria

Each criterion maps to tests in `GetPayment.Tests.cs`.

- [x] AC-1: An unpaid payment shows its payment page — `GetPayment_UnpaidPayment_ShowsItsPaymentPageAndNoRefunds`
- [x] AC-2: A paid payment shows no payment page — `GetPayment_PaidPayment_ShowsNoPaymentPage`
- [x] AC-3: An expired payment page is not offered — `GetPayment_PaymentPageValidityPassed_ShowsNoPaymentPage`
- [x] AC-4: A partly refunded payment lists its refunds — `GetPayment_PartlyRefundedPayment_ListsItsRefund`
- [x] AC-5: A refund in progress is shown — `GetPayment_RefundInProgress_ShowsItPendingAndCountsNothingRefunded`
- [x] AC-6: A booking with no payment is not found — `GetPayment_BookingWithNoPayment_IsNotFound`
- [x] AC-7: No personal data in the answer — `GetPayment_AnyPayment_ContainsNoCustomerEmail`
- [x] AC-8: Only Booking may read a payment — `GetPayment_MissingOrWrongInternalKey_IsRefusedAsUnauthorized`, `GetPayment_OnlyAUsersAccessToken_IsRefusedAsUnauthorized`

## Invariants touched

- Reading changes nothing and publishes nothing (reader connection).
- Refunded amount is the total of the Completed refunds (`payments.refunded_amount`), so a Pending refund counts for nothing.
- The customer email is never selected, so it cannot be returned.
- A malformed booking identifier is `VALIDATION_FAILED`, not a 404.
