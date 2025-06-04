using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace SpatialAggregation
{
    public static class SpatialAggregator
    {
        public static List<Coordinate> GenerateRandomPoints(int nPoints, double lonMin, double lonMax, double latMin, double latMax, Random random)
        {
            var points = new List<Coordinate>();
            for (int i = 0; i < nPoints; i++)
            {
                double lon = random.NextDouble() * (lonMax - lonMin) + lonMin;
                double lat = random.NextDouble() * (latMax - latMin) + latMin;
                points.Add(new Coordinate(lon, lat));
            }
            return points;
        }

        public static List<Coordinate> TransformCoordinates(List<Coordinate> coordinates, ICoordinateTransformation transformation)
        {
            var transformedCoordinates = new List<Coordinate>();
            foreach (var point in coordinates)
            {
                // Create a double array for the transformation input
                double[] transformedPoint = transformation.MathTransform.Transform(new double[] { point.X, point.Y });
                // Create a new Coordinate from the transformed double array
                transformedCoordinates.Add(new Coordinate(transformedPoint[0], transformedPoint[1]));
            }
            return transformedCoordinates;
        }

        public static Envelope CalculateBoundingBox(List<Coordinate> puTransformed, List<Coordinate> doTransformed)
        {
            var allPoints = puTransformed.Concat(doTransformed).ToList();
            double minX = allPoints.Min(p => p.X);
            double minY = allPoints.Min(p => p.Y);
            double maxX = allPoints.Max(p => p.X);
            double maxY = allPoints.Max(p => p.Y);
            return new Envelope(minX, maxX, minY, maxY);
        }

        public static List<Polygon> CreateGrid(Envelope boundingBox, double gridSize)
        {
            var gridCells = new List<Polygon>();
            var geometryFactory = new GeometryFactory();

            for (double x = boundingBox.MinX; x < boundingBox.MaxX; x += gridSize)
            {
                for (double y = boundingBox.MinY; y < boundingBox.MaxY; y += gridSize)
                {
                    var envelope = new Envelope(x, x + gridSize, y, y + gridSize);
                    gridCells.Add((Polygon)geometryFactory.ToGeometry(envelope));
                }
            }
            return gridCells;
        }

        private static readonly object _lock = new object();

        public static Dictionary<Polygon, (int PUCount, int DOCount)> CountPointsInGrid(List<Polygon> gridCells, List<Coordinate> puCoordinates, List<Coordinate> doCoordinates)
        {
            var results = new Dictionary<Polygon, (int PUCount, int DOCount)>();
            foreach (var cell in gridCells)
            {
                results[cell] = (0, 0);
            }

            Parallel.ForEach(puCoordinates, puPoint =>
            {
                var pointGeometry = new Point(puPoint);
                foreach (var cell in gridCells)
                {
                    if (cell.Contains(pointGeometry))
                    {
                        lock (_lock)
                        {
                            var counts = results[cell];
                            results[cell] = (counts.PUCount + 1, counts.DOCount);
                        }
                        break;
                    }
                }
            });

            Parallel.ForEach(doCoordinates, doPoint =>
            {
                var pointGeometry = new Point(doPoint);
                foreach (var cell in gridCells)
                {
                    if (cell.Contains(pointGeometry))
                    {
                        lock (_lock)
                        {
                            var counts = results[cell];
                            results[cell] = (counts.PUCount, counts.DOCount + 1);
                        }
                        break;
                    }
                }
            });

            return results;
        }
    }
}
