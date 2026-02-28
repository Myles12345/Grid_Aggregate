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

    /// <summary>Supply = driver becoming available; Demand = passenger requesting a ride.</summary>
    public enum EventType { Supply, Demand }

    /// <summary>
    /// Classification of a grid cell in a given hour bucket.
    ///   NetSupply   – more available drivers than ride requests
    ///   NetDemand   – more ride requests than available drivers
    ///   Balanced    – supply equals demand (and above the activity threshold)
    ///   Unsupported – total activity below the minimum threshold; no reliable signal
    /// </summary>
    public enum ZoneStatus { Unsupported, NetSupply, NetDemand, Balanced }

    /// <summary>A single rideshare event (driver available or passenger request) in projected space.</summary>
    /// <param name="X">Web Mercator X (metres).</param>
    /// <param name="Y">Web Mercator Y (metres).</param>
    /// <param name="Hour">Hour of day [0–23].</param>
    /// <param name="Type">Supply (driver) or Demand (passenger).</param>
    public record GridEvent(double X, double Y, int Hour, EventType Type);

    /// <summary>Aggregated result for one cell/hour combination.</summary>
    public record HourlyZoneResult(
        int CellX, int CellY, int Hour,
        int Supply, int Demand,
        ZoneStatus Status)
    {
        /// <summary>Positive = net driver surplus; negative = net passenger surplus.</summary>
        public int Net => Supply - Demand;
    }

    // -----------------------------------------------------------------------
    // Aggregator
    // -----------------------------------------------------------------------

    public static class SpatialAggregator
    {
        // -------------------------------------------------------------------
        // Point generation (unchanged – used for test data)
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
        // Coordinate transformation — parallelised; MathTransform is stateless
        // for well-known projections (WGS84 → WebMercator) so parallel reads
        // are safe.
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

        // -------------------------------------------------------------------
        // Core aggregation
        //
        // Algorithm:
        //   1. Pre-allocate int[24, gridWidth, gridHeight] arrays for supply
        //      and demand counts.
        //   2. For each event, compute its cell index in O(1) using floor
        //      arithmetic — no geometric loop, no Polygon.Contains().
        //   3. Write to the array with Interlocked.Increment — zero lock
        //      contention, full parallel throughput.
        //   4. Single classification pass: emit only cell-hours with activity.
        // -------------------------------------------------------------------

        /// <param name="events">All supply and demand events in projected (metre) space.</param>
        /// <param name="gridOrigin">Bounding envelope that defines the grid origin and extent.</param>
        /// <param name="gridSize">Cell size in metres (e.g. 500).</param>
        /// <param name="minActivityThreshold">
        ///   Minimum total events (supply + demand) required for a cell-hour to
        ///   be classified as NetSupply / NetDemand / Balanced. Below this value
        ///   the zone is marked Unsupported.
        /// </param>
        public static List<HourlyZoneResult> AggregateHourly(
            IReadOnlyList<GridEvent> events,
            Envelope gridOrigin,
            double gridSize,
            int minActivityThreshold = 5)
        {
            int gridWidth  = (int)Math.Ceiling(gridOrigin.Width  / gridSize);
            int gridHeight = (int)Math.Ceiling(gridOrigin.Height / gridSize);
            double originX = gridOrigin.MinX;
            double originY = gridOrigin.MinY;

            // Pre-allocate accumulators — indexed [hour, cellX, cellY].
            // Default value of int[] elements is 0, so no explicit init needed.
            int[,,] supplyCounts = new int[24, gridWidth, gridHeight];
            int[,,] demandCounts = new int[24, gridWidth, gridHeight];

            // Parallel accumulation — lock-free via Interlocked.Increment.
            Parallel.ForEach(events, evt =>
            {
                int cx = (int)Math.Floor((evt.X - originX) / gridSize);
                int cy = (int)Math.Floor((evt.Y - originY) / gridSize);

                // Guard against floating-point edge cases at the boundary.
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

                if (total == 0) continue;   // completely empty cell-hour — skip

                ZoneStatus status = total < minActivityThreshold
                    ? ZoneStatus.Unsupported
                    : (supply - demand) switch
                    {
                        > 0 => ZoneStatus.NetSupply,
                        < 0 => ZoneStatus.NetDemand,
                        _   => ZoneStatus.Balanced
                    };

                results.Add(new HourlyZoneResult(cx, cy, hr, supply, demand, status));
            }

            return results;
        }
    }
}
