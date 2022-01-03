// <copyright file="AggregatorStore.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using OpenTelemetry.Internal;

namespace OpenTelemetry.Metrics
{
    internal sealed class AggregatorStore
    {
        private static readonly ObjectArrayEqualityComparer ObjectArrayComparer = new ObjectArrayEqualityComparer();
        private readonly object lockZeroTags = new object();
        private readonly HashSet<string> tagKeysInteresting;
        private readonly int tagsKeysInterestingCount;

        // Two-Level lookup. TagKeys x [ TagValues x Metrics ]
        private readonly ConcurrentDictionary<string[], ConcurrentDictionary<object[], int>> keyValue2MetricAggs =
            new ConcurrentDictionary<string[], ConcurrentDictionary<object[], int>>(new StringArrayEqualityComparer());

        private readonly AggregationTemporality temporality;
        private readonly string name;
        private readonly string metricPointCapHitMessage;
        private readonly bool outputDelta;
        private readonly MetricPoint[] metricPoints;
        private readonly int[] currentMetricPointBatch;
        private readonly AggregationType aggType;
        private readonly double[] histogramBounds;
        private readonly UpdateLongDelegate updateLongCallback;
        private readonly UpdateDoubleDelegate updateDoubleCallback;
        private readonly int maxMetricPoints;
        private int metricPointIndex = 0;
        private int batchSize = 0;
        private int metricCapHitMessageLogged;
        private bool zeroTagMetricPointInitialized;
        private DateTimeOffset startTimeExclusive;
        private DateTimeOffset endTimeInclusive;

        internal AggregatorStore(
            string name,
            AggregationType aggType,
            AggregationTemporality temporality,
            int maxMetricPoints,
            double[] histogramBounds,
            string[] tagKeysInteresting = null)
        {
            this.name = name;
            this.maxMetricPoints = maxMetricPoints;
            this.metricPointCapHitMessage = $"Maximum MetricPoints limit reached for this Metric stream. Configured limit: {this.maxMetricPoints}";
            this.metricPoints = new MetricPoint[maxMetricPoints];
            this.currentMetricPointBatch = new int[maxMetricPoints];
            this.aggType = aggType;
            this.temporality = temporality;
            this.outputDelta = temporality == AggregationTemporality.Delta ? true : false;
            this.histogramBounds = histogramBounds;
            this.startTimeExclusive = DateTimeOffset.UtcNow;
            if (tagKeysInteresting == null)
            {
                this.updateLongCallback = this.UpdateLong;
                this.updateDoubleCallback = this.UpdateDouble;
            }
            else
            {
                this.updateLongCallback = this.UpdateLongCustomTags;
                this.updateDoubleCallback = this.UpdateDoubleCustomTags;
                var hs = new HashSet<string>(tagKeysInteresting, StringComparer.Ordinal);
                this.tagKeysInteresting = hs;
                this.tagsKeysInterestingCount = hs.Count;
            }
        }

        private delegate void UpdateLongDelegate(long value, ReadOnlySpan<KeyValuePair<string, object>> tags);

        private delegate void UpdateDoubleDelegate(double value, ReadOnlySpan<KeyValuePair<string, object>> tags);

        internal void Update(long value, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            this.updateLongCallback(value, tags);
        }

        internal void Update(double value, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            this.updateDoubleCallback(value, tags);
        }

        internal int Snapshot()
        {
            this.batchSize = 0;
            var indexSnapshot = Math.Min(this.metricPointIndex, this.maxMetricPoints - 1);
            if (this.temporality == AggregationTemporality.Delta)
            {
                this.SnapshotDelta(indexSnapshot);
            }
            else
            {
                this.SnapshotCumulative(indexSnapshot);
            }

            this.endTimeInclusive = DateTimeOffset.UtcNow;
            return this.batchSize;
        }

        internal void SnapshotDelta(int indexSnapshot)
        {
            for (int i = 0; i <= indexSnapshot; i++)
            {
                ref var metricPoint = ref this.metricPoints[i];
                if (metricPoint.MetricPointStatus == MetricPointStatus.NoCollectPending)
                {
                    continue;
                }

                metricPoint.TakeSnapshot(this.outputDelta);
                this.currentMetricPointBatch[this.batchSize] = i;
                this.batchSize++;
            }

            if (this.endTimeInclusive != default)
            {
                this.startTimeExclusive = this.endTimeInclusive;
            }
        }

        internal void SnapshotCumulative(int indexSnapshot)
        {
            for (int i = 0; i <= indexSnapshot; i++)
            {
                ref var metricPoint = ref this.metricPoints[i];
                if (metricPoint.StartTime == default)
                {
                    continue;
                }

                metricPoint.TakeSnapshot(this.outputDelta);
                this.currentMetricPointBatch[this.batchSize] = i;
                this.batchSize++;
            }
        }

        internal MetricPointsAccessor GetMetricPoints()
        {
            return new MetricPointsAccessor(this.metricPoints, this.currentMetricPointBatch, this.batchSize, this.startTimeExclusive, this.endTimeInclusive);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeZeroTagPointIfNotInitialized()
        {
            if (!this.zeroTagMetricPointInitialized)
            {
                lock (this.lockZeroTags)
                {
                    if (!this.zeroTagMetricPointInitialized)
                    {
                        var dt = DateTimeOffset.UtcNow;
                        this.metricPoints[0] = new MetricPoint(this.aggType, dt, null, null, this.histogramBounds);
                        this.zeroTagMetricPointInitialized = true;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int LookupAggregatorStore(string[] tagKeys, object[] tagValues, int length)
        {
            int aggregatorIndex;
            string[] seqKey = null;

            // GetOrAdd by TagKeys at 1st Level of 2-level dictionary structure.
            // Get back a Dictionary of [ Values x Metrics[] ].
            if (!this.keyValue2MetricAggs.TryGetValue(tagKeys, out var value2metrics))
            {
                // Note: We are using storage from ThreadStatic, so need to make a deep copy for Dictionary storage.
                seqKey = new string[length];
                tagKeys.CopyTo(seqKey, 0);

                value2metrics = new ConcurrentDictionary<object[], int>(ObjectArrayComparer);
                if (!this.keyValue2MetricAggs.TryAdd(seqKey, value2metrics))
                {
                    this.keyValue2MetricAggs.TryGetValue(seqKey, out value2metrics);
                }
            }

            // GetOrAdd by TagValues at 2st Level of 2-level dictionary structure.
            // Get back Metrics[].
            if (!value2metrics.TryGetValue(tagValues, out aggregatorIndex))
            {
                aggregatorIndex = this.metricPointIndex;
                if (aggregatorIndex >= this.maxMetricPoints)
                {
                    // sorry! out of data points.
                    // TODO: Once we support cleanup of
                    // unused points (typically with delta)
                    // we can re-claim them here.
                    return -1;
                }

                lock (value2metrics)
                {
                    // check again after acquiring lock.
                    if (!value2metrics.TryGetValue(tagValues, out aggregatorIndex))
                    {
                        aggregatorIndex = Interlocked.Increment(ref this.metricPointIndex);
                        if (aggregatorIndex >= this.maxMetricPoints)
                        {
                            // sorry! out of data points.
                            // TODO: Once we support cleanup of
                            // unused points (typically with delta)
                            // we can re-claim them here.
                            return -1;
                        }

                        // Note: We are using storage from ThreadStatic, so need to make a deep copy for Dictionary storage.
                        if (seqKey == null)
                        {
                            seqKey = new string[length];
                            tagKeys.CopyTo(seqKey, 0);
                        }

                        var seqVal = new object[length];
                        tagValues.CopyTo(seqVal, 0);

                        ref var metricPoint = ref this.metricPoints[aggregatorIndex];
                        var dt = DateTimeOffset.UtcNow;
                        metricPoint = new MetricPoint(this.aggType, dt, seqKey, seqVal, this.histogramBounds);

                        // Add to dictionary *after* initializing MetricPoint
                        // as other threads can start writing to the
                        // MetricPoint, if dictionary entry found.
                        value2metrics.TryAdd(seqVal, aggregatorIndex);
                    }
                }
            }

            return aggregatorIndex;
        }

        private void UpdateLong(long value, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            try
            {
                var index = this.FindMetricAggregatorsDefault(tags);
                if (index < 0)
                {
                    if (Interlocked.CompareExchange(ref this.metricCapHitMessageLogged, 1, 0) == 0)
                    {
                        OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, this.metricPointCapHitMessage, "Modify instrumentation to reduce the number of unique key/value pair combinations. Or use MeterProviderBuilder.SetMaxMetricPointsPerMetricStream to set higher limit.");
                    }

                    return;
                }

                this.metricPoints[index].Update(value);
            }
            catch (Exception)
            {
                OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, "SDK internal error occurred.", "Contact SDK owners.");
            }
        }

        private void UpdateLongCustomTags(long value, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            try
            {
                var index = this.FindMetricAggregatorsCustomTag(tags);
                if (index < 0)
                {
                    if (Interlocked.CompareExchange(ref this.metricCapHitMessageLogged, 1, 0) == 0)
                    {
                        OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, this.metricPointCapHitMessage, "Modify instrumentation to reduce the number of unique key/value pair combinations. Or use MeterProviderBuilder.SetMaxMetricPointsPerMetricStream to set higher limit.");
                    }

                    return;
                }

                this.metricPoints[index].Update(value);
            }
            catch (Exception)
            {
                OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, "SDK internal error occurred.", "Contact SDK owners.");
            }
        }

        private void UpdateDouble(double value, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            try
            {
                var index = this.FindMetricAggregatorsDefault(tags);
                if (index < 0)
                {
                    if (Interlocked.CompareExchange(ref this.metricCapHitMessageLogged, 1, 0) == 0)
                    {
                        OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, this.metricPointCapHitMessage, "Modify instrumentation to reduce the number of unique key/value pair combinations. Or use MeterProviderBuilder.SetMaxMetricPointsPerMetricStream to set higher limit.");
                    }

                    return;
                }

                this.metricPoints[index].Update(value);
            }
            catch (Exception)
            {
                OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, "SDK internal error occurred.", "Contact SDK owners.");
            }
        }

        private void UpdateDoubleCustomTags(double value, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            try
            {
                var index = this.FindMetricAggregatorsCustomTag(tags);
                if (index < 0)
                {
                    if (Interlocked.CompareExchange(ref this.metricCapHitMessageLogged, 1, 0) == 0)
                    {
                        OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, this.metricPointCapHitMessage, "Modify instrumentation to reduce the number of unique key/value pair combinations. Or use MeterProviderBuilder.SetMaxMetricPointsPerMetricStream to set higher limit.");
                    }

                    return;
                }

                this.metricPoints[index].Update(value);
            }
            catch (Exception)
            {
                OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, "SDK internal error occurred.", "Contact SDK owners.");
            }
        }

        private int FindMetricAggregatorsDefault(ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            int tagLength = tags.Length;
            if (tagLength == 0)
            {
                this.InitializeZeroTagPointIfNotInitialized();
                return 0;
            }

            var storage = ThreadStaticStorage.GetStorage();

            storage.SplitToKeysAndValues(tags, tagLength, out var tagKeys, out var tagValues);

            if (tagLength > 1)
            {
                Array.Sort(tagKeys, tagValues);
            }

            return this.LookupAggregatorStore(tagKeys, tagValues, tagLength);
        }

        private int FindMetricAggregatorsCustomTag(ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            int tagLength = tags.Length;
            if (tagLength == 0 || this.tagsKeysInterestingCount == 0)
            {
                this.InitializeZeroTagPointIfNotInitialized();
                return 0;
            }

            // TODO: Get only interesting tags
            // from the incoming tags

            var storage = ThreadStaticStorage.GetStorage();

            storage.SplitToKeysAndValues(tags, tagLength, this.tagKeysInteresting, out var tagKeys, out var tagValues, out var actualLength);

            // Actual number of tags depend on how many
            // of the incoming tags has user opted to
            // select.
            if (actualLength == 0)
            {
                this.InitializeZeroTagPointIfNotInitialized();
                return 0;
            }

            if (actualLength > 1)
            {
                Array.Sort(tagKeys, tagValues);
            }

            return this.LookupAggregatorStore(tagKeys, tagValues, actualLength);
        }
    }
}