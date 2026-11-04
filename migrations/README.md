# Migrations

Raw, versioned SQL for `routego_payment`, applied by the pipeline's DbUp step — never by the application.

| What | How |
|---|---|
| Order | `V####__*.sql` by sequence, then `R__*.sql` (run-always routing: every run, after every V####, not journaled) |
| Journal | `public.schema_versions` |
| Transactions | each script wraps itself in `BEGIN; ... COMMIT;`; the runner adds no wrapping transaction |
| Variables | disabled |
| Empty folder | fails loudly |
| Run by hand | `deploy/ec2/apply-migrations.sh` (the same rules, for the EC2 deployment) |

Area to schema: `payments` (Payment, GatewayEvent, PaymentEvent) and `refunds` (Refund).
Tables carry the entity file they implement in their `COMMENT`.
