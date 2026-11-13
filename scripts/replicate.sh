#!/usr/bin/env bash
# Copy the primary bucket to a second provider (the "1 off-site / different provider" in 3-2-1).
#
# `rclone copy` (not `sync`) only ever ADDS files to the replica. If something deletes or
# corrupts files in the primary, that mistake does not propagate. Because objects are
# content-addressed (named by their hash), an existing object never changes, so a copy is
# always safe and incremental: only new hashes are transferred.
#
# --checksum compares hashes, not just size/date. Run nightly (cron or a scheduled GitHub Action).
set -euo pipefail

rclone copy "primary:${PRIMARY_BUCKET}" "replica:${REPLICA_BUCKET}" \
  --checksum --transfers 8 --fast-list --stats-one-line -v

# Prove it: list anything in the primary that the replica is missing or that differs.
rclone check "primary:${PRIMARY_BUCKET}" "replica:${REPLICA_BUCKET}" --one-way --checksum
