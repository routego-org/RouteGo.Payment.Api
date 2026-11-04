-- =============================================================================
-- V0003__create_payments_payments.sql
-- The one charge for one booking: amount, status, gateway reference, refunded amount.
-- =============================================================================
-- Implements: data-model/payments/payment.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: keyed by the entity's identity attribute payment_id (uuid), given to Booking when the
--   payment starts.
-- Money: whole Indian rupees, as the entity file states ("whole rupees, 1 or more"); there are no
--   minor units, so bigint holds rupees directly - not paise as the projection default assumes.
-- booking_id is an external reference (Booking's): text, no foreign key.
-- created_at and updated_at are entity attributes AND audit columns: one column each, stamped by the trigger.

BEGIN;

CREATE SCHEMA IF NOT EXISTS payments;

CREATE TABLE payments.payments (
    payment_id       uuid        NOT NULL,
    booking_id       text        NOT NULL,
    amount           bigint      NOT NULL,
    currency         text        NOT NULL,
    customer_email   text        NOT NULL,
    gateway_ref      text        NOT NULL,
    checkout_url     text        NOT NULL,
    status           text        NOT NULL,
    failure_reason   text        NULL,
    refunded_amount  bigint      NOT NULL DEFAULT 0,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    -- The *_by_id columns are text (not uuid) - the actor id is an opaque string from the
    -- authenticating issuer, capped at 64 chars by the CHECKs below.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_payments PRIMARY KEY (payment_id),
    -- Rule: "A booking has at most one payment." - also what makes a start repeat-safe.
    CONSTRAINT uq_payments_booking_id UNIQUE (booking_id),
    -- A gateway event finds its payment through the gateway's reference, so it names exactly one.
    CONSTRAINT uq_payments_gateway_ref UNIQUE (gateway_ref),
    CONSTRAINT ck_payments_booking_id_len CHECK (length(booking_id) <= 64),
    -- Rule: amount is whole Indian rupees, 1 or more.
    CONSTRAINT ck_payments_amount CHECK (amount >= 1),
    -- Rule: currency is always INR.
    CONSTRAINT ck_payments_currency CHECK (currency = 'INR'),
    CONSTRAINT ck_payments_currency_len CHECK (length(currency) <= 3),
    CONSTRAINT ck_payments_customer_email_len CHECK (length(customer_email) <= 254),
    CONSTRAINT ck_payments_gateway_ref_len CHECK (length(gateway_ref) <= 200),
    CONSTRAINT ck_payments_checkout_url_len CHECK (length(checkout_url) <= 2000),
    -- Attribute status: exactly the six values the entity file lists.
    CONSTRAINT ck_payments_status CHECK (status IN ('Created', 'Succeeded', 'Failed', 'RefundPending', 'PartiallyRefunded', 'Refunded')),
    CONSTRAINT ck_payments_failure_reason_len CHECK (failure_reason IS NULL OR length(failure_reason) <= 500),
    -- Attribute failure_reason: "present exactly when the status is Failed".
    CONSTRAINT ck_payments_failure_reason_failed CHECK ((status = 'Failed') = (failure_reason IS NOT NULL)),
    -- Rule: refunds together never exceed the payment's amount.
    CONSTRAINT ck_payments_refunded_amount CHECK (refunded_amount >= 0 AND refunded_amount <= amount),
    CONSTRAINT ck_payments_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_payments_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

SELECT audit.fn_enable_audit('payments.payments', 'payment_id');

COMMENT ON TABLE payments.payments IS 'data-model/payments/payment.md';

COMMIT;
