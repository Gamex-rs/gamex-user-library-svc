#!/bin/bash
set -euo pipefail

# Same region the service signs requests for (AWS_DEFAULT_REGION in docker-compose.yml).
REGION=eu-central-1
ACCOUNT=000000000000

create_queue_with_dlq() {
  local name=$1
  awslocal --region "$REGION" sqs create-queue --queue-name "$name-dlq"
  awslocal --region "$REGION" sqs create-queue --queue-name "$name" \
    --attributes "{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"arn:aws:sqs:$REGION:$ACCOUNT:$name-dlq\\\",\\\"maxReceiveCount\\\":\\\"3\\\"}\"}"
}

create_queue_with_dlq gamex-library-events
create_queue_with_dlq gamex-activity-events
create_queue_with_dlq gamex-catalog-events
