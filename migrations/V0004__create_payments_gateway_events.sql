-- =============================================================================
-- V0004__create_payments_gateway_events.sql
-- One report from the gateway, applied or rejected, kept so a repeat takes effect once.
-- =============================================================================
-- Implements: data-model/payments/gateway-event.md v0.1.0
-- Change tracking: excluded - see the audit.fn_exclude_from_audit reason below.
-- Identifier: keyed by the entity's identity attribute gateway_event_id, an external reference
--   (the gateway's own identity for the event) - text. The PRIMARY KEY IS the idempotency guarantee:
--   the record of an event and its effect are one transaction, and this key refuses a repeat.
-- Money: whole Indian rupees (see V0003).

BEGIN;

CREATE TABLE payments.gateway_events (
    gateway_event_id text        NOT NULL,
    payment_id       uuid        NULL,
    gateway_ref      text        NOT NULL,
    outcome          text        NOT NULL,
    amount           bigint      NOT NULL,
    reason           text        NULL,
    payload          text        NOT NULL,
    status           text        NOT NULL,
    rejection_reason text        NULL,
    occurred_at      timestamptz NULL,
    received_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_gateway_events PRIMARY KEY (gateway_event_id),
    -- Relationship: "about one Payment (none when no payment matched)".
    CONSTRAINT fk_gateway_events_payment_id FOREIGN KEY (payment_id) REFERENCES payments.payments (payment_id),
    CONSTRAINT ck_gateway_events_gateway_event_id_len CHECK (length(gateway_event_id) <= 200),
    CONSTRAINT ck_gateway_events_gateway_ref_len CHECK (length(gateway_ref) <= 200),
    -- Attribute outcome: exactly the values the entity file lists.
    CONSTRAINT ck_gateway_events_outcome CHECK (outcome IN ('Succeeded', 'Failed')),
    CONSTRAINT ck_gateway_events_reason_len CHECK (reason IS NULL OR length(reason) <= 500),
    -- The report exactly as received, kept for support and disputes.
    CONSTRAINT ck_gateway_events_payload_len CHECK (length(payload) <= 16000),
    -- Attribute status: exactly the values the entity file lists.
    CONSTRAINT ck_gateway_events_status CHECK (status IN ('Processed', 'Rejected')),
    -- Attribute rejection_reason: exactly the values listed.
    CONSTRAINT ck_gateway_events_rejection_reason CHECK (rejection_reason IS NULL OR rejection_reason IN ('AmountMismatch', 'AlreadySettled')),
    -- Attribute rejection_reason: "present exactly when the status is Rejected".
    CONSTRAINT ck_gateway_events_rejection_reason_rejected CHECK ((status = 'Rejected') = (rejection_reason IS NOT NULL))
);

CREATE INDEX ix_gateway_events_payment_id ON payments.gateway_events (payment_id);

SELECT audit.fn_exclude_from_audit(
    'payments.gateway_events',
    'machine-written append-only record: written once by the webhook worker, never changed (entity rule), and carries its own received_at');

COMMENT ON TABLE payments.gateway_events IS 'data-model/payments/gateway-event.md';

COMMIT;
