using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems.Transformations;

namespace SpatialAggregation
{
    // -----------------------------------------------------------------------
    // Domain types
    // -----------------------------------------------------------------------

    /// <summary>
    /// Supply = driver completing a dropoff (becoming available).
    /// Demand = passenger originating a pickup request.
    /// </summary>
    public enum EventType { Supply, Demand }

    /// <summary>
    /// Classification of a grid cell in a given hour bucket.
    ///   NetSupply   – effective driver capacity exceeds ride requests
    ///   NetDemand   – ride requests exceed effective driver capacity
    ///   Balanced    – supply capacity equals demand (above activity threshold)
    ///   Unsupported – total activity below the minimum threshold; no reliable signal
    /// </summary>
    public enum ZoneStatus { Unsupported, NetSupply, NetDemand, Balanced }

    /// <summary>A single rideshare event in projected (Web Mercator) space.</summary>
    public record GridEvent(double X, double Y, int Hour, EventType Type);

    /// <summary>Aggregated result for one cell/hour combination.</summary>
    /// <param name="Supply">Raw driver events observed in this cell-hour.</param>
    /// <param name="EffectiveSupply">
    ///   Supply × driverCapacityFactor — the number of rides a driver pool
    ///   can realistically complete in one hour (e.g., 2 trips per driver).
    /// </param>
    public record HourlyZoneResult(
        int CellX, int CellY, int Hour,
        int Supply, int Demand, int EffectiveSupply,
        ZoneStatus Status)
    {
        /// <summary>
        /// Positive = net driver surplus (more capacity than requests).
        /// Negative = net passenger deficit (more requests than capacity).
        /// </summary>
        public int Net => EffectiveSupply - Demand;
    }

    // -----------------------------------------------------------------------
    // Aggregator
    // -----------------------------------------------------------------------

    public static class SpatialAggregator
    {
        // -------------------------------------------------------------------
        // Point generation — used for synthetic test data
        // -------------------------------------------------------------------

        public static List<Coordinate> GenerateRandomPoints(
            int nPoints,
            double lonMin, double lonMax,
            double latMin, double latMax,
            Random random)
        {
            var points = new List<Coordinate>(nPoints);
            for (int i = 0; i < nPoints; i++)
            {
                double lon = random.NextDouble() * (lonMax - lonMin) + lonMin;
                double lat = random.NextDouble() * (latMax - latMin) + latMin;
                points.Add(new Coordinate(lon, lat));
            }
            return points;
        }

        // -------------------------------------------------------------------
        // Coordinate transformation
        // MathTransform for WGS84 → WebMercator is a pure function — thread-safe.
        // -------------------------------------------------------------------

        public static Coordinate[] TransformCoordinates(
            IReadOnlyList<Coordinate> coordinates,
            ICoordinateTransformation transformation)
        {
            var mathTransform = transformation.MathTransform;
            var result = new Coordinate[coordinates.Count];

            Parallel.For(0, coordinates.Count, i =>
            {
                var pt = coordinates[i];
                double[] t = mathTransform.Transform(new[] { pt.X, pt.Y });
                result[i] = new Coordinate(t[0], t[1]);
            });

            return result;
        }

        // -------------------------------------------------------------------
        // Bounding box — single pass, no LINQ allocation
        // -------------------------------------------------------------------

        public static Envelope CalculateBoundingBox(
            IReadOnlyList<Coordinate> supply,
            IReadOnlyList<Coordinate> demand)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var p in supply)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            foreach (var p in demand)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            return new Envelope(minX, maxX, minY, maxY);
        }

        /// <summary>Overload that derives the bounding box directly from a GridEvent list.</summary>
        public static Envelope CalculateBoundingBox(IReadOnlyList<GridEvent> events)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var e in events)
            {
                if (e.X < minX) minX = e.X;
                if (e.Y < minY) minY = e.Y;
                if (e.X > maxX) maxX = e.X;
                if (e.Y > maxY) maxY = e.Y;
            }

            return new Envelope(minX, maxX, minY, maxY);
        }

        // -------------------------------------------------------------------
        // Core aggregation
        //
        // Algorithm:
        //   1. Pre-allocate int[24, gridWidth, gridHeight] arrays for supply
        //      and demand counts.
        //   2. Compute each event's cell index in O(1) using floor arithmetic —
        //      no geometric loop, no Polygon.Contains().
        //   3. Write to the array with Interlocked.Increment — zero lock
        //      contention, full parallel throughput.
        //   4. Classification pass: apply driverCapacityFactor to raw supply
        //      counts, then emit ZoneStatus per cell-hour.
        // -------------------------------------------------------------------

        /// <param name="events">All supply and demand events in projected (metre) space.</param>
        /// <param name="gridOrigin">Bounding envelope defining the grid origin and extent.</param>
        /// <param name="gridSize">Cell edge length in metres (e.g. 500).</param>
        /// <param name="driverCapacityFactor">
        ///   Average number of ride trips one driver can complete per hour.
        ///   Default 2.0 (one ~25-min trip + pickup time, repeated twice per hour).
        ///   EffectiveSupply = rawSupply × driverCapacityFactor.
        /// </param>
        /// <param name="minActivityThreshold">
        ///   Minimum total raw events (supply + demand) for a cell-hour to be
        ///   classified. Below this it is marked Unsupported.
        /// </param>
        public static List<HourlyZoneResult> AggregateHourly(
            IReadOnlyList<GridEvent> events,
            Envelope gridOrigin,
            double gridSize,
            double driverCapacityFactor = 2.0,
            int minActivityThreshold = 5)
        {
            int gridWidth  = (int)Math.Ceiling(gridOrigin.Width  / gridSize);
            int gridHeight = (int)Math.Ceiling(gridOrigin.Height / gridSize);
            double originX = gridOrigin.MinX;
            double originY = gridOrigin.MinY;

            int[,,] supplyCounts = new int[24, gridWidth, gridHeight];
            int[,,] demandCounts = new int[24, gridWidth, gridHeight];

            // Parallel accumulation — lock-free via Interlocked.Increment.
            Parallel.ForEach(events, evt =>
            {
                int cx = (int)Math.Floor((evt.X - originX) / gridSize);
                int cy = (int)Math.Floor((evt.Y - originY) / gridSize);

                // Guard floating-point boundary edge cases.
                if (cx < 0 || cx >= gridWidth || cy < 0 || cy >= gridHeight) return;

                if (evt.Type == EventType.Supply)
                    Interlocked.Increment(ref supplyCounts[evt.Hour, cx, cy]);
                else
                    Interlocked.Increment(ref demandCounts[evt.Hour, cx, cy]);
            });

            // Classification pass — O(24 × gridWidth × gridHeight).
            var results = new List<HourlyZoneResult>();

            for (int hr = 0; hr < 24; hr++)
            for (int cx = 0; cx < gridWidth; cx++)
            for (int cy = 0; cy < gridHeight; cy++)
            {
                int supply = supplyCounts[hr, cx, cy];
                int demand = demandCounts[hr, cx, cy];
                int total  = supply + demand;

                if (total == 0) continue;

                // Apply capacity factor: each driver can serve ~2 rides/hour.
                int effectiveSupply = (int)Math.Round(supply * driverCapacityFactor);

                ZoneStatus status = total < minActivityThreshold
                    ? ZoneStatus.Unsupported
                    : (effectiveSupply - demand) switch
                    {
                        > 0 => ZoneStatus.NetSupply,
                        < 0 => ZoneStatus.NetDemand,
                        _   => ZoneStatus.Balanced
                    };

                results.Add(new HourlyZoneResult(cx, cy, hr, supply, demand, effectiveSupply, status));
            }

            return results;
        }
    }
}
