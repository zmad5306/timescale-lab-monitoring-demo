# TimescaleDB Industrial Lab Monitoring Demo Requirements

## Purpose

Build a monorepo demo that shows how to model, store, roll up, query, and visualize long-running industrial laboratory sensor telemetry with TimescaleDB, .NET 10, React, and Highcharts Stock.

The demo should emphasize practical time-series design:

- efficient raw telemetry storage with hypertables
- realistic sensor and channel metadata
- layered continuous aggregates for multi-year charting
- API-driven automatic downsampling based on visible chart range
- compression and retention policy examples
- elegant frontend visualization for multi-channel sensor history
- synthetic data generation for 7 years of history

Data ingestion from live devices is out of scope. The demo will include scripts to generate and load synthetic historical data.

## Target Scenario

The sample environment is a laboratory environmental and equipment monitoring system. It models approximately one dozen sensors deployed across rooms, cold storage, incubators, vibration-sensitive benches, clean zones, and utility spaces.

Each sensor has 3 to 6 channels in the demo, while the model should support up to 10 channels per sensor. The core model should not require the system to know a fixed channel type taxonomy. In realistic deployments, channels often arrive from equipment as a label, unit, sampling interval, and numeric value. For example, two channels may both be temperature readings but have different labels such as `Ambient Temperature`, `Freezer Probe A`, or `Incubator Chamber Temperature`.

Channels should be modeled generically using display labels and units. Optional metadata may provide demo generation hints or chart display hints, but the storage, API, and frontend should not depend on a closed set of known channel types.

Example channel labels and units:

- `Ambient Temperature`, `degC`
- `Relative Humidity`, `%RH`
- `Pressure Differential`, `Pa`
- `Vibration RMS`, `g`
- `Shock Event Magnitude`, `g`
- `CO2 Concentration`, `ppm`
- `Freezer Probe A`, `degC`
- `Incubator Chamber Temperature`, `degC`
- `Particle Count`, `count/L`
- `Door Open Count`, `count`

Channels should have varied units, sampling intervals, expected ranges, and realistic behavior.

## Demo Scale

The seeded dataset must cover 7 years of historical data.

Initial target scale:

- 12 sensors
- 3 to 6 channels per sensor
- roughly 50 to 70 total channels
- raw sampling intervals vary by channel, for example:
  - 5 seconds for vibration or shock-sensitive channels
  - 15 seconds for fast environmental channels
  - 1 minute for standard temperature and humidity
  - 5 minutes for slow-moving utility or particle-count channels

The data generator should include:

- daily and seasonal patterns
- sensor-specific offsets
- calibration drift
- random noise
- occasional spikes
- short outages or missing data
- rare anomaly windows

## Functional Requirements

### Sensor Catalog

The system must expose a catalog of sensors and their channels.

Each sensor should include:

- stable identifier
- display name
- location
- sensor type
- installation date
- optional notes or metadata

Each channel should include:

- stable identifier
- owning sensor
- display name
- unit
- nominal sampling interval
- expected min and max values
- chart display defaults
- enabled/disabled status
- optional metadata for generation/display hints

### Time-Series Storage

The primary telemetry table must store raw channel observations as time-series data.

Each reading must include:

- timestamp
- sensor id
- channel id
- numeric value
- optional quality/status marker

The table must be implemented as a TimescaleDB hypertable partitioned by time. Space partitioning by channel or sensor may be considered if the demo volume warrants it, but the initial design should stay understandable.

### Aggregated Storage

The database must provide layered continuous aggregates for charting large ranges efficiently.

Recommended aggregate layers:

- 5 minute
- 1 hour
- 1 day
- 1 month

Raw data should be used for narrow chart ranges. The 5-minute layer replaces a 1-minute layer to keep the sample simpler while still showing high-resolution downsampling.

Each aggregate bucket should include:

- bucket timestamp
- sensor id
- channel id
- average value
- minimum value
- maximum value
- first value, if practical
- last value, if practical
- sample count

The API should normally return average values for chart lines and may expose min/max bands later.

### Downsampling Selection

The backend must select the appropriate data resolution based on:

- requested `from` timestamp
- requested `to` timestamp
- chart pixel width or target point count
- requested channels

The frontend should send the visible range and approximate chart width. The API should choose raw or aggregate data automatically.

Initial target point count:

- approximately 800 to 1,500 points per visible channel

Suggested selection behavior:

| Visible Range | Source |
| --- | --- |
| up to 6 hours | raw |
| 6 hours to 7 days | 5-minute aggregate |
| 7 days to 180 days | 1-hour aggregate |
| 180 days to 3 years | 1-day aggregate |
| over 3 years | 1-month aggregate |

The implementation may refine this using target point count rather than fixed thresholds.

Resolution changes must preserve a smooth user experience. Zooming, panning, or range-selector changes should not cause the chart to visibly blank, flicker, jump axes unnecessarily, or feel like it is fighting the user. The frontend should keep the current data visible while fetching the next resolution, debounce or cancel stale requests, and replace series data only when the response still matches the latest visible range.

### Charting

The frontend must use Highcharts Stock.

Primary chart workflow:

- user selects one sensor
- the app displays one chart for that sensor
- multiple selected channels for that sensor can be overlaid
- the stock navigator and range selector should be enabled
- zooming or changing the visible range should trigger an API reload at an appropriate resolution
- resolution changes during zoom should feel smooth, with existing data retained until replacement data is ready
- channel units should be handled cleanly, including separate y-axes when needed

The initial UI does not need cross-sensor comparison.

### API

The .NET 10 backend should use a realistic layered architecture rather than a minimal throwaway API.

Expected backend layers:

- API endpoints/controllers
- application services
- repositories or query services
- database migrations/schema scripts
- DTOs/contracts
- configuration

The API should expose at least:

- list sensors
- list channels for a sensor
- query time-series data for a sensor and selected channels
- return selected resolution metadata with chart data

Example query shape:

`GET /api/sensors/{sensorId}/series?channelIds=...&from=...&to=...&width=1200`

The response should include:

- selected resolution
- source table or aggregate layer name
- timestamps and values per channel
- unit metadata needed for chart rendering

### Data Generation

The demo must include a repeatable script or console tool to seed 7 years of data.

The loader should:

- create sensor and channel metadata
- generate realistic measurements
- bulk load efficiently into TimescaleDB
- support deterministic seeds for repeatable demos
- allow smaller datasets for quick local testing

Preferred approach:

- .NET console seed tool or SQL/COPY-based generator
- clear command documented in the implementation plan

### TimescaleDB Features

The demo should demonstrate the TimescaleDB features that matter for this use case:

- hypertables
- time-oriented indexes
- continuous aggregates
- refresh policies
- compression policies
- retention policies
- chunk interval selection
- optional gap filling for missing data

Real-time aggregates should be documented and considered. For this demo, they are useful if the newest raw data should appear in aggregate-backed queries before the continuous aggregate refresh job has materialized it. Since this sample loads historical data and does not implement live ingestion, real-time aggregates are optional.

Chunk interval tuning should be included in the schema design. A practical starting point is a raw hypertable chunk interval of 1 day to 7 days depending on generated volume. Aggregate hypertables can use larger chunk intervals.

### Compression And Retention

Raw data must be retained for the full 7-year demo history.

The design should distinguish:

- a short hot window for recent uncompressed raw data
- older compressed raw chunks
- continuous aggregates retained for the full demo history

Initial policy:

- keep raw data for 7 years
- compress raw chunks older than 7 to 30 days
- keep aggregate data for at least 7 years
- optionally retain monthly aggregates longer than raw data in a production variant

The demo should include retention policy examples even if the 7-year demo keeps all generated raw data.

### Late And Corrected Data

The project must include a separate document describing how to handle late-arriving or corrected readings that target compressed chunks.

The live code does not need to implement the full mutation workflow, but the strategy must cover:

- queueing corrections
- grouping by affected chunk/window
- planned decompression windows
- applying updates or inserts in batches
- refreshing affected continuous aggregates
- recompressing chunks
- auditability and operational safeguards

## Non-Functional Requirements

### Performance

The chart endpoint should avoid returning excessive points. It should choose an aggregate layer that keeps responses responsive for 7-year ranges.

Target local demo behavior:

- sensor list loads in under 500 ms
- chart reloads usually complete in under 1 second against seeded data
- full 7-year views avoid raw-table scans
- zoom-driven reloads avoid flicker and discard stale responses from earlier ranges

### Local Developer Experience

The project should run locally with Docker Compose.

Expected services:

- TimescaleDB
- .NET 10 API
- React frontend

Optional services:

- database admin UI
- seed runner profile

The repository should include clear commands for:

- starting infrastructure
- applying schema
- loading demo data
- running API
- running frontend

### Frontend Quality

The frontend should feel like a real monitoring tool:

- dense but readable layout
- efficient sensor/channel selection
- clear chart controls
- polished handling of loading and empty states
- no marketing-style landing page
- responsive enough for desktop and tablet demo use

### Educational Value

The sample should make TimescaleDB decisions visible and understandable.

The code and docs should help a developer answer:

- why hypertables are used
- why continuous aggregates are layered
- how the API chooses resolution
- how compression affects old data
- how retention policies fit the lifecycle
- how chart range affects query cost

## Out Of Scope

- live device ingestion
- authentication and authorization
- alerting and notification workflows
- cross-sensor comparison
- multi-tenant isolation
- production deployment automation
- full correction workflow implementation for compressed chunks

## Open Decisions

- exact raw hypertable chunk interval after estimating generated row counts
- whether to use Entity Framework Core for metadata and Dapper/Npgsql for time-series queries
- whether synthetic data is generated in .NET, SQL, or a hybrid COPY workflow
- whether gap filling is enabled in the first chart endpoint or added after baseline visualization
- exact frontend component library, if any
