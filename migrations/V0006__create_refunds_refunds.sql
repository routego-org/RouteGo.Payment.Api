-- =============================================================================
-- V0006__create_refunds_refunds.sql
-- Money Payment returns to a customer for one payment, and whether the gateway has returned it.
-- =============================================================================
-- Implements: data-model/refunds/refund.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: refund_id (uuid) is the entity's surrogate attribute; the entity's IDENTITY is the
--   refund key, so it carries the unique constraint ("One refund exists per refund key").
-- Money: whole Indian rupees (see V0003).
-- booking_id is an external reference (Booking's): text, no foreign key.
-- created_at is an entity attribute AND an audit column: one column, stamped by the trigger.

BEGIN;

CREATE SCHEMA IF NOT EXISTS refunds;

CREATE TABLE refunds.refunds (
    refund_id         uuid        NOT NULL,
    refund_key        text        NOT NULL,
    payment_id        uuid        NOT NULL,
    booking_id        text        NOT NULL,
    amount            bigint      NOT NULL,
    reason            text        NOT NULL,
    status            text        NOT NULL,
    gateway_refund_id text        NULL,
    completed_at      timestamptz NULL,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_refunds PRIMARY KEY (refund_id),
    -- Identity: "One refund exists per refund key."
    CONSTRAINT uq_refunds_refund_key UNIQUE (refund_key),
    -- Relationship: "Belongs to one Payment".
    CONSTRAINT fk_refunds_payment_id FOREIGN KEY (payment_id) REFERENCES payments.payments (payment_id),
    CONSTRAINT ck_refunds_refund_key_len CHECK (length(refund_key) <= 200),
    CONSTRAINT ck_refunds_booking_id_len CHECK (length(booking_id) <= 64),
    -- Rule: amount is whole INR, 1 or more.
    CONSTRAINT ck_refunds_amount CHECK (amount >= 1),
    -- Attribute reason: exactly the values the entity file lists.
    CONSTRAINT ck_refunds_reason CHECK (reason IN ('Cancellation', 'LatePayment', 'SeatsLost', 'TripCancelled')),
    -- Attribute status: exactly the values the entity file lists.
    CONSTRAINT ck_refunds_status CHECK (status IN ('Pending', 'Completed')),
    CONSTRAINT ck_refunds_gateway_refund_id_len CHECK (gateway_refund_id IS NULL OR length(gateway_refund_id) <= 200),
    -- Attributes gateway_refund_id and completed_at: "present once Completed" / "present exactly when Completed".
    CONSTRAINT ck_refunds_completed_attributes CHECK (
        (status = 'Completed') = (completed_at IS NOT NULL)
        AND (status = 'Completed') = (gateway_refund_id IS NOT NULL)),
    CONSTRAINT ck_refunds_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_refunds_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

CREATE INDEX ix_refunds_payment_id ON refunds.refunds (payment_id);

SELECT audit.fn_enable_audit('refunds.refunds', 'refund_id');

COMMENT ON TABLE refunds.refunds IS 'data-model/refunds/refund.md';

COMMIT;
