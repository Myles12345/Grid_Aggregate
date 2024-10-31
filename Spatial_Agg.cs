using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using System.Threading.Tasks;

class Program
{
    static void Main()
    {
        // Set up parameters
        int nPoints = 5000;  // Adjusted for demonstration; increase to 500,000 for full-scale
        double lonMin = -74.1, lonMax = -73.9, latMin = 40.7, latMax = 40.85;
        double gridSize = 500;  // 500m grid cells

        // Generate random points for PU and DO within bounding box
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

        // Step 2: Define the CRS and transformation to Web Mercator (EPSG:3857)
        var wgs84 = GeographicCoordinateSystem.WGS84;
        var mercator = ProjectedCoordinateSystem.WebMercator;
        var coordinateTransform = new CoordinateTransformationFactory().CreateFromCoordinateSystems(wgs84, mercator);

        // Transform coordinates to Web Mercator, with debug output
        puCoordinates = puCoordinates.Select(coord =>
            {
                var transformedCoord = coordinateTransform.MathTransform.Transform(new double[] { coord.X, coord.Y });
                Console.WriteLine($"Transformed PU Coordinate: X={transformedCoord[0]}, Y={transformedCoord[1]}");  // Debug: Check transformed coordinates
                return new Coordinate(transformedCoord[0], transformedCoord[1]);
            }).ToList();

        doCoordinates = doCoordinates.Select(coord =>
            {
                var transformedCoord = coordinateTransform.MathTransform.Transform(new double[] { coord.X, coord.Y });
                Console.WriteLine($"Transformed DO Coordinate: X={transformedCoord[0]}, Y={transformedCoord[1]}");  // Debug: Check transformed coordinates
                return new Coordinate(transformedCoord[0], transformedCoord[1]);
            }).ToList();

        // Step 3: Define bounding box and dynamically adjust it based on transformed coordinates
        double minX = puCoordinates.Concat(doCoordinates).Min(coord => coord.X);
        double minY = puCoordinates.Concat(doCoordinates).Min(coord => coord.Y);
        double maxX = puCoordinates.Concat(doCoordinates).Max(coord => coord.X);
        double maxY = puCoordinates.Concat(doCoordinates).Max(coord => coord.Y);

        var boundingBox = new Envelope(minX, maxX, minY, maxY);
        Console.WriteLine($"Dynamic Bounding Box: {boundingBox}");  // Debug: Verify bounding box covers all points

        // Step 4: Create 500m x 500m grid within bounding box and check grid cells
        var gridCells = new List<Polygon>();
        for (double x = boundingBox.MinX; x < boundingBox.MaxX; x += gridSize)
        {
            for (double y = boundingBox.MinY; y < boundingBox.MaxY; y += gridSize)
            {
                var cell = GeometryFactory.Default.CreatePolygon(new[]
                {
                    new Coordinate(x, y),
                    new Coordinate(x + gridSize, y),
                    new Coordinate(x + gridSize, y + gridSize),
                    new Coordinate(x, y + gridSize),
                    new Coordinate(x, y)
                });
                Console.WriteLine($"Grid Cell Created with Coordinates: {cell.Envelope}");  // Debug: Print grid cell envelope to verify
                gridCells.Add(cell);
            }
        }

        // Step 5: Count points within each grid cell using parallel processing and different intersection methods
        var puCounts = new Dictionary<Polygon, int>();
        var doCounts = new Dictionary<Polygon, int>();

        Parallel.ForEach(gridCells, cell =>
        {
            // Using Intersects instead of Covers to verify coverage
            int puCount = puCoordinates.Count(pu => cell.Intersects(new Point(pu)));
            int doCount = doCoordinates.Count(doCoord => cell.Intersects(new Point(doCoord)));

            lock (puCounts)
            {
                if (puCount > 0 || doCount > 0)  // Only report non-zero counts for debugging clarity
                {
                    Console.WriteLine($"Grid Cell has counts: PU={puCount}, DO={doCount}");  // Debug: Non-zero cell counts
                }
                puCounts[cell] = puCount;
                doCounts[cell] = doCount;
            }
        });

        // Step 6: Output results or write to GeoJSON
        var gridResults = gridCells
            .Where(cell => puCounts[cell] > 0 || doCounts[cell] > 0)
            .Select(cell => new
            {
                Geometry = cell,
                PUCount = puCounts[cell],
                DOCount = doCounts[cell]
            })
            .ToList();

        foreach (var result in gridResults)
        {
            Console.WriteLine($"Grid Cell: {result.Geometry}; PU Count: {result.PUCount}; DO Count: {result.DOCount}");
        }
    }
}
