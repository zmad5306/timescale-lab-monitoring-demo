# Strategy For Late Or Corrected Data In Compressed TimescaleDB Chunks

## Problem

The demo keeps 7 years of raw sensor data. Recent data remains hot and uncompressed, while older chunks are compressed to reduce storage and improve long-range query efficiency.

In real monitoring systems, late or corrected readings may arrive after the target raw-data chunk has already been compressed. Examples include:

- delayed device uploads
- corrected calibration offsets
- backfilled readings after network outages
- operator edits
- lab equipment service corrections
- imported historical data from another system

TimescaleDB can update compressed data, but frequent small mutations against old compressed chunks are usually inefficient. A production system should avoid decompressing and recompressing chunks one row at a time.

## Goals

The recommended workflow should:

- preserve the 7-year raw history
- keep old chunks compressed most of the time
- batch old-data corrections by affected time window
- avoid repeated decompression of the same chunk
- refresh affected continuous aggregates
- leave an audit trail
- be operationally safe and observable

## High-Level Approach

Use a correction queue rather than applying late mutations directly to compressed chunks.

New or corrected readings are classified into two paths:

- hot-window writes are applied directly to raw hypertable data
- cold-window writes are queued for scheduled batch processing

The hot window should align with the compression policy. If raw chunks are compressed after 14 days, then readings newer than 14 days can usually be written directly. Readings older than that enter the correction queue.

## Suggested Tables

### Correction Queue

```sql
CREATE TABLE reading_correction_queue (
    id bigserial PRIMARY KEY,
    requested_at timestamptz NOT NULL DEFAULT now(),
    effective_time timestamptz NOT NULL,
    sensor_id uuid NOT NULL,
    channel_id uuid NOT NULL,
    corrected_value double precision NOT NULL,
    quality smallint NOT NULL DEFAULT 0,
    operation text NOT NULL CHECK (operation IN ('insert', 'update', 'delete')),
    reason text NULL,
    source text NULL,
    status text NOT NULL DEFAULT 'pending',
    processed_at timestamptz NULL,
    error text NULL
);

CREATE INDEX ix_reading_correction_queue_status_time
    ON reading_correction_queue (status, effective_time);
```

### Correction Batch

```sql
CREATE TABLE reading_correction_batches (
    id bigserial PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz NULL,
    completed_at timestamptz NULL,
    status text NOT NULL DEFAULT 'pending',
    min_time timestamptz NOT NULL,
    max_time timestamptz NOT NULL,
    affected_chunks integer NOT NULL DEFAULT 0,
    correction_count integer NOT NULL DEFAULT 0,
    error text NULL
);
```

These tables are not required for the first demo implementation, but they document a realistic production pattern.

## Processing Workflow

1. Accept late or corrected readings into `reading_correction_queue`.
2. Periodically select pending corrections older than a short settling delay.
3. Group corrections by compressed chunk or by bounded time window.
4. Pause or coordinate with compression jobs for the affected window.
5. Decompress each affected chunk once.
6. Apply all queued inserts, updates, or deletes for that chunk.
7. Refresh continuous aggregates for the affected time range.
8. Recompress the affected chunk.
9. Mark queue rows as processed and record batch metadata.

## Chunk-Aware Grouping

The batch processor should group corrections by the chunk that owns `effective_time`.

Useful TimescaleDB catalog views include:

- `timescaledb_information.chunks`
- `show_chunks(...)`

Conceptual query:

```sql
SELECT show_chunks(
    'sensor_readings',
    older_than => now() - INTERVAL '14 days'
);
```

For implementation, the processor can derive affected windows from queued correction timestamps and then call TimescaleDB functions for chunks intersecting those windows.

## Decompression And Recompression

For each affected chunk:

```sql
SELECT decompress_chunk('_timescaledb_internal._hyper_...');
```

Apply the queued mutations:

```sql
INSERT INTO sensor_readings (time, sensor_id, channel_id, value, quality)
VALUES (...)
ON CONFLICT (channel_id, time)
DO UPDATE SET
    value = EXCLUDED.value,
    quality = EXCLUDED.quality;
```

Then recompress:

```sql
SELECT compress_chunk('_timescaledb_internal._hyper_...');
```

Exact chunk names should be discovered at runtime. Application code should not hard-code internal chunk names.

## Continuous Aggregate Refresh

Any mutation to old raw data can make materialized aggregate data stale. After applying corrections, refresh each affected aggregate over a bounded window.

Example:

```sql
CALL refresh_continuous_aggregate(
    'sensor_readings_5m',
    '2022-05-01T00:00:00Z',
    '2022-05-02T00:00:00Z'
);
```

Refresh all aggregate layers that may be queried:

- 5-minute
- 1-hour
- 1-day
- 1-month

If aggregates are layered, refresh from the lowest layer upward.

Recommended order:

1. raw corrections
2. refresh 5-minute aggregate
3. refresh 1-hour aggregate
4. refresh 1-day aggregate
5. refresh 1-month aggregate
6. recompress affected chunks if they were decompressed

Depending on TimescaleDB version and aggregate definitions, aggregate invalidation may help track affected ranges, but explicit bounded refreshes are easier to reason about in a demo.

## Scheduling Strategy

Run the correction processor on a controlled schedule.

Common options:

- hourly for normal historical corrections
- nightly for large correction batches
- manual/admin-triggered for major backfills

Use a settling delay so corrections for the same time window can accumulate:

- wait 15 to 60 minutes before processing cold-window corrections
- group by day or chunk
- cap the maximum number of chunks per batch

## Operational Safeguards

Use safeguards before mutating compressed history:

- process one bounded time window per transaction when practical
- enforce a maximum batch size
- prevent two workers from processing the same chunk concurrently
- record batch start and completion timestamps
- mark failed corrections for retry
- keep original values in an audit table for updates/deletes
- expose metrics for queue depth and oldest pending correction
- run aggregate refresh with explicit start and end timestamps

For concurrency control, use advisory locks based on chunk name or time window.

Conceptual example:

```sql
SELECT pg_try_advisory_lock(hashtext('sensor_readings:2022-05-01'));
```

## Audit Trail

For regulated or laboratory environments, correction history often matters as much as current values.

Recommended audit data:

- who or what requested the correction
- original value
- corrected value
- reason
- source system
- request timestamp
- processing timestamp
- batch id

An audit table can be populated before the correction update is applied.

## Production Variant

For very high correction volumes, consider a delta-table strategy:

- leave compressed raw chunks untouched most of the time
- write corrections to a separate correction hypertable
- query through a view or reconciliation process
- periodically compact corrections into raw history during maintenance windows

This reduces decompression frequency but complicates query logic. For this demo, the queued batch mutation strategy is easier to explain and closer to standard operational maintenance.

## Demo Recommendation

The sample project should document this strategy but not implement it in the first version.

The initial code should include:

- compression policy for old raw chunks
- aggregate refresh scripts
- comments in database docs explaining why old corrections should be batched

A later enhancement can add:

- `reading_correction_queue`
- admin endpoint to enqueue corrections
- background worker to process correction batches
- UI indicator for pending historical corrections
