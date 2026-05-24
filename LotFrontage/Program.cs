using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ParcelFrontage
{
    class Program
    {
        // -------------------------------------------------------------------
        // CONFIG
        // -------------------------------------------------------------------

        // Oakland County Parcel Service - Layer 1 = Tax Parcel Plus
        private const string ParcelService =
            "https://gisservices.oakgov.com/arcgis/rest/services/Enterprise/EnterpriseOpenParcelDataMapService/MapServer/1/query";

        // Tune concurrency and checkpoint behavior.
        private const int MaxConcurrency = 8;
        private const int CheckpointEvery = 250;

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        static async Task Main(string[] args)
        {
            string inputPath = Path.Combine(AppContext.BaseDirectory, "data", "lots.csv");
            string outputPath = Path.Combine(AppContext.BaseDirectory, "data", "lots_with_dimensions.csv");

            if (!File.Exists(inputPath))
            {
                Console.WriteLine($"Input file not found: {inputPath}");
                return;
            }

            var rows = ReadCsv(inputPath);
            if (rows.Count == 0)
            {
                Console.WriteLine("Input CSV is empty.");
                return;
            }

            var header = rows[0];
            int idIdx = header.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
            int addressIdx = header.FindIndex(h => h.Equals("Address", StringComparison.OrdinalIgnoreCase));
            int cityIdx = header.FindIndex(h => h.Equals("City", StringComparison.OrdinalIgnoreCase));
            int stateIdx = header.FindIndex(h => h.Equals("State", StringComparison.OrdinalIgnoreCase));
            int zipIdx = header.FindIndex(h => h.Equals("Zip", StringComparison.OrdinalIgnoreCase));

            if (addressIdx < 0)
            {
                Console.WriteLine("Could not find Address column in input CSV.");
                return;
            }

            // Load cache of previously-computed dimensions from existing output file.
            var cache = LoadDimensionCache(outputPath);
            if (cache.Count > 0)
            {
                Console.WriteLine($"Loaded {cache.Count} cached lot dimensions from {outputPath}");
            }

            header.Add("lot_width");
            header.Add("lot_depth");
            header.Add("propwire_link");

            int totalDataRows = rows.Count - 1;
            int cachedCount = 0;
            var toProcess = new List<int>();

            // First pass: populate cached rows + propwire links; collect remaining work.
            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];

                string id = Get(row, idIdx);
                string addr = Get(row, addressIdx);
                string city = Get(row, cityIdx);
                string state = Get(row, stateIdx);
                string zip = Get(row, zipIdx);

                string cacheKey = BuildCacheKey(id, addr, city, state, zip);

                string widthStr = "";
                string depthStr = "";

                if (cache.TryGetValue(cacheKey, out var cached) &&
                    !string.IsNullOrWhiteSpace(cached.width) &&
                    !string.IsNullOrWhiteSpace(cached.depth))
                {
                    widthStr = cached.width;
                    depthStr = cached.depth;
                    cachedCount++;
                }

                string propwireLink = BuildPropwireLink(id, addr, city, state, zip);

                // Pad row and append the three new columns (placeholder for non-cached).
                while (row.Count < header.Count - 3) row.Add("");
                row.Add(widthStr);
                row.Add(depthStr);
                row.Add(propwireLink);

                if (string.IsNullOrWhiteSpace(widthStr) || string.IsNullOrWhiteSpace(depthStr))
                {
                    toProcess.Add(i);
                }
            }

            Console.WriteLine($"Total rows: {totalDataRows}, cached: {cachedCount}, to process: {toProcess.Count}");
            Console.WriteLine($"Concurrency: {MaxConcurrency}, checkpoint every {CheckpointEvery} rows.");
            Console.WriteLine();

            int widthCol = header.Count - 3;
            int depthCol = header.Count - 2;

            int doneCount = 0;
            int errorCount = 0;
            var checkpointLock = new SemaphoreSlim(1, 1);
            int sinceCheckpoint = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            using var throttle = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

            var tasks = toProcess.Select(async rowIndex =>
            {
                await throttle.WaitAsync();
                try
                {
                    var row = rows[rowIndex];
                    string addr = Get(row, addressIdx);
                    string city = Get(row, cityIdx);
                    string state = Get(row, stateIdx);
                    string zip = Get(row, zipIdx);
                    string fullAddress = string.Join(", ",
                        new[] { addr, city, state, zip }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));

                    try
                    {
                        var (lon, lat) = await GeocodeAddress(fullAddress, locationType: null);
                        var polygon = await QueryParcel(lon, lat);
                        var (width, depth) = ComputeLotDimensions(polygon);

                        row[widthCol] = width.ToString("F1", CultureInfo.InvariantCulture);
                        row[depthCol] = depth.ToString("F1", CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errorCount);
                        // Keep cells blank for failed rows; log compactly.
                        Console.WriteLine($"  ERROR ({fullAddress}): {ex.Message}");
                    }

                    int done = Interlocked.Increment(ref doneCount);
                    int sinceCp = Interlocked.Increment(ref sinceCheckpoint);

                    if (done % 50 == 0 || done == toProcess.Count)
                    {
                        double rate = done / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                        int remaining = toProcess.Count - done;
                        TimeSpan eta = TimeSpan.FromSeconds(remaining / Math.Max(0.001, rate));
                        Console.WriteLine(
                            $"[{done}/{toProcess.Count}] " +
                            $"errors: {errorCount}, " +
                            $"rate: {rate:F1}/s, " +
                            $"eta: {eta:hh\\:mm\\:ss}");
                    }

                    if (sinceCp >= CheckpointEvery)
                    {
                        await checkpointLock.WaitAsync();
                        try
                        {
                            if (sinceCheckpoint >= CheckpointEvery)
                            {
                                Interlocked.Exchange(ref sinceCheckpoint, 0);
                                try
                                {
                                    WriteCsv(outputPath, rows);
                                    Console.WriteLine($"  -- checkpoint saved at {done}/{toProcess.Count} --");
                                }
                                catch (IOException ex)
                                {
                                    Console.WriteLine($"  -- checkpoint skipped: {ex.Message} --");
                                }
                            }
                        }
                        finally
                        {
                            checkpointLock.Release();
                        }
                    }
                }
                finally
                {
                    throttle.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            try
            {
                WriteCsv(outputPath, rows);
                Console.WriteLine();
                Console.WriteLine($"Wrote: {outputPath}");
                Console.WriteLine($"Done. Processed: {doneCount}, errors: {errorCount}, cached reused: {cachedCount}, total: {totalDataRows}");
            }
            catch (IOException ex)
            {
                Console.WriteLine();
                Console.WriteLine($"ERROR writing output: {ex.Message}");
                Console.WriteLine("(Close the file in Excel or any other app and re-run.)");
            }
        }

        static string Get(List<string> row, int idx)
        {
            if (idx < 0 || idx >= row.Count) return "";
            return row[idx] ?? "";
        }

        static string BuildCacheKey(string id, string addr, string city, string state, string zip)
        {
            if (!string.IsNullOrWhiteSpace(id))
                return "id:" + id.Trim();
            return "addr:" + string.Join("|",
                new[] { addr, city, state, zip }
                    .Select(s => (s ?? "").Trim().ToUpperInvariant()));
        }

        static Dictionary<string, (string width, string depth)> LoadDimensionCache(string outputPath)
        {
            var cache = new Dictionary<string, (string width, string depth)>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(outputPath)) return cache;

            var rows = ReadCsv(outputPath);
            if (rows.Count < 2) return cache;

            var header = rows[0];
            int idIdx = header.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
            int addressIdx = header.FindIndex(h => h.Equals("Address", StringComparison.OrdinalIgnoreCase));
            int cityIdx = header.FindIndex(h => h.Equals("City", StringComparison.OrdinalIgnoreCase));
            int stateIdx = header.FindIndex(h => h.Equals("State", StringComparison.OrdinalIgnoreCase));
            int zipIdx = header.FindIndex(h => h.Equals("Zip", StringComparison.OrdinalIgnoreCase));
            int widthIdx = header.FindIndex(h => h.Equals("lot_width", StringComparison.OrdinalIgnoreCase));
            int depthIdx = header.FindIndex(h => h.Equals("lot_depth", StringComparison.OrdinalIgnoreCase));

            if (widthIdx < 0 || depthIdx < 0) return cache;

            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                string key = BuildCacheKey(
                    Get(row, idIdx),
                    Get(row, addressIdx),
                    Get(row, cityIdx),
                    Get(row, stateIdx),
                    Get(row, zipIdx));

                cache[key] = (Get(row, widthIdx), Get(row, depthIdx));
            }

            return cache;
        }

        static string BuildPropwireLink(string id, string addr, string city, string state, string zip)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";

            var slugParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(addr)) slugParts.Add(SlugifyForPropwire(addr));
            if (!string.IsNullOrWhiteSpace(city)) slugParts.Add(SlugifyForPropwire(city));
            if (!string.IsNullOrWhiteSpace(state)) slugParts.Add(state.Trim().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(zip)) slugParts.Add(zip.Trim());

            string slug = string.Join("-", slugParts.Where(s => !string.IsNullOrEmpty(s)));
            if (string.IsNullOrEmpty(slug)) return "";
            return $"https://propwire.com/realestate/{slug}/{id.Trim()}/property-details";
        }

        static string SlugifyForPropwire(string s)
        {
            // Splits on non-alphanumeric runs, title-cases each token (so "ST" -> "St"),
            // and joins with hyphens. State codes stay 2-letter (still title-cased: "MI" -> "Mi"),
            // but the Propwire URL accepts that form.
            var tokens = new List<string>();
            var current = new StringBuilder();

            foreach (char c in s.Trim())
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(c);
                }
                else
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                }
            }
            if (current.Length > 0) tokens.Add(current.ToString());

            return string.Join("-", tokens.Select(TitleCaseToken));
        }

        static string TitleCaseToken(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            if (t.All(char.IsDigit)) return t;
            return char.ToUpperInvariant(t[0]) + t.Substring(1).ToLowerInvariant();
        }

        // -------------------------------------------------------------------
        // GEOCODING
        // -------------------------------------------------------------------

        static async Task<(double lon, double lat)> GeocodeAddress(string address, string? locationType = null)
        {
            string url =
                "https://geocode.arcgis.com/arcgis/rest/services/" +
                "World/GeocodeServer/findAddressCandidates" +
                $"?SingleLine={Uri.EscapeDataString(address)}" +
                "&f=json&maxLocations=1" +
                (locationType != null ? $"&locationType={locationType}" : "");

            string json = await Http.GetStringAsync(url);

            using JsonDocument doc = JsonDocument.Parse(json);

            var candidates = doc.RootElement.GetProperty("candidates");

            if (candidates.GetArrayLength() == 0)
                throw new Exception("No geocode result");

            var location = candidates[0]
                .GetProperty("location");

            double lon = location.GetProperty("x").GetDouble();
            double lat = location.GetProperty("y").GetDouble();

            return (lon, lat);
        }

        // -------------------------------------------------------------------
        // PARCEL QUERY
        // -------------------------------------------------------------------

        static async Task<List<Point>> QueryParcel(double lon, double lat)
        {
            string geometry =
                $"{lon.ToString(CultureInfo.InvariantCulture)}," +
                $"{lat.ToString(CultureInfo.InvariantCulture)}";

            string url =
                $"{ParcelService}" +
                $"?geometry={geometry}" +
                "&geometryType=esriGeometryPoint" +
                "&inSR=4326" +
                "&spatialRel=esriSpatialRelIntersects" +
                "&returnGeometry=true" +
                "&f=geojson" +
                "&outFields=*";

            string json = await Http.GetStringAsync(url);

            using JsonDocument doc = JsonDocument.Parse(json);

            var features = doc.RootElement.GetProperty("features");

            if (features.GetArrayLength() == 0)
                throw new Exception("No parcel found");

            var geometryElement = features[0]
                .GetProperty("geometry");

            var coordinates = geometryElement
                .GetProperty("coordinates")[0];

            var points = new List<Point>();

            foreach (var coord in coordinates.EnumerateArray())
            {
                double x = coord[0].GetDouble();
                double y = coord[1].GetDouble();

                points.Add(new Point { X = x, Y = y });
            }

            return points;
        }

        // -------------------------------------------------------------------
        // FRONTAGE CALCULATION
        // -------------------------------------------------------------------

        static (double width, double depth) ComputeLotDimensions(List<Point> polygon)
        {
            if (polygon.Count < 3)
                throw new Exception("Invalid polygon");

            // Convert lat/lon ring to local planar coordinates in feet.
            // Use the polygon's average latitude as the reference for the
            // longitude-to-feet scaling so the result is approximately
            // isotropic over the small extent of a single parcel.
            const double feetPerDegreeLat = 364000.0;

            double avgLat = polygon.Average(p => p.Y);
            double feetPerDegreeLon = Math.Cos(avgLat * Math.PI / 180.0) * feetPerDegreeLat;

            double refX = polygon[0].X;
            double refY = polygon[0].Y;

            var planar = polygon
                .Select(p => new Point
                {
                    X = (p.X - refX) * feetPerDegreeLon,
                    Y = (p.Y - refY) * feetPerDegreeLat
                })
                .ToList();

            // Drop duplicate closing point if present.
            if (planar.Count > 1 &&
                Math.Abs(planar[0].X - planar[^1].X) < 1e-6 &&
                Math.Abs(planar[0].Y - planar[^1].Y) < 1e-6)
            {
                planar.RemoveAt(planar.Count - 1);
            }

            var hull = ConvexHull(planar);
            if (hull.Count < 2)
                throw new Exception("Degenerate polygon");

            // Rotating-calipers style search: for each hull edge, rotate so
            // that edge is axis-aligned, then take the axis-aligned bounding
            // box. The minimum-area rectangle is always aligned with one of
            // the hull edges.
            double bestArea = double.MaxValue;
            double bestShortSide = 0;
            double bestLongSide = 0;

            for (int i = 0; i < hull.Count; i++)
            {
                Point a = hull[i];
                Point b = hull[(i + 1) % hull.Count];

                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;

                double ux = dx / len;
                double uy = dy / len;

                double minU = double.MaxValue, maxU = double.MinValue;
                double minV = double.MaxValue, maxV = double.MinValue;

                foreach (var p in hull)
                {
                    double u = p.X * ux + p.Y * uy;
                    double v = -p.X * uy + p.Y * ux;
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }

                double width = maxU - minU;
                double height = maxV - minV;
                double area = width * height;

                if (area < bestArea)
                {
                    bestArea = area;
                    bestShortSide = Math.Min(width, height);
                    bestLongSide = Math.Max(width, height);
                }
            }

            return (bestShortSide, bestLongSide);
        }

        // Andrew's monotone chain convex hull. Returns points in CCW order.
        static List<Point> ConvexHull(List<Point> points)
        {
            var pts = points
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            if (pts.Count <= 1) return pts;

            var lower = new List<Point>();
            foreach (var p in pts)
            {
                while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<Point>();
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                var p = pts[i];
                while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        static double Cross(Point o, Point a, Point b)
        {
            return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        }

        // -------------------------------------------------------------------
        // DISTANCE
        // -------------------------------------------------------------------

        static double DistanceFeet(Point p1, Point p2)
        {
            // Approx conversion for small distances

            const double feetPerDegreeLat = 364000.0;

            double avgLat =
                (p1.Y + p2.Y) / 2.0 *
                Math.PI / 180.0;

            double feetPerDegreeLon =
                Math.Cos(avgLat) * feetPerDegreeLat;

            double dx =
                (p2.X - p1.X) * feetPerDegreeLon;

            double dy =
                (p2.Y - p1.Y) * feetPerDegreeLat;

            return Math.Sqrt(dx * dx + dy * dy);
        }

        // -------------------------------------------------------------------
        // CSV I/O
        // -------------------------------------------------------------------

        static List<List<string>> ReadCsv(string path)
        {
            var rows = new List<List<string>>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();

            var row = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ',')
                    {
                        row.Add(field.ToString());
                        field.Clear();
                    }
                    else if (c == '\r')
                    {
                        // ignore; \n will terminate the line
                    }
                    else if (c == '\n')
                    {
                        row.Add(field.ToString());
                        field.Clear();
                        rows.Add(row);
                        row = new List<string>();
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }

            return rows;
        }

        static void WriteCsv(string path, List<List<string>> rows)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new StreamWriter(path);
            foreach (var row in rows)
            {
                writer.WriteLine(string.Join(",", row.Select(EscapeCsv)));
            }
        }

        static string EscapeCsv(string? value)
        {
            value ??= "";
            bool needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
            if (!needsQuotes) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }

    // -------------------------------------------------------------------
    // TYPES
    // -------------------------------------------------------------------

    class Point
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    class Segment
    {
        public double LengthFeet { get; set; }
        public double MidY { get; set; }
    }
}
