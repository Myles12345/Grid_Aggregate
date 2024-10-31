using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Geometries.Prepared;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using Parallel = System.Threading.Tasks.Parallel;

class Program
{
    static void Main()
    {
        // Set up parameters
        var nPoints = 5000;  // Adjusted for demonstration
        double lonMin = -74.1, lonMax = -73.9, latMin = 40.7, latMax = 40.85;
        double gridSize = 500;  // 500m grid cells
        
        // Step 1: Generate random points within bounding box
        var random = new Random();
        List<Coordinate> puCoordinates = new List<Coordinate>();
        List<Coordinate> doCoordinates = new List<Coordinate>();

        for (int i = 0; i < nPoints; i++)
        {
            double lon = lonMin + random.NextDouble() * (lonMax - lonMin);
            double lat = latMin + random.NextDouble() * (latMax - latMin);
            puCoordinates.Add(new Coordinate(lon, lat));
            
            lon = lonMin + random.NextDouble() * (lonMax - lonMin);
            lat = latMin + random.NextDouble() * (latMax - latMin);
            doCoordinates.Add(new Coordinate(lon, lat));
        }

        // Step 2: Transform coordinates to a projected CRS for accurate distance calculations (Web Mercator)
        var wgs84 = GeographicCoordinateSystem.WGS84;
        var mercator = ProjectedCoordinateSystem.WebMercator;
        var transform = new CoordinateTransformationFactory().CreateFromCoordinateSystems(wgs84, mercator);

        puCoordinates = puCoordinates.Select(c => transform.Transform(new Coordinate(c.X, c.Y))).ToList();
        doCoordinates = doCoordinates.Select(c => transform.Transform(new Coordinate(c.X, c.Y))).ToList();

        // Step 3: Create 500m x 500m grid covering the bounding box
        var boundingBox = GeometryFactory.Default.CreatePolygon(new[]
        {
            new Coordinate(lonMin, latMin),
            new Coordinate(lonMax, latMin),
            new Coordinate(lonMax, latMax),
            new Coordinate(lonMin, latMax),
            new Coordinate(lonMin, latMin)
        }).EnvelopeInternal;

        var gridCells = new List<Polygon>();
        for (double x = boundingBox.MinX; x < boundingBox.MaxX; x += gridSize)
        {
            for (double y = boundingBox.MinY; y < boundingBox.MaxY; y += gridSize)
            {
                gridCells.Add(GeometryFactory.Default.CreatePolygon(new[]
                {
                    new Coordinate(x, y),
                    new Coordinate(x + gridSize, y),
                    new Coordinate(x + gridSize, y + gridSize),
                    new Coordinate(x, y + gridSize),
                    new Coordinate(x, y)
                }));
            }
        }

        // Step 4: Count points within each grid cell (using parallel processing)
        var puCounts = new Dictionary<Polygon, int>();
        var doCounts = new Dictionary<Polygon, int>();

        Parallel.ForEach(gridCells, cell =>
        {
            int puCount = puCoordinates.Count(pu => cell.Covers(new Point(pu)));
            int doCount = doCoordinates.Count(doCoord => cell.Covers(new Point(doCoord)));

            lock (puCounts)
            {
                puCounts[cell] = puCount;
                doCounts[cell] = doCount;
            }
        });

        // Step 5: Filter and Export as GeoJSON for Visualization
        var gridResults = gridCells
            .Where(cell => puCounts[cell] > 0 || doCounts[cell] > 0)
            .Select(cell => new
            {
                Geometry = cell,
                PUCount = puCounts[cell],
                DOCount = doCounts[cell]
            })
            .ToList();

        // For each cell with counts, export data in GeoJSON format or visualize using your preferred method.
        foreach (var result in gridResults)
        {
            Console.WriteLine($"Grid Cell: {result.Geometry}; PU Count: {result.PUCount}; DO Count: {result.DOCount}");
        }
    }
}
