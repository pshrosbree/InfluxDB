﻿// Copyright (c) Allan Hardy. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using App.Metrics.Abstractions.Reporting;
using App.Metrics.Apdex;
using App.Metrics.Core.Abstractions;
using App.Metrics.Counter;
using App.Metrics.Extensions.Reporting.ElasticSearch.Client;
using App.Metrics.Extensions.Reporting.ElasticSearch.Extensions;
using App.Metrics.Health;
using App.Metrics.Histogram;
using App.Metrics.Infrastructure;
using App.Metrics.Meter;
using App.Metrics.Tagging;
using App.Metrics.Timer;
using Microsoft.Extensions.Logging;

namespace App.Metrics.Extensions.Reporting.ElasticSearch
{
    public class ElasticSearchReporter : IMetricReporter
    {
        private readonly ElasticSearchBulkClient _client;
        private readonly ILogger<ElasticSearchReporter> _logger;
        private readonly Func<string, string, string> _metricNameFormatter;
        private readonly BulkPayloadBuilder _payloadBuilder;
        private bool _disposed;

        public ElasticSearchReporter(
            ElasticSearchBulkClient client,
            BulkPayloadBuilder payloadBuilder,
            TimeSpan reportInterval,
            ILoggerFactory loggerFactory,
            Func<string, string, string> metricNameFormatter)
            : this(
                client,
                payloadBuilder,
                reportInterval,
                typeof(ElasticSearchReporter).Name,
                loggerFactory,
                metricNameFormatter)
        {
        }

        public ElasticSearchReporter(
            ElasticSearchBulkClient client,
            BulkPayloadBuilder payloadBuilder,
            TimeSpan reportInterval,
            string name,
            ILoggerFactory loggerFactory,
            Func<string, string, string> metricNameFormatter)
        {
            ReportInterval = reportInterval;
            Name = name;

            _payloadBuilder = payloadBuilder;
            _metricNameFormatter = metricNameFormatter;
            _logger = loggerFactory.CreateLogger<ElasticSearchReporter>();
            _client = client;
        }

        public string Name { get; }

        public TimeSpan ReportInterval { get; }

        public void Dispose() { Dispose(true); }

        public void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Free any other managed objects here.
                    _payloadBuilder.Clear();
                }
            }

            _disposed = true;
        }

        public async Task<bool> EndAndFlushReportRunAsync(IMetrics metrics)
        {
            _logger.LogTrace($"Ending {Name} Run");

            var result = await _client.WriteAsync(_payloadBuilder.Payload);

            _payloadBuilder.Clear();

            return result;
        }

        public void ReportEnvironment(EnvironmentInfo environmentInfo) { }

        public void ReportHealth(
            GlobalMetricTags globalTags,
            IEnumerable<HealthCheck.Result> healthyChecks,
            IEnumerable<HealthCheck.Result> degradedChecks,
            IEnumerable<HealthCheck.Result> unhealthyChecks)
        {
            _logger.LogTrace($"Packing Health Checks for {Name}");

            var unhealthy = unhealthyChecks as HealthCheck.Result[] ?? unhealthyChecks.ToArray();
            var degraded = degradedChecks as HealthCheck.Result[] ?? degradedChecks.ToArray();

            var isUnhealthy = unhealthy.Any();
            var isDegraded = degraded.Any();
            var healthy = !isUnhealthy && !isDegraded;

            var healthStatusValue = 2;

            if (isUnhealthy)
            {
                healthStatusValue = 3;
            }
            else if (healthy)
            {
                healthStatusValue = 1;
            }

            var tags = new MetricTags(globalTags.Select(t => t.Key).ToArray(), globalTags.Select(t => t.Value).ToArray());

            _payloadBuilder.Pack("health", healthStatusValue, tags);

            var checks = unhealthy.Concat(degraded).Concat(healthyChecks);

            foreach (var healthCheck in checks)
            {
                var allTags = MetricTags.Concat(tags, new MetricTags("health_check", healthCheck.Name));

                if (healthCheck.Check.Status == HealthCheckStatus.Unhealthy)
                {
                    _payloadBuilder.Pack("health_checks__unhealhty", healthCheck.Check.Message, allTags);
                }
                else if (healthCheck.Check.Status == HealthCheckStatus.Healthy)
                {
                    _payloadBuilder.Pack("health_checks__healthy", healthCheck.Check.Message, allTags);
                }
                else if (healthCheck.Check.Status == HealthCheckStatus.Degraded)
                {
                    _payloadBuilder.Pack("health_checks__degraded", healthCheck.Check.Message, allTags);
                }
            }

            _logger.LogTrace($"Packed Health Checks for {Name}");
        }

        public void ReportMetric<T>(string context, MetricValueSourceBase<T> valueSource)
        {
            _logger.LogTrace($"Packing Metric {typeof(T)} for {Name}");

            if (typeof(T) == typeof(double))
            {
                ReportGauge(context, valueSource as MetricValueSourceBase<double>);
                return;
            }

            if (typeof(T) == typeof(CounterValue))
            {
                ReportCounter(context, valueSource as MetricValueSourceBase<CounterValue>);
                return;
            }

            if (typeof(T) == typeof(MeterValue))
            {
                ReportMeter(context, valueSource as MetricValueSourceBase<MeterValue>);
                return;
            }

            if (typeof(T) == typeof(TimerValue))
            {
                ReportTimer(context, valueSource as MetricValueSourceBase<TimerValue>);
                return;
            }

            if (typeof(T) == typeof(HistogramValue))
            {
                ReportHistogram(context, valueSource as MetricValueSourceBase<HistogramValue>);
                return;
            }

            if (typeof(T) == typeof(ApdexValue))
            {
                ReportApdex(context, valueSource as MetricValueSourceBase<ApdexValue>);
                return;
            }

            _logger.LogTrace($"Finished Packing Metric {typeof(T)} for {Name}");
        }

        public void StartReportRun(IMetrics metrics)
        {
            _logger.LogTrace($"Starting {Name} Report Run");

            _payloadBuilder.Init();
        }

        private void ReportApdex(string context, MetricValueSourceBase<ApdexValue> valueSource)
        {
            var apdexValueSource = valueSource as ApdexValueSource;

            if (apdexValueSource == null)
            {
                return;
            }

            var data = new Dictionary<string, object>();

            valueSource.Value.AddApdexValues(data);

            _payloadBuilder.PackValueSource(_metricNameFormatter, context, valueSource, data);
        }

        private void ReportCounter(string context, MetricValueSourceBase<CounterValue> valueSource)
        {
            var counterValueSource = valueSource as CounterValueSource;

            if (counterValueSource == null)
            {
                return;
            }

            if (counterValueSource.Value.Items.Any() && counterValueSource.ReportSetItems)
            {
                foreach (var item in counterValueSource.Value.Items.Distinct())
                {
                    _payloadBuilder.PackCounterSetItems(_metricNameFormatter, context, valueSource, item, counterValueSource);
                }
            }

            _payloadBuilder.PackValueSource(_metricNameFormatter, context, valueSource, counterValueSource);
        }

        private void ReportGauge(string context, MetricValueSourceBase<double> valueSource)
        {
            if (!double.IsNaN(valueSource.Value) && !double.IsInfinity(valueSource.Value))
            {
                _payloadBuilder.PackValueSource(_metricNameFormatter, context, valueSource);
            }
        }

        private void ReportHistogram(string context, MetricValueSourceBase<HistogramValue> valueSource)
        {
            var data = new Dictionary<string, object>();

            valueSource.Value.AddHistogramValues(data);

            _payloadBuilder.PackValueSource(_metricNameFormatter, context, valueSource, data);
        }

        private void ReportMeter(string context, MetricValueSourceBase<MeterValue> valueSource)
        {
            var data = new Dictionary<string, object>();

            if (valueSource.Value.Items.Any())
            {
                foreach (var item in valueSource.Value.Items.Distinct())
                {
                    _payloadBuilder.PackMeterSetItems(_metricNameFormatter, context, valueSource, item);
                }
            }

            valueSource.Value.AddMeterValues(data);

            _payloadBuilder.PackValueSource(_metricNameFormatter, context, valueSource, data);
        }

        private void ReportTimer(string context, MetricValueSourceBase<TimerValue> valueSource)
        {
            var data = new Dictionary<string, object>();

            valueSource.Value.Rate.AddMeterValues(data);
            valueSource.Value.Histogram.AddHistogramValues(data);

            _payloadBuilder.PackValueSource(_metricNameFormatter, context, valueSource, data);
        }
    }
}