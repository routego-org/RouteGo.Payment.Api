# CAP-PAY-001 — Create payment

- Capability: CAP-PAY-001
- Canonical spec: `spec-RouteGo.Payment.API/capabilities/payments/features/payment-start.md` (v0.1.1)
- Area: Payments
- Slice: `Features/Payments/CreatePayment/`

## Contract

- Operation: `createPayment` in `contracts/payments/openapi.yaml` (entity file `payment.yaml`)
- Route / verb: `POST /internal/payments`, internal key (`X-Internal-Key`) only
- Request: `PaymentRequest` — `bookingId` (UUID), `amount` (whole rupees, 1 or more), `currency` (`INR`), `customerEmail`
- Response: `201` `Payment` for a new payment, `200` `Payment` for a repeat of the same amount
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `PAYMENT_ALREADY_EXISTS` (409, with `paymentId`), `UPSTREAM_UNAVAILABLE` (502), `UPSTREAM_TIMEOUT` (504)

## Acceptance criteria

Each criterion maps to tests in `CreatePayment.Tests.cs`, run against a real Postgres through the real host.

- [x] AC-1: Start a payment and get the payment page — `CreatePayment_BookingWithNoPayment_CreatesItAndReturnsThePaymentPage`
- [x] AC-2: The customer is sent back to RouteGo after paying — `CreatePayment_AnyBooking_TellsTheGatewayToReturnTheCustomerToPaymentsOwnAddress`
- [x] AC-3: The same start again returns the same payment — `CreatePayment_SameStartAgain_ReturnsTheSamePaymentAndAsksTheGatewayOnce`
- [x] AC-4: A repeat after the payment settled shows no payment page — `CreatePayment_RepeatAfterThePaymentSucceeded_ReturnsItWithNoPaymentPage`
- [x] AC-5: A second start with a different amount is refused — `CreatePayment_SecondStartWithAnotherAmount_IsRefusedNamingTheExistingPayment`
- [x] AC-6: Many starts for one booking make one payment — `CreatePayment_FiftyStartsAtTheSameMoment_MakeOnePaymentAndAnswerEveryRequestWithIt`
- [x] AC-7: Request rules — `CreatePayment_InvalidRequest_IsRefusedAndNothingIsCreated`
- [x] AC-8: The gateway cannot prepare a page — `CreatePayment_GatewayDown_IsRefusedNothingIsLeftAndALaterStartWorks`
- [x] AC-9: The gateway answers too slowly — `CreatePayment_GatewayTooSlow_IsRefusedWithATimeoutAndNothingIsCreated`
- [x] AC-10: Only Booking may start a payment — `CreatePayment_MissingOrWrongInternalKey_IsRefusedAsUnauthorized`, `CreatePayment_OnlyAUsersAccessToken_IsRefusedAsUnauthorized`

## Invariants touched

- A booking has at most one payment: `uq_payments_booking_id`. The insert has no conflict target because the gateway reference is unique too.
- The payment is recorded only after the gateway prepared its page, so a gateway failure leaves nothing behind. The gateway is asked with the booking as its repeat key.
- Amount and currency never change; whole rupees, INR only (`Domain/PaymentRules`).
- The payment page address is offered only while the payment is Created and the page validity (15 minutes, setting `PaymentPageValidityMinutes`) has not passed (`PaymentRules.IsPageOffered`).
- The return address is Payment's setting `ReturnUrlTemplate`; the request carries none.
- Starting a payment publishes no event.
