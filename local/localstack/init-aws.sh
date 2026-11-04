#!/bin/bash
# LOCAL ONLY. Runs once LocalStack is ready: creates what Payment needs from the platform
# (00-overall-architecture.md section 12.4, 06-payment.md section 6). The local stand-ins for the
# services that consume Payment's events (Booking, Audit) are queues you can read by hand.
set -euo pipefail
export AWS_DEFAULT_REGION=ap-south-1
ACCOUNT=000000000000
QUEUES=http://localhost:4566/$ACCOUNT

# Topics (the audit and booking topics are shared with other services and may already exist).
for topic in payment audit booking; do
  awslocal sns create-topic --name "routego-${topic}-events" >/dev/null
done

# A queue with its dead-letter queue: five receives, then parked.
make_queue() { # name dlq-name visibility-seconds
  awslocal sqs create-queue --queue-name "$2" >/dev/null
  dlq_arn=$(awslocal sqs get-queue-attributes --queue-url "$QUEUES/$2" --attribute-names QueueArn --query Attributes.QueueArn --output text)
  attributes=$(python3 -c 'import json,sys; print(json.dumps({"VisibilityTimeout": sys.argv[1], "ReceiveMessageWaitTimeSeconds": "20", "RedrivePolicy": json.dumps({"deadLetterTargetArn": sys.argv[2], "maxReceiveCount": "5"})}))' "$3" "$dlq_arn")
  awslocal sqs create-queue --queue-name "$1" --attributes "$attributes" >/dev/null
}

subscribe() { # queue topic filter-json
  attributes=$(python3 -c 'import json,sys; print(json.dumps({"RawMessageDelivery": "true", "FilterPolicy": json.dumps({"eventType": json.loads(sys.argv[1])})}))' "$3")
  awslocal sns subscribe     --topic-arn "arn:aws:sns:ap-south-1:$ACCOUNT:routego-$2-events"     --protocol sqs --notification-endpoint "arn:aws:sqs:ap-south-1:$ACCOUNT:$1"     --attributes "$attributes" >/dev/null
}

# What Payment reads.
make_queue payment-webhooks-q payment-webhooks-dlq 60   # fed by the Webhook Receiver (or MockGateway directly)
make_queue payment-refund-q payment-refund-dlq 60
subscribe payment-refund-q booking '["RefundRequested"]'

# Local stand-ins for Payment's subscribers, so a run can be inspected with awslocal sqs receive-message.
make_queue booking-payment-q booking-payment-dlq 60
subscribe booking-payment-q payment '["PaymentSucceeded","PaymentFailed","RefundCompleted"]'
make_queue audit-q audit-dlq 60
subscribe audit-q audit '["AuditRecorded"]'

echo "Payment queues and topics are ready."
