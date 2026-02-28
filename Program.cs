using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using SpatialAggregation;

// ────────────────────────────────────────────────────────────────────────────
// Usage:
//   dotnet run                          # synthetic data → map.html
//   dotnet run -- --input events.csv   # load CSV       → map.html
//   dotnet run -- --input events.csv --output out.html --capacity 2.5 --grid-size 500 --threshold 5
//   dotnet run -- --sample             # write a sample events.csv and exit
//
// All flags are optional; defaults are shown above.
// ────────────────────────────────────────────────────────────────────────────

class Program
{
    static void Main(string[] args)
    {
        // ── Parse CLI args ────────────────────────────────────────────────
        string? inputFile  = GetArg(args, "--input");
        string  outputFile = GetArg(args, "--output") ?? "map.html";
        bool    writeSample = args.Contains("--sample");

        double gridSize  = double.TryParse(GetArg(args, "--grid-size"),  out double gs)  ? gs  : 500.0;
        double capacity  = double.TryParse(GetArg(args, "--capacity"),   out double cap) ? cap : 2.0;
        int    threshold = int.TryParse(   GetArg(args, "--threshold"),  out int thr)    ? thr : 5;

        // ── Coordinate transform (shared) ─────────────────────────────────
        var transformFactory = new CoordinateTransformationFactory();
        var transform = transformFactory.CreateFromCoordinateSystems(
            GeographicCoordinateSystem.WGS84,
            ProjectedCoordinateSystem.WebMercator);

        // ── Sample CSV writer ─────────────────────────────────────────────
        if (writeSample)
        {
            const string samplePath = "events_sample.csv";
            FileIngestionService.WriteSampleCsv(samplePath);
            Console.WriteLine($"Sample CSV written to: {samplePath}");
            Console.WriteLine("Edit it and re-run with: dotnet run -- --input events_sample.csv");
            return;
        }

        // ── Load events ───────────────────────────────────────────────────
        List<GridEvent> events;
        Envelope boundingBox;

        if (inputFile != null)
        {
            Console.WriteLine($"Loading events from: {inputFile}");
            var result = FileIngestionService.LoadCsv(inputFile, transform);

            Console.WriteLine($"  Lines read  : {result.LinesRead}");
            Console.WriteLine($"  Events loaded: {result.Events.Count}");
            if (result.ParseErrors > 0)
                Console.WriteLine($"  Parse errors : {result.ParseErrors}  (see stderr for details)");
            if (result.EarliestDay.HasValue)
                Console.WriteLine($"  Date range  : {result.EarliestDay} → {result.LatestDay}");

            if (result.Events.Count == 0)
            {
                Console.Error.WriteLine("No events loaded — nothing to aggregate. Exiting.");
                return;
            }

            events      = result.Events;
            boundingBox = SpatialAggregator.CalculateBoundingBox(events);
        }
        else
        {
            // ── Synthetic demo data ───────────────────────────────────────
            Console.WriteLine("No --input file specified. Running with synthetic demo data.");
            Console.WriteLine("  Tip: dotnet run -- --sample   to generate a starter CSV.");
            Console.WriteLine();

            const int    nEvents = 5_000;
            const double lonMin  = -74.1;
            const double lonMax  = -73.9;
            const double latMin  =  40.70;
            const double latMax  =  40.85;
            var random = new Random(42);

            // Supply  = drivers completing a dropoff (becoming available)
            // Demand  = passengers originating a pickup request
            var supplyRaw = SpatialAggregator.GenerateRandomPoints(nEvents, lonMin, lonMax, latMin, latMax, random);
            var demandRaw = SpatialAggregator.GenerateRandomPoints(nEvents, lonMin, lonMax, latMin, latMax, random);

            var supplyProjected = SpatialAggregator.TransformCoordinates(supplyRaw, transform);
            var demandProjected = SpatialAggregator.TransformCoordinates(demandRaw, transform);

            boundingBox = SpatialAggregator.CalculateBoundingBox(supplyProjected, demandProjected);

            events = new List<GridEvent>(nEvents * 2);
            for (int i = 0; i < nEvents; i++)
            {
                events.Add(new GridEvent(supplyProjected[i].X, supplyProjected[i].Y,
                                         random.Next(0, 24), EventType.Supply));
                events.Add(new GridEvent(demandProjected[i].X, demandProjected[i].Y,
                                         random.Next(0, 24), EventType.Demand));
            }
        }

        // ── Aggregate ─────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"Grid size        : {gridSize} m");
        Console.WriteLine($"Capacity factor  : {capacity}× trips/driver/hour");
        Console.WriteLine($"Activity threshold: {threshold} events/cell-hour");
        Console.WriteLine();

        List<HourlyZoneResult> results = SpatialAggregator.AggregateHourly(
            events, boundingBox, gridSize,
            driverCapacityFactor:   capacity,
            minActivityThreshold:   threshold);

        // ── Console summary ───────────────────────────────────────────────
        int cntSupply      = results.Count(r => r.Status == ZoneStatus.NetSupply);
        int cntDemand      = results.Count(r => r.Status == ZoneStatus.NetDemand);
        int cntBalanced    = results.Count(r => r.Status == ZoneStatus.Balanced);
        int cntUnsupported = results.Count(r => r.Status == ZoneStatus.Unsupported);

        Console.WriteLine("=== Hourly Supply / Demand Summary ===");
        Console.WriteLine($"Total active cell-hours : {results.Count}");
        Console.WriteLine($"  Net Supply   (capacity > requests) : {cntSupply}");
        Console.WriteLine($"  Net Demand   (requests > capacity) : {cntDemand}");
        Console.WriteLine($"  Balanced     (capacity == requests): {cntBalanced}");
        Console.WriteLine($"  Unsupported  (below threshold)     : {cntUnsupported}");
        Console.WriteLine();

        // Detailed table — only classified zones (skip Unsupported for brevity)
        Console.WriteLine("Hour | Cell (X,  Y) | Drivers | Eff.Supply | Requests |   Net | Status");
        Console.WriteLine("-----|--------------|---------|------------|----------|-------|------------");

        foreach (var r in results
            .Where(r => r.Status != ZoneStatus.Unsupported)
            .OrderBy(r => r.Hour).ThenBy(r => r.CellX).ThenBy(r => r.CellY))
        {
            Console.WriteLine(
                $" {r.Hour:D2}  | ({r.CellX,3},{r.CellY,3})     " +
                $"| {r.Supply,7} | {r.EffectiveSupply,10} | {r.Demand,8} | {r.Net,+5} | {r.Status}");
        }

        // ── Map export ────────────────────────────────────────────────────
        Console.WriteLine();
        Console.Write($"Writing map to: {outputFile} ... ");
        MapExporter.ExportHtml(results, boundingBox, gridSize, outputFile);
        Console.WriteLine("done.");
        Console.WriteLine($"Open {outputFile} in any browser to explore the hourly grid.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
