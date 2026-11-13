#!/usr/bin/env bash
# Nightly database backup INTO the archive bucket.
#
# The database is the map of the archive; the bucket is the territory. Without the map
# you still have every file (and manifest/index.json), but versions, tags and audit
# history live here. So the database gets backed up into the same durable place.
#
# The dump is encrypted with `age` (a small, modern, well-reviewed encryption tool)
# to the public key in AGE_RECIPIENT, so the server that makes backups can't read them.
#
# Needs: pg_dump, age, rclone. Env: DATABASE_URL, AGE_RECIPIENT, PRIMARY_BUCKET + rclone remote "primary".
set -euo pipefail

stamp=$(date -u +%Y-%m-%dT%H%M%SZ)
file="archive-${stamp}.sql.gz.age"

pg_dump --no-owner --format=plain "$DATABASE_URL" \
  | gzip -9 \
  | age -r "$AGE_RECIPIENT" \
  | rclone rcat "primary:${PRIMARY_BUCKET}/db-backups/${file}"

echo "Backed up database to db-backups/${file}"

# Keep 30 days of dailies (older ones are deleted; the bucket's own versioning/lock is the safety net).
rclone delete "primary:${PRIMARY_BUCKET}/db-backups" --min-age 30d
