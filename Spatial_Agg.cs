using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Features;
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

        // Transform coordinates to Web Mercator
        puCoordinates = puCoordinates.Select(coord =>
            new Coordinate(
                coordinateTransform.MathTransform.Transform(new double[] { coord.X, coord.Y })[0],
                coordinateTransform.MathTransform.Transform(new double[] { coord.X, coord.Y })[1]
            )).ToList();

        doCoordinates = doCoordinates.Select(coord =>
            new Coordinate(
                coordinateTransform.MathTransform.Transform(new double[] { coord.X, coord.Y })[0],
                coordinateTransform.MathTransform.Transform(new double[] { coord.X, coord.Y })[1]
            )).ToList();

        // Step 3: Create 500m x 500m grid
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

        // Step 5: Output results or write to GeoJSON
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
