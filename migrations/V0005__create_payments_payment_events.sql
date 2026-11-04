-- =============================================================================
-- V0005__create_payments_payment_events.sql
-- An event recorded with the change that caused it, kept until it has been sent.
-- =============================================================================
-- Implements: data-model/payments/payment-event.md v0.1.0
-- Change tracking: excluded - see the audit.fn_exclude_from_audit reason below.
-- Identifier: keyed by the entity's identity attribute event_id (uuid), fixed when the event is
--   recorded and the same on every send.
-- Order: events are sent "in the order recorded". recorded_at defaults to clock_timestamp() (the
--   moment of each INSERT, not of the transaction), so two events recorded by one transaction in
--   order keep that order; the publisher reads by recorded_at.

BEGIN;

CREATE TABLE payments.payment_events (
    event_id       uuid        NOT NULL,
    event_type     text        NOT NULL,
    stream         text        NOT NULL,
    payment_id     uuid        NOT NULL,
    event_data     text        NOT NULL,
    correlation_id text        NOT NULL,
    recorded_at    timestamptz NOT NULL DEFAULT clock_timestamp(),
    sent_at        timestamptz NULL,
    CONSTRAINT pk_payment_events PRIMARY KEY (event_id),
    -- Relationship: "Belongs to one Payment".
    CONSTRAINT fk_payment_events_payment_id FOREIGN KEY (payment_id) REFERENCES payments.payments (payment_id),
    -- Attribute event_type: exactly the values the entity file lists.
    CONSTRAINT ck_payment_events_event_type CHECK (event_type IN ('PaymentSucceeded', 'PaymentFailed', 'RefundCompleted', 'AuditRecorded')),
    -- Attribute stream: exactly the values the entity file lists.
    CONSTRAINT ck_payment_events_stream CHECK (stream IN ('PaymentEvents', 'AuditEvents')),
    -- Attribute stream: "AuditRecorded to the audit events stream, every other event to the payment events stream".
    CONSTRAINT ck_payment_events_stream_matches_type CHECK ((event_type = 'AuditRecorded') = (stream = 'AuditEvents')),
    CONSTRAINT ck_payment_events_event_data_len CHECK (length(event_data) <= 16000),
    CONSTRAINT ck_payment_events_correlation_id_len CHECK (length(correlation_id) <= 128)
);

-- The publisher's one query: the unsent events, oldest first.
CREATE INDEX ix_payment_events_unsent ON payments.payment_events (recorded_at) WHERE sent_at IS NULL;
CREATE INDEX ix_payment_events_payment_id ON payments.payment_events (payment_id);

SELECT audit.fn_exclude_from_audit(
    'payments.payment_events',
    'machine-written outbox: rows are written by the service in the same transaction as the change they announce and only ever gain sent_at; their history is the event stream itself');

COMMENT ON TABLE payments.payment_events IS 'data-model/payments/payment-event.md';

COMMIT;
