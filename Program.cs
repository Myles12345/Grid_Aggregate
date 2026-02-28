using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using SpatialAggregation;

class Program
{
    static void Main()
    {
        // ----------------------------------------------------------------
        // Parameters
        // ----------------------------------------------------------------
        const int    nEvents   = 5000;          // events per type (supply / demand)
        const double lonMin    = -74.1;
        const double lonMax    = -73.9;
        const double latMin    =  40.7;
        const double latMax    =  40.85;
        const double gridSize  = 500;           // 500 m cells
        const int    minThresh = 5;             // minimum events to classify a zone
        var random = new Random(42);

        // ----------------------------------------------------------------
        // 1. Generate raw WGS84 coordinates
        //    Supply  = drivers becoming available  (completing a dropoff)
        //    Demand  = passengers requesting a ride (originating a pickup)
        // ----------------------------------------------------------------
        List<Coordinate> supplyRaw = SpatialAggregator.GenerateRandomPoints(
            nEvents, lonMin, lonMax, latMin, latMax, random);

        List<Coordinate> demandRaw = SpatialAggregator.GenerateRandomPoints(
            nEvents, lonMin, lonMax, latMin, latMax, random);

        // Assign each event a random hour of day [0-23].
        int[] supplyHours = new int[nEvents];
        int[] demandHours = new int[nEvents];
        for (int i = 0; i < nEvents; i++)
        {
            supplyHours[i] = random.Next(0, 24);
            demandHours[i] = random.Next(0, 24);
        }

        // ----------------------------------------------------------------
        // 2. Project WGS84 → Web Mercator (EPSG:3857) for metre-based grid
        // ----------------------------------------------------------------
        var transformFactory = new CoordinateTransformationFactory();
        var transform = transformFactory.CreateFromCoordinateSystems(
            GeographicCoordinateSystem.WGS84,
            ProjectedCoordinateSystem.WebMercator);

        Coordinate[] supplyProjected = SpatialAggregator.TransformCoordinates(supplyRaw, transform);
        Coordinate[] demandProjected = SpatialAggregator.TransformCoordinates(demandRaw, transform);

        // ----------------------------------------------------------------
        // 3. Build the grid envelope from all projected points
        // ----------------------------------------------------------------
        Envelope boundingBox = SpatialAggregator.CalculateBoundingBox(supplyProjected, demandProjected);

        // ----------------------------------------------------------------
        // 4. Assemble GridEvent list
        // ----------------------------------------------------------------
        var events = new List<GridEvent>(nEvents * 2);
        for (int i = 0; i < nEvents; i++)
        {
            events.Add(new GridEvent(supplyProjected[i].X, supplyProjected[i].Y,
                                     supplyHours[i], EventType.Supply));
            events.Add(new GridEvent(demandProjected[i].X, demandProjected[i].Y,
                                     demandHours[i], EventType.Demand));
        }

        // ----------------------------------------------------------------
        // 5. Aggregate — O(1) cell lookup, lock-free parallel writes
        // ----------------------------------------------------------------
        List<HourlyZoneResult> results = SpatialAggregator.AggregateHourly(
            events, boundingBox, gridSize, minThresh);

        // ----------------------------------------------------------------
        // 6. Summary
        // ----------------------------------------------------------------
        int countSupply      = results.Count(r => r.Status == ZoneStatus.NetSupply);
        int countDemand      = results.Count(r => r.Status == ZoneStatus.NetDemand);
        int countBalanced    = results.Count(r => r.Status == ZoneStatus.Balanced);
        int countUnsupported = results.Count(r => r.Status == ZoneStatus.Unsupported);

        Console.WriteLine("=== Grid Aggregate — Hourly Supply/Demand Summary ===");
        Console.WriteLine($"Grid size      : {gridSize} m");
        Console.WriteLine($"Activity threshold: {minThresh} events/cell-hour");
        Console.WriteLine($"Total active cell-hours : {results.Count}");
        Console.WriteLine($"  Net Supply   (drivers > requests) : {countSupply}");
        Console.WriteLine($"  Net Demand   (requests > drivers) : {countDemand}");
        Console.WriteLine($"  Balanced     (supply == demand)   : {countBalanced}");
        Console.WriteLine($"  Unsupported  (below threshold)    : {countUnsupported}");
        Console.WriteLine();

        // ----------------------------------------------------------------
        // 7. Per-cell detail — ordered by hour then cell index
        // ----------------------------------------------------------------
        Console.WriteLine("Hour | Cell (X, Y) | Supply | Demand |   Net | Status");
        Console.WriteLine("-----|-------------|--------|--------|-------|------------");

        double originX = boundingBox.MinX;
        double originY = boundingBox.MinY;

        foreach (var r in results
            .Where(r => r.Status != ZoneStatus.Unsupported)
            .OrderBy(r => r.Hour)
            .ThenBy(r => r.CellX)
            .ThenBy(r => r.CellY))
        {
            // Centroid of the cell in Web Mercator metres (useful for downstream mapping)
            double centroidX = originX + (r.CellX + 0.5) * gridSize;
            double centroidY = originY + (r.CellY + 0.5) * gridSize;

            Console.WriteLine(
                $" {r.Hour:D2}  | ({r.CellX,3},{r.CellY,3}) [{centroidX:F0},{centroidY:F0}]" +
                $" | Supply: {r.Supply,4} | Demand: {r.Demand,4} | Net: {r.Net,+4} | {r.Status}");
        }

        // Unsupported zones listed separately for operational awareness
        if (countUnsupported > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {countUnsupported} Unsupported cell-hours " +
                              $"(below {minThresh}-event threshold) ---");
            foreach (var r in results
                .Where(r => r.Status == ZoneStatus.Unsupported)
                .OrderBy(r => r.Hour).ThenBy(r => r.CellX).ThenBy(r => r.CellY))
            {
                Console.WriteLine(
                    $" {r.Hour:D2}  | ({r.CellX,3},{r.CellY,3}) " +
                    $"| Supply: {r.Supply} | Demand: {r.Demand}");
            }
        }
    }
}
