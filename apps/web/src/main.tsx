import React from 'react';
import { createRoot } from 'react-dom/client';
import { Activity, CalendarRange, Database, Loader2, Search } from 'lucide-react';
import Highcharts from 'highcharts/highstock';
import './styles.css';

type SensorSummary = {
  id: string;
  name: string;
  location: string;
  model: string;
  installedOn: string;
  notes: string | null;
  channelCount: number;
};

type SensorChannelSummary = {
  id: string;
  sensorId: string;
  name: string;
  unit: string;
  nominalSampleSeconds: number;
  expectedMin: number | null;
  expectedMax: number | null;
  isEnabled: boolean;
};

type ChannelSeries = {
  channelId: string;
  name: string;
  unit: string;
  points: [number, number][];
};

type SensorSeriesResponse = {
  sensorId: string;
  from: string;
  to: string;
  resolution: string;
  bucketSeconds: number | null;
  source: string;
  targetPointCount: number;
  series: ChannelSeries[];
};

type SensorReadingRangeResponse = {
  sensorId: string;
  from: string | null;
  to: string | null;
};

type LoadState = 'idle' | 'loading' | 'refreshing' | 'error';

const defaultRangeDays = 30;
const rangeQueryDelayMs = 280;

function App() {
  const [sensors, setSensors] = React.useState<SensorSummary[]>([]);
  const [selectedSensorId, setSelectedSensorId] = React.useState<string | null>(null);
  const [channels, setChannels] = React.useState<SensorChannelSummary[]>([]);
  const [selectedChannelIds, setSelectedChannelIds] = React.useState<string[]>([]);
  const [seriesResponse, setSeriesResponse] = React.useState<SensorSeriesResponse | null>(null);
  const [readingRangeTo, setReadingRangeTo] = React.useState<Date | null>(null);
  const [sensorFilter, setSensorFilter] = React.useState('');
  const [range, setRange] = React.useState(() => defaultRange());
  const [chartWidth, setChartWidth] = React.useState(1200);
  const [queryRange, setQueryRange] = React.useState(range);
  const [loadState, setLoadState] = React.useState<LoadState>('idle');
  const [error, setError] = React.useState<string | null>(null);
  const latestRequest = React.useRef(0);
  const chartShellRef = React.useRef<HTMLDivElement | null>(null);

  React.useEffect(() => {
    let cancelled = false;

    fetchJson<SensorSummary[]>('/api/sensors')
      .then((items) => {
        if (cancelled) {
          return;
        }

        setSensors(items);
        setSelectedSensorId(items[0]?.id ?? null);
      })
      .catch((requestError: Error) => {
        if (!cancelled) {
          setError(requestError.message);
          setLoadState('error');
        }
      });

    return () => {
      cancelled = true;
    };
  }, []);

  React.useEffect(() => {
    if (!selectedSensorId) {
      setChannels([]);
      setSelectedChannelIds([]);
      return;
    }

    let cancelled = false;

    Promise.all([
      fetchJson<SensorChannelSummary[]>(`/api/sensors/${selectedSensorId}/channels`),
      fetchJson<SensorReadingRangeResponse>(`/api/sensors/${selectedSensorId}/reading-range`),
    ])
      .then(([items, readingRange]) => {
        if (cancelled) {
          return;
        }

        setChannels(items);
        setSelectedChannelIds(items.filter((channel) => channel.isEnabled).slice(0, 4).map((channel) => channel.id));

        if (readingRange.to) {
          const latestReadingAt = new Date(readingRange.to);
          const nextRange = rangeEndingAt(latestReadingAt, defaultRangeDays);
          setReadingRangeTo(latestReadingAt);
          setRange(nextRange);
          setQueryRange(nextRange);
        } else {
          setReadingRangeTo(null);
        }
      })
      .catch((requestError: Error) => {
        if (!cancelled) {
          setError(requestError.message);
          setLoadState('error');
        }
      });

    return () => {
      cancelled = true;
    };
  }, [selectedSensorId]);

  React.useEffect(() => {
    const observer = new ResizeObserver((entries) => {
      const nextWidth = Math.round(entries[0]?.contentRect.width ?? 1200);
      if (nextWidth > 0) {
        setChartWidth(nextWidth);
      }
    });

    if (chartShellRef.current) {
      observer.observe(chartShellRef.current);
    }

    return () => observer.disconnect();
  }, []);

  React.useEffect(() => {
    if (!selectedSensorId || selectedChannelIds.length === 0) {
      setSeriesResponse(null);
      return;
    }

    const timeout = window.setTimeout(() => {
      setQueryRange(range);
    }, rangeQueryDelayMs);

    return () => window.clearTimeout(timeout);
  }, [range, selectedSensorId, selectedChannelIds]);

  React.useEffect(() => {
    if (!selectedSensorId || selectedChannelIds.length === 0) {
      setSeriesResponse(null);
      return;
    }

    const requestId = latestRequest.current + 1;
    latestRequest.current = requestId;
    const controller = new AbortController();
    setLoadState((current) => (seriesResponse ? 'refreshing' : 'loading'));
    setError(null);

    const params = new URLSearchParams({
      from: queryRange.from.toISOString(),
      to: queryRange.to.toISOString(),
      width: String(chartWidth),
      channelIds: selectedChannelIds.join(','),
    });

    fetchJson<SensorSeriesResponse>(`/api/sensors/${selectedSensorId}/series?${params}`, controller.signal)
      .then((response) => {
        if (latestRequest.current !== requestId) {
          return;
        }

        setSeriesResponse(response);
        setLoadState('idle');
      })
      .catch((requestError: Error) => {
        if (controller.signal.aborted || latestRequest.current !== requestId) {
          return;
        }

        setError(requestError.message);
        setLoadState('error');
      });

    return () => controller.abort();
  }, [selectedSensorId, selectedChannelIds, queryRange.from, queryRange.to, chartWidth]);

  const selectedSensor = sensors.find((sensor) => sensor.id === selectedSensorId) ?? null;
  const filteredSensors = React.useMemo(() => {
    const filter = sensorFilter.trim().toLowerCase();
    if (!filter) {
      return sensors;
    }

    return sensors.filter((sensor) =>
      [sensor.name, sensor.location, sensor.model].some((value) => value.toLowerCase().includes(filter))
    );
  }, [sensorFilter, sensors]);

  const handleRangeChange = React.useCallback((from: Date, to: Date) => {
    setRange({ from, to });
  }, []);

  function toggleChannel(channelId: string) {
    setSelectedChannelIds((current) => {
      if (current.includes(channelId)) {
        return current.filter((id) => id !== channelId);
      }

      return [...current, channelId];
    });
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <Database size={22} aria-hidden="true" />
          <div>
            <h1>Lab Monitor</h1>
            <p>Timescale telemetry</p>
          </div>
        </div>

        <label className="search-box">
          <Search size={16} aria-hidden="true" />
          <input
            aria-label="Filter sensors"
            onChange={(event) => setSensorFilter(event.target.value)}
            placeholder="Filter sensors"
            type="search"
            value={sensorFilter}
          />
        </label>

        <div className="sensor-list" aria-label="Sensors">
          {filteredSensors.map((sensor) => (
            <button
              className={sensor.id === selectedSensorId ? 'sensor-row selected' : 'sensor-row'}
              key={sensor.id}
              onClick={() => setSelectedSensorId(sensor.id)}
              type="button"
            >
              <span>{sensor.name}</span>
              <small>{sensor.location}</small>
            </button>
          ))}
        </div>
      </aside>

      <main className="workspace">
        <header className="topbar">
          <div>
            <h2>{selectedSensor?.name ?? 'Select a sensor'}</h2>
            <p>{selectedSensor ? `${selectedSensor.location} · ${selectedSensor.model}` : 'No sensor selected'}</p>
          </div>

          <div className="status-strip">
            <StatusPill icon={<Activity size={15} />} label={seriesResponse?.resolution ?? 'No data'} />
            <StatusPill icon={<CalendarRange size={15} />} label={`${formatDate(range.from)} to ${formatDate(range.to)}`} />
            <StatusPill icon={<Database size={15} />} label={seriesResponse?.source ?? 'Waiting'} />
          </div>
        </header>

        <section className="content-grid">
          <section className="chart-panel" ref={chartShellRef}>
            <div className="chart-toolbar">
              <RangeButton active={isApproxRange(range, 7)} anchorDate={readingRangeTo} label="7D" days={7} setRange={setRange} />
              <RangeButton active={isApproxRange(range, 30)} anchorDate={readingRangeTo} label="30D" days={30} setRange={setRange} />
              <RangeButton active={isApproxRange(range, 183)} anchorDate={readingRangeTo} label="6M" days={183} setRange={setRange} />
              <RangeButton active={isApproxRange(range, 365)} anchorDate={readingRangeTo} label="1Y" days={365} setRange={setRange} />
              <RangeButton active={isApproxRange(range, 365 * 7)} anchorDate={readingRangeTo} label="7Y" days={365 * 7} setRange={setRange} />
            </div>

            <StockChart
              response={seriesResponse}
              loading={loadState === 'loading' || loadState === 'refreshing'}
              onRangeChange={handleRangeChange}
            />

            {seriesResponse && seriesResponse.series.length === 0 && loadState === 'idle' && (
              <div className="empty-chart">No readings found for the selected range and channels.</div>
            )}

            {loadState === 'refreshing' && (
              <div className="loading-overlay" role="status">
                <Loader2 size={18} aria-hidden="true" />
                Updating resolution
              </div>
            )}

            {error && <div className="error-bar">{error}</div>}
          </section>

          <aside className="channels-panel">
            <div className="panel-heading">
              <h3>Channels</h3>
              <span>{selectedChannelIds.length} selected</span>
            </div>

            <div className="channel-list">
              {channels.map((channel) => (
                <label className="channel-row" key={channel.id}>
                  <input
                    checked={selectedChannelIds.includes(channel.id)}
                    onChange={() => toggleChannel(channel.id)}
                    type="checkbox"
                  />
                  <span>
                    <strong>{channel.name}</strong>
                    <small>{channel.unit} · every {formatInterval(channel.nominalSampleSeconds)}</small>
                  </span>
                </label>
              ))}
            </div>
          </aside>
        </section>
      </main>
    </div>
  );
}

function StockChart({
  response,
  loading,
  onRangeChange,
}: {
  response: SensorSeriesResponse | null;
  loading: boolean;
  onRangeChange: (from: Date, to: Date) => void;
}) {
  const containerRef = React.useRef<HTMLDivElement | null>(null);
  const chartRef = React.useRef<Highcharts.Chart | null>(null);
  const debounceRef = React.useRef<number | null>(null);
  const structureRef = React.useRef('');

  React.useEffect(() => {
    if (!containerRef.current) {
      return;
    }

    chartRef.current = Highcharts.stockChart(containerRef.current, {
      chart: {
        animation: true,
        backgroundColor: 'transparent',
        height: 610,
        spacing: [12, 10, 12, 6],
      },
      credits: { enabled: false },
      legend: {
        enabled: true,
        align: 'left',
        verticalAlign: 'top',
      },
      navigator: {
        enabled: true,
      },
      rangeSelector: {
        enabled: false,
      },
      scrollbar: {
        enabled: true,
      },
      title: {
        text: '',
      },
      tooltip: {
        shared: true,
        valueDecimals: 2,
      },
      xAxis: {
        events: {
          afterSetExtremes(event) {
            if (event.trigger === 'sync') {
              return;
            }

            if (debounceRef.current) {
              window.clearTimeout(debounceRef.current);
            }

            debounceRef.current = window.setTimeout(() => {
              onRangeChange(new Date(event.min), new Date(event.max));
            }, 220);
          },
        },
      },
      yAxis: [],
      series: [],
    });

    return () => {
      chartRef.current?.destroy();
      chartRef.current = null;
      if (debounceRef.current) {
        window.clearTimeout(debounceRef.current);
      }
    };
  }, [onRangeChange]);

  React.useEffect(() => {
    const chart = chartRef.current;
    if (!chart || !response) {
      return;
    }

    const units = Array.from(new Set(response.series.map((item) => item.unit)));
    const structure = response.series.map((item) => `${item.channelId}:${item.unit}:${item.name}`).join('|');

    if (structureRef.current === structure) {
      response.series.forEach((item) => {
        const series = chart.get(item.channelId) as Highcharts.Series | undefined;
        series?.setData(item.points, false, false, false);
      });

      chart.xAxis[0].setExtremes(new Date(response.from).getTime(), new Date(response.to).getTime(), false, false, {
        trigger: 'sync',
      });
      chart.redraw();
      return;
    }

    structureRef.current = structure;
    const axes = units.map((unit, index) => ({
      id: unit,
      title: { text: unit },
      opposite: index % 2 === 1,
      labels: { align: (index % 2 === 1 ? 'left' : 'right') as Highcharts.AlignValue },
    }));

    while (chart.series.length) {
      chart.series[0].remove(false);
    }

    while (chart.yAxis.length) {
      chart.yAxis[0].remove(false);
    }

    axes.forEach((axis) => chart.addAxis(axis, false, false));

    response.series.forEach((item) => {
      chart.addSeries(
        {
          type: 'line',
          id: item.channelId,
          name: `${item.name} (${item.unit})`,
          data: item.points,
          yAxis: item.unit,
          tooltip: { valueSuffix: ` ${item.unit}` },
          turboThreshold: 0,
        },
        false
      );
    });

    chart.xAxis[0].setExtremes(new Date(response.from).getTime(), new Date(response.to).getTime(), false, false, {
      trigger: 'sync',
    });
    chart.redraw();
  }, [response]);

  React.useEffect(() => {
    if (!chartRef.current) {
      return;
    }

    if (loading && !chartRef.current.series.length) {
      chartRef.current.showLoading('Loading');
    } else {
      chartRef.current.hideLoading();
    }
  }, [loading]);

  return <div className="chart" ref={containerRef} />;
}

function StatusPill({ icon, label }: { icon: React.ReactNode; label: string }) {
  return (
    <span className="status-pill">
      {icon}
      {label}
    </span>
  );
}

function RangeButton({
  active,
  anchorDate,
  label,
  days,
  setRange,
}: {
  active: boolean;
  anchorDate: Date | null;
  label: string;
  days: number;
  setRange: React.Dispatch<React.SetStateAction<{ from: Date; to: Date }>>;
}) {
  return (
    <button
      className={active ? 'active' : undefined}
      type="button"
      onClick={() => setRange(anchorDate ? rangeEndingAt(anchorDate, days) : rangeFromDays(days))}
    >
      {label}
    </button>
  );
}

async function fetchJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal });
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }

  return response.json() as Promise<T>;
}

function defaultRange() {
  return rangeFromDays(defaultRangeDays);
}

function rangeFromDays(days: number) {
  const to = new Date();
  to.setUTCHours(0, 0, 0, 0);
  const from = new Date(to);
  from.setUTCDate(from.getUTCDate() - days);
  return { from, to };
}

function rangeEndingAt(to: Date, days: number) {
  const normalizedTo = new Date(to);
  normalizedTo.setUTCHours(0, 0, 0, 0);
  normalizedTo.setUTCDate(normalizedTo.getUTCDate() + 1);
  const from = new Date(normalizedTo);
  from.setUTCDate(from.getUTCDate() - days);
  return { from, to: normalizedTo };
}

function formatDate(value: Date) {
  return value.toISOString().slice(0, 10);
}

function formatInterval(seconds: number) {
  if (seconds < 60) {
    return `${seconds}s`;
  }

  return `${Math.round(seconds / 60)}m`;
}

function isApproxRange(range: { from: Date; to: Date }, days: number) {
  const actualDays = Math.round((range.to.getTime() - range.from.getTime()) / 86_400_000);
  return actualDays === days;
}

createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
