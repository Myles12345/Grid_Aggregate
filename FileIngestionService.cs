using System;
using System.Collections.Generic;
using System.IO;
using ProjNet.CoordinateSystems.Transformations;
using SpatialAggregation;

namespace SpatialAggregation
{
    /// <summary>
    /// Reads rideshare event CSVs and returns projected GridEvent lists ready
    /// for aggregation.
    ///
    /// Expected CSV format (header row required):
    ///
    ///   lat,lon,hour,day,event_type
    ///   40.750,-73.980,8,2024-01-15,Supply
    ///   40.720,-74.010,9,2024-01-15,Demand
    ///
    /// Columns:
    ///   lat        — WGS84 decimal latitude  [-90, 90]
    ///   lon        — WGS84 decimal longitude [-180, 180]
    ///   hour       — integer hour of day [0, 23]
    ///   day        — ISO date yyyy-MM-dd  (optional; used for multi-day datasets)
    ///   event_type — "Supply" or "Demand" (case-insensitive)
    ///
    /// Lines starting with '#' and blank lines are silently skipped.
    /// Parse errors are counted and reported but do not abort loading.
    /// </summary>
    public static class FileIngestionService
    {
        public record LoadResult(
            List<GridEvent> Events,
            int LinesRead,
            int ParseErrors,
            DateOnly? EarliestDay,
            DateOnly? LatestDay);

        /// <param name="filePath">Path to the CSV file.</param>
        /// <param name="transform">
        ///   Pre-built WGS84 → Web Mercator transformation (reuse across calls).
        /// </param>
        public static LoadResult LoadCsv(string filePath, ICoordinateTransformation transform)
        {
            var mathTransform = transform.MathTransform;
            var events       = new List<GridEvent>();
            int linesRead    = 0;
            int parseErrors  = 0;
            DateOnly? earliest = null;
            DateOnly? latest   = null;

            string[] lines = File.ReadAllLines(filePath);

            // Locate header, identify column positions by name so column
            // order is not enforced.
            int headerIndex = -1;
            int iLat = -1, iLon = -1, iHour = -1, iDay = -1, iType = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                var cols = trimmed.Split(',');
                for (int c = 0; c < cols.Length; c++)
                {
                    switch (cols[c].Trim().ToLowerInvariant())
                    {
                        case "lat":        iLat  = c; break;
                        case "lon":        iLon  = c; break;
                        case "hour":       iHour = c; break;
                        case "day":        iDay  = c; break;
                        case "event_type": iType = c; break;
                    }
                }

                if (iLat >= 0 && iLon >= 0 && iHour >= 0 && iType >= 0)
                {
                    headerIndex = i;
                    break;
                }

                Console.Error.WriteLine($"[Ingestion] Header row not found at line {i + 1} — expected columns: lat, lon, hour, day, event_type");
                return new LoadResult(events, 0, 1, null, null);
            }

            if (headerIndex < 0)
            {
                Console.Error.WriteLine("[Ingestion] No valid header row found in file.");
                return new LoadResult(events, 0, 1, null, null);
            }

            for (int i = headerIndex + 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                linesRead++;
                var parts = trimmed.Split(',');

                int maxIndex = Math.Max(iLat, Math.Max(iLon, Math.Max(iHour, iType)));
                if (iDay >= 0) maxIndex = Math.Max(maxIndex, iDay);

                if (parts.Length <= maxIndex)
                {
                    parseErrors++;
                    Console.Error.WriteLine($"[Ingestion] Line {i + 1}: too few columns — skipped.");
                    continue;
                }

                if (!double.TryParse(parts[iLat].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lat) ||
                    lat < -90 || lat > 90)
                {
                    parseErrors++;
                    Console.Error.WriteLine($"[Ingestion] Line {i + 1}: invalid lat '{parts[iLat].Trim()}' — skipped.");
                    continue;
                }

                if (!double.TryParse(parts[iLon].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lon) ||
                    lon < -180 || lon > 180)
                {
                    parseErrors++;
                    Console.Error.WriteLine($"[Ingestion] Line {i + 1}: invalid lon '{parts[iLon].Trim()}' — skipped.");
                    continue;
                }

                if (!int.TryParse(parts[iHour].Trim(), out int hour) || hour < 0 || hour > 23)
                {
                    parseErrors++;
                    Console.Error.WriteLine($"[Ingestion] Line {i + 1}: invalid hour '{parts[iHour].Trim()}' — skipped.");
                    continue;
                }

                if (!Enum.TryParse<EventType>(parts[iType].Trim(), ignoreCase: true, out EventType eventType))
                {
                    parseErrors++;
                    Console.Error.WriteLine($"[Ingestion] Line {i + 1}: unknown event_type '{parts[iType].Trim()}' (expected Supply or Demand) — skipped.");
                    continue;
                }

                // Optional day column
                if (iDay >= 0 && iDay < parts.Length &&
                    DateOnly.TryParseExact(parts[iDay].Trim(), "yyyy-MM-dd", out DateOnly day))
                {
                    if (earliest == null || day < earliest) earliest = day;
                    if (latest   == null || day > latest)   latest   = day;
                }

                // Project lon/lat → Web Mercator (lon=X, lat=Y for the transform)
                double[] proj = mathTransform.Transform(new[] { lon, lat });
                events.Add(new GridEvent(proj[0], proj[1], hour, eventType));
            }

            return new LoadResult(events, linesRead, parseErrors, earliest, latest);
        }

        // -------------------------------------------------------------------
        // Sample CSV generator — writes a skeleton file the user can fill in.
        // -------------------------------------------------------------------
        public static void WriteSampleCsv(string outputPath)
        {
            File.WriteAllText(outputPath,
                "# Grid Aggregate — rideshare event data\n" +
                "# Columns: lat, lon, hour (0-23), day (yyyy-MM-dd), event_type (Supply|Demand)\n" +
                "lat,lon,hour,day,event_type\n" +
                "40.7500,-73.9800,8,2024-01-15,Supply\n" +
                "40.7480,-73.9820,8,2024-01-15,Supply\n" +
                "40.7510,-73.9790,9,2024-01-15,Demand\n" +
                "40.7450,-73.9850,9,2024-01-15,Demand\n" +
                "40.7460,-73.9870,9,2024-01-15,Demand\n");
        }
    }
}
