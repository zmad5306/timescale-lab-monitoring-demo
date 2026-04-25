import React from 'react';
import { createRoot } from 'react-dom/client';
import { Activity, CalendarRange, Database, Loader2, Search, TriangleAlert } from 'lucide-react';
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
  alarm: AlarmConfiguration;
  isEnabled: boolean;
};

type AlarmConfiguration = {
  normalMin: number | null;
  normalMax: number | null;
  downsampleMethod: DownsampleMethod;
};

type ChannelSeries = {
  channelId: string;
  name: string;
  unit: string;
  downsampleMethod: string;
  alarm: AlarmConfiguration;
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
type DateRange = { from: Date; to: Date };
type DownsampleMethod = 'average' | 'minimum' | 'maximum' | 'first' | 'last';
type StockChartWithNavigator = Highcharts.Chart & {
  navigator?: {
    xAxis?: Highcharts.Axis;
    series?: Highcharts.Series[];
  };
};

const defaultRangeDays = 30;
const rangeQueryDelayMs = 280;
const rangeOptions = [
  { label: '6H', days: 0.25 },
  { label: '3D', days: 3 },
  { label: '7D', days: 7 },
  { label: '30D', days: 30 },
  { label: '90D', days: 90 },
  { label: '6M', days: 183 },
  { label: '1Y', days: 365 },
  { label: '2Y', days: 365 * 2 },
  { label: '7Y', days: 365 * 7 },
];
const downsampleOptions: { value: DownsampleMethod; label: string }[] = [
  { value: 'average', label: 'Average' },
  { value: 'minimum', label: 'Minimum' },
  { value: 'maximum', label: 'Maximum' },
  { value: 'first', label: 'First' },
  { value: 'last', label: 'Last' },
];

function App() {
  const [sensors, setSensors] = React.useState<SensorSummary[]>([]);
  const [selectedSensorId, setSelectedSensorId] = React.useState<string | null>(null);
  const [channels, setChannels] = React.useState<SensorChannelSummary[]>([]);
  const [selectedChannelIds, setSelectedChannelIds] = React.useState<string[]>([]);
  const [seriesResponse, setSeriesResponse] = React.useState<SensorSeriesResponse | null>(null);
  const [readingRange, setReadingRange] = React.useState<DateRange | null>(null);
  const [sensorFilter, setSensorFilter] = React.useState('');
  const [downsampleMethods, setDownsampleMethods] = React.useState<Record<string, DownsampleMethod>>({});
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
        setDownsampleMethods(Object.fromEntries(items.map((channel) => [channel.id, defaultDownsampleMethod(channel)])));

        if (readingRange.from && readingRange.to) {
          const availableRange = { from: new Date(readingRange.from), to: new Date(readingRange.to) };
          const latestReadingAt = availableRange.to;
          const nextRange = rangeEndingAt(latestReadingAt, defaultRangeDays);
          setReadingRange(availableRange);
          setRange(nextRange);
          setQueryRange(nextRange);
        } else {
          setReadingRange(null);
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

  const downsampleQuery = React.useMemo(
    () =>
      selectedChannelIds
        .map((channelId) => `${channelId}:${downsampleMethods[channelId] ?? 'average'}`)
        .join(','),
    [selectedChannelIds, downsampleMethods]
  );

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
      downsample: downsampleQuery,
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
  }, [selectedSensorId, selectedChannelIds, queryRange.from, queryRange.to, chartWidth, downsampleQuery]);

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
              <div className="range-group">
                {rangeOptions.map((rangeOption) => (
                  <RangeButton
                    active={isApproxRange(range, rangeOption.days)}
                    anchorDate={readingRange?.to ?? null}
                    key={rangeOption.label}
                    label={rangeOption.label}
                    days={rangeOption.days}
                    setRange={setRange}
                  />
                ))}
              </div>
            </div>

            <StockChart
              response={seriesResponse}
              dataRange={readingRange}
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
              {channels.map((channel) => {
                const isSelected = selectedChannelIds.includes(channel.id);

                return (
                  <div className="channel-row" key={channel.id}>
                    <input
                      aria-label={`Select ${channel.name}`}
                      checked={isSelected}
                      onChange={() => toggleChannel(channel.id)}
                      type="checkbox"
                    />
                    <span>
                      <strong>{channel.name}</strong>
                      <small>{channel.unit} · every {formatInterval(channel.nominalSampleSeconds)}</small>
                      <small className="alarm-summary">
                        <TriangleAlert size={13} aria-hidden="true" />
                        {formatAlarmBand(channel.alarm, channel.unit)}
                      </small>
                    </span>
                    <select
                      aria-label={`${channel.name} downsample method`}
                      disabled={!isSelected}
                      onChange={(event) =>
                        setDownsampleMethods((current) => ({
                          ...current,
                          [channel.id]: event.target.value as DownsampleMethod,
                        }))
                      }
                      value={downsampleMethods[channel.id] ?? defaultDownsampleMethod(channel)}
                    >
                      {downsampleOptions.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                    </select>
                  </div>
                );
              })}
            </div>
          </aside>
        </section>
      </main>
    </div>
  );
}

function StockChart({
  response,
  dataRange,
  loading,
  onRangeChange,
}: {
  response: SensorSeriesResponse | null;
  dataRange: DateRange | null;
  loading: boolean;
  onRangeChange: (from: Date, to: Date) => void;
}) {
  const containerRef = React.useRef<HTMLDivElement | null>(null);
  const chartRef = React.useRef<Highcharts.Chart | null>(null);
  const debounceRef = React.useRef<number | null>(null);
  const structureRef = React.useRef('');
  const seriesIdsRef = React.useRef<string[]>([]);
  const axisIdsRef = React.useRef<string[]>([]);

  React.useEffect(() => {
    if (!containerRef.current) {
      return;
    }

    chartRef.current = Highcharts.stockChart(containerRef.current, {
      chart: {
        animation: true,
        backgroundColor: 'transparent',
        height: 610,
        selectionMarkerFill: 'rgba(15, 118, 110, 0.16)',
        spacing: [12, 10, 12, 6],
        zooming: {
          mouseWheel: false,
          type: 'x',
        },
      },
      credits: { enabled: false },
      legend: {
        enabled: true,
        align: 'left',
        verticalAlign: 'top',
      },
      navigator: {
        adaptToUpdatedData: false,
        enabled: true,
        series: {
          type: 'line',
          data: [],
        },
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
        headerFormat: '<span style="font-size: 0.8em">{point.key:%Y-%m-%d %H:%M:%S}</span><br/>',
        shared: true,
        valueDecimals: 2,
        xDateFormat: '%Y-%m-%d %H:%M:%S',
      },
      xAxis: {
        type: 'datetime',
        events: {
          afterSetExtremes(event) {
            if (event.trigger === 'sync') {
              return;
            }

            if (debounceRef.current) {
              window.clearTimeout(debounceRef.current);
            }

            debounceRef.current = window.setTimeout(() => {
              if (!Number.isFinite(event.min) || !Number.isFinite(event.max) || event.max <= event.min) {
                return;
              }

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
    if (!chart || !dataRange) {
      return;
    }

    const min = dataRange.from.getTime();
    const max = dataRange.to.getTime();
    if (!Number.isFinite(min) || !Number.isFinite(max) || max <= min) {
      return;
    }

    const navigator = (chart as StockChartWithNavigator).navigator;
    navigator?.series?.[0]?.setData(
      [
        [min, 0],
        [max, 0],
      ],
      false,
      false,
      false
    );
    navigator?.xAxis?.setExtremes(min, max, false, false, { trigger: 'sync' });

    chart.redraw();
  }, [dataRange]);

  React.useEffect(() => {
    const chart = chartRef.current;
    if (!chart || !response) {
      return;
    }

    const units = Array.from(new Set(response.series.map((item) => item.unit)));
    const axisIdByUnit = new Map(units.map((unit, index) => [unit, `unit-axis-${index}`]));
    const structure = response.series
      .map((item) => `${item.channelId}:${item.unit}:${item.name}:${item.alarm.normalMin ?? ''}:${item.alarm.normalMax ?? ''}`)
      .join('|');

    if (structureRef.current === structure) {
      response.series.forEach((item) => {
        const series = chart.get(item.channelId) as Highcharts.Series | undefined;
        series?.setData(item.points, false, false, false);
        const alarmSeries = chart.get(alarmSeriesId(item.channelId)) as Highcharts.Series | undefined;
        alarmSeries?.setData(alarmPoints(item), false, false, false);
      });

      chart.xAxis[0].setExtremes(new Date(response.from).getTime(), new Date(response.to).getTime(), false, false, {
        trigger: 'sync',
      });
      chart.redraw();
      return;
    }

    structureRef.current = structure;
    const axes = units.map((unit, index) => ({
      id: axisIdByUnit.get(unit),
      title: { text: unit },
      opposite: index % 2 === 1,
      labels: { align: (index % 2 === 1 ? 'left' : 'right') as Highcharts.AlignValue },
      plotBands: response.series
        .filter((item) => item.unit === unit)
        .map((item, bandIndex) => alarmPlotBand(item, bandIndex)),
    }));

    seriesIdsRef.current.forEach((seriesId) => {
      const series = chart.get(seriesId) as Highcharts.Series | undefined;
      series?.remove(false);
    });

    axisIdsRef.current.forEach((axisId) => {
      const axis = chart.get(axisId) as Highcharts.Axis | undefined;
      axis?.remove(false);
    });

    axes.forEach((axis) => chart.addAxis(axis, false, false));

    response.series.forEach((item) => {
      const yAxis = axisIdByUnit.get(item.unit);
      if (!yAxis) {
        return;
      }

      chart.addSeries(
        {
          type: 'line',
          id: item.channelId,
          name: `${item.name} (${item.unit})`,
          data: item.points,
          yAxis,
          tooltip: {
            pointFormat: `<span style="color:{series.color}">\u25CF</span> {series.name}: <b>{point.y:.2f} ${item.unit}</b><br/>`,
          },
          turboThreshold: 0,
        },
        false
      );

      chart.addSeries(
        {
          type: 'scatter',
          id: alarmSeriesId(item.channelId),
          name: `${item.name} alarm`,
          data: alarmPoints(item),
          yAxis,
          color: '#dc2626',
          marker: {
            enabled: true,
            radius: 4,
            symbol: 'triangle',
          },
          tooltip: {
            pointFormat: `<span style="color:{series.color}">\u25B2</span> {series.name}: <b>{point.y:.2f} ${item.unit}</b><br/>`,
          },
          turboThreshold: 0,
          zIndex: 5,
        },
        false
      );
    });

    seriesIdsRef.current = response.series.flatMap((item) => [item.channelId, alarmSeriesId(item.channelId)]);
    axisIdsRef.current = axes.map((axis) => axis.id).filter((axisId): axisId is string => Boolean(axisId));

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
  from.setTime(from.getTime() - days * 86_400_000);
  return { from, to };
}

function rangeEndingAt(to: Date, days: number) {
  const normalizedTo = new Date(to);
  normalizedTo.setUTCHours(0, 0, 0, 0);
  normalizedTo.setUTCDate(normalizedTo.getUTCDate() + 1);
  const from = new Date(normalizedTo);
  from.setTime(from.getTime() - days * 86_400_000);
  return { from, to: normalizedTo };
}

function defaultDownsampleMethod(channel: SensorChannelSummary): DownsampleMethod {
  if (channel.alarm?.downsampleMethod) {
    return channel.alarm.downsampleMethod;
  }

  const name = channel.name.toLowerCase();
  const unit = channel.unit.toLowerCase();

  if (name.includes('count') || name.includes('event') || name.includes('shock') || name.includes('freezer') || unit.includes('count')) {
    return 'maximum';
  }

  if (name.includes('pressure differential')) {
    return 'minimum';
  }

  if (name.includes('current') || name.includes('line pressure')) {
    return 'last';
  }

  return 'average';
}

function alarmSeriesId(channelId: string) {
  return `${channelId}-alarm`;
}

function alarmPoints(item: ChannelSeries): [number, number][] {
  return item.points.filter((point) => isInAlarm(point[1], item.alarm));
}

function isInAlarm(value: number, alarm: AlarmConfiguration) {
  return (alarm.normalMin !== null && value < alarm.normalMin) || (alarm.normalMax !== null && value > alarm.normalMax);
}

function alarmPlotBand(item: ChannelSeries, index: number): Highcharts.YAxisPlotBandsOptions {
  return {
    id: `${item.channelId}-normal-band`,
    from: item.alarm.normalMin ?? -Number.MAX_VALUE,
    to: item.alarm.normalMax ?? Number.MAX_VALUE,
    color: index % 2 === 0 ? 'rgba(20, 184, 166, 0.08)' : 'rgba(34, 197, 94, 0.07)',
    label: {
      text: `${item.name} normal`,
      style: {
        color: '#55706b',
        fontSize: '10px',
      },
    },
  };
}

function formatAlarmBand(alarm: AlarmConfiguration, unit: string) {
  if (alarm.normalMin !== null && alarm.normalMax !== null) {
    return `Normal ${formatNumber(alarm.normalMin)} to ${formatNumber(alarm.normalMax)} ${unit}`;
  }

  if (alarm.normalMin !== null) {
    return `Normal >= ${formatNumber(alarm.normalMin)} ${unit}`;
  }

  return `Normal <= ${formatNumber(alarm.normalMax ?? 0)} ${unit}`;
}

function formatNumber(value: number) {
  return Number.isInteger(value) ? String(value) : value.toLocaleString(undefined, { maximumFractionDigits: 2 });
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
  const actualDays = (range.to.getTime() - range.from.getTime()) / 86_400_000;
  return Math.abs(actualDays - days) < 0.01;
}

createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
