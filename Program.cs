using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using SpatialAggregation; // Added for SpatialAggregator

class Program
{
    static void Main()
    {
        // Set up parameters
        int nPoints = 5000;
        double lonMin = -74.1, lonMax = -73.9, latMin = 40.7, latMax = 40.85;
        double gridSize = 500; // 500m grid cells
        var random = new Random();

        // Generate random points
        List<Coordinate> puCoordinates = SpatialAggregator.GenerateRandomPoints(nPoints, lonMin, lonMax, latMin, latMax, random);
        List<Coordinate> doCoordinates = SpatialAggregator.GenerateRandomPoints(nPoints, lonMin, lonMax, latMin, latMax, random);

        // Define the CRS and transformation
        var wgs84 = GeographicCoordinateSystem.WGS84;
        var mercator = ProjectedCoordinateSystem.WebMercator;
        var coordinateTransformFactory = new CoordinateTransformationFactory();
        var coordinateTransformation = coordinateTransformFactory.CreateFromCoordinateSystems(wgs84, mercator);

        // Transform coordinates
        List<Coordinate> puTransformed = SpatialAggregator.TransformCoordinates(puCoordinates, coordinateTransformation);
        List<Coordinate> doTransformed = SpatialAggregator.TransformCoordinates(doCoordinates, coordinateTransformation);

        // Calculate bounding box
        Envelope boundingBox = SpatialAggregator.CalculateBoundingBox(puTransformed, doTransformed);

        // Create grid
        List<Polygon> gridCells = SpatialAggregator.CreateGrid(boundingBox, gridSize);

        // Count points in grid
        Dictionary<Polygon, (int PUCount, int DOCount)> aggregatedResults = SpatialAggregator.CountPointsInGrid(gridCells, puTransformed, doTransformed);

        // Output results
        foreach (var result in aggregatedResults)
        {
            if (result.Value.PUCount > 0 || result.Value.DOCount > 0)
            {
                Console.WriteLine($"Grid Cell: {result.Key.Coordinates[0].X},{result.Key.Coordinates[0].Y}; PU Count: {result.Value.PUCount}; DO Count: {result.Value.DOCount}");
            }
        }
    }
}
