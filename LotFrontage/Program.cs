using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

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
            if (args.Length > 0 && args[0].Equals("map", StringComparison.OrdinalIgnoreCase))
            {
                double minWidth = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 50;
                double maxWidth = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 80;
                double maxValue = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : double.PositiveInfinity;
                await MapMain(minWidth, maxWidth, maxValue);
                return;
            }

            if (args.Length > 0 && args[0].Equals("redfin", StringComparison.OrdinalIgnoreCase))
            {
                int? limit = null;
                if (args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    limit = n;
                }
                await RedfinMain(limit);
                return;
            }

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

        // -------------------------------------------------------------------
        // MAP MODE
        // -------------------------------------------------------------------

        static async Task MapMain(double minWidth, double maxWidth, double maxValue = double.PositiveInfinity)
        {
            string outputCsv = Path.Combine(AppContext.BaseDirectory, "data", "lots_with_dimensions.csv");
            string sourceCsv = Path.Combine(AppContext.BaseDirectory, "data", "lots.csv");
            string geocodeCsv = Path.Combine(AppContext.BaseDirectory, "data", "geocodes.csv");
            string redfinCsv = Path.Combine(AppContext.BaseDirectory, "data", "redfin_values.csv");
            string valueSuffix = double.IsPositiveInfinity(maxValue) ? "" : $"_lt{maxValue:F0}";
            string mapHtml = Path.Combine(AppContext.BaseDirectory, "data", $"map_{minWidth:F0}_{maxWidth:F0}{valueSuffix}.html");

            if (!File.Exists(outputCsv))
            {
                Console.WriteLine($"Output CSV not found: {outputCsv}");
                Console.WriteLine("Run the program in default mode first to produce lots_with_dimensions.csv.");
                return;
            }

            // Build Id -> Market Value lookup from the original CSV.
            var marketValueById = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(sourceCsv))
            {
                var srcRows = ReadCsv(sourceCsv);
                if (srcRows.Count >= 2)
                {
                    var srcHeader = srcRows[0];
                    int srcIdIdx = srcHeader.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
                    int mvIdx = srcHeader.FindIndex(h => h.Equals("Estimated Value", StringComparison.OrdinalIgnoreCase));
                    if (srcIdIdx >= 0 && mvIdx >= 0)
                    {
                        for (int i = 1; i < srcRows.Count; i++)
                        {
                            var r = srcRows[i];
                            string id = Get(r, srcIdIdx);
                            if (string.IsNullOrWhiteSpace(id)) continue;
                            if (double.TryParse(Get(r, mvIdx), NumberStyles.Float, CultureInfo.InvariantCulture, out double mv) && mv > 0)
                            {
                                marketValueById[id] = mv;
                            }
                        }
                    }
                }
                Console.WriteLine($"Market value entries: {marketValueById.Count}");
            }

            // Build Id -> Redfin Estimate lookup.
            var redfinValueById = new Dictionary<string, (string url, double value)>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(redfinCsv))
            {
                var rfCache = LoadRedfinCache(redfinCsv);
                foreach (var kv in rfCache)
                {
                    if (kv.Value.status == "ok" && kv.Value.value > 0)
                    {
                        redfinValueById[kv.Key] = (kv.Value.url, kv.Value.value);
                    }
                }
                Console.WriteLine($"Redfin estimate entries: {redfinValueById.Count}");
            }

            var rows = ReadCsv(outputCsv);
            if (rows.Count < 2)
            {
                Console.WriteLine("Output CSV is empty.");
                return;
            }

            var header = rows[0];
            int idIdx = header.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
            int addressIdx = header.FindIndex(h => h.Equals("Address", StringComparison.OrdinalIgnoreCase));
            int cityIdx = header.FindIndex(h => h.Equals("City", StringComparison.OrdinalIgnoreCase));
            int stateIdx = header.FindIndex(h => h.Equals("State", StringComparison.OrdinalIgnoreCase));
            int zipIdx = header.FindIndex(h => h.Equals("Zip", StringComparison.OrdinalIgnoreCase));
            int widthIdx = header.FindIndex(h => h.Equals("lot_width", StringComparison.OrdinalIgnoreCase));
            int depthIdx = header.FindIndex(h => h.Equals("lot_depth", StringComparison.OrdinalIgnoreCase));
            int propwireIdx = header.FindIndex(h => h.Equals("propwire_link", StringComparison.OrdinalIgnoreCase));

            if (widthIdx < 0 || addressIdx < 0)
            {
                Console.WriteLine("Required columns missing.");
                return;
            }

            // Filter rows by lot_width range.
            var matches = new List<(string id, string addr, string city, string state, string zip, double width, double depth, string propwire, double marketValue, string redfinUrl, double redfinValue)>();
            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                string widthStr = Get(row, widthIdx);
                if (!double.TryParse(widthStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double width)) continue;
                if (width < minWidth || width > maxWidth) continue;

                double.TryParse(Get(row, depthIdx), NumberStyles.Float, CultureInfo.InvariantCulture, out double depth);

                string id = Get(row, idIdx);
                marketValueById.TryGetValue(id, out double mv);

                if (!double.IsPositiveInfinity(maxValue))
                {
                    if (mv <= 0 || mv >= maxValue) continue;
                }

                redfinValueById.TryGetValue(id, out var rf);

                matches.Add((
                    id,
                    Get(row, addressIdx),
                    Get(row, cityIdx),
                    Get(row, stateIdx),
                    Get(row, zipIdx),
                    width,
                    depth,
                    Get(row, propwireIdx),
                    mv,
                    rf.url ?? "",
                    rf.value
                ));
            }

            Console.WriteLine($"Lots with lot_width in [{minWidth}, {maxWidth}]: {matches.Count}");

            // Load existing geocode cache (id,lat,lon).
            var geocodeCache = LoadGeocodeCache(geocodeCsv);
            Console.WriteLine($"Geocode cache entries: {geocodeCache.Count}");

            // Determine which matches need geocoding.
            var toGeocode = matches.Where(m => !geocodeCache.ContainsKey(m.id)).ToList();
            Console.WriteLine($"To geocode: {toGeocode.Count}");

            if (toGeocode.Count > 0)
            {
                using var throttle = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
                int done = 0;
                int errors = 0;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var lockObj = new object();
                int sinceSave = 0;

                var tasks = toGeocode.Select(async m =>
                {
                    await throttle.WaitAsync();
                    try
                    {
                        string fullAddress = string.Join(", ",
                            new[] { m.addr, m.city, m.state, m.zip }
                                .Where(s => !string.IsNullOrWhiteSpace(s)));

                        try
                        {
                            var (lon, lat) = await GeocodeAddress(fullAddress, locationType: null);
                            lock (lockObj)
                            {
                                geocodeCache[m.id] = (lat, lon);
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref errors);
                            Console.WriteLine($"  ERROR ({fullAddress}): {ex.Message}");
                        }

                        int d = Interlocked.Increment(ref done);
                        int s = Interlocked.Increment(ref sinceSave);

                        if (d % 50 == 0 || d == toGeocode.Count)
                        {
                            double rate = d / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                            int remaining = toGeocode.Count - d;
                            TimeSpan eta = TimeSpan.FromSeconds(remaining / Math.Max(0.001, rate));
                            Console.WriteLine($"[{d}/{toGeocode.Count}] errors: {errors}, rate: {rate:F1}/s, eta: {eta:hh\\:mm\\:ss}");
                        }

                        if (s >= 250)
                        {
                            lock (lockObj)
                            {
                                if (sinceSave >= 250)
                                {
                                    sinceSave = 0;
                                    try { SaveGeocodeCache(geocodeCsv, geocodeCache); }
                                    catch (IOException) { /* skip checkpoint if locked */ }
                                }
                            }
                        }
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
                SaveGeocodeCache(geocodeCsv, geocodeCache);
                Console.WriteLine($"Geocoded {done} addresses ({errors} errors).");
            }

            // Build the HTML map.
            var markers = matches
                .Where(m => geocodeCache.ContainsKey(m.id))
                .Select(m =>
                {
                    var (lat, lon) = geocodeCache[m.id];
                    return new
                    {
                        m.id,
                        m.addr,
                        m.city,
                        m.state,
                        m.zip,
                        m.width,
                        m.depth,
                        m.propwire,
                        m.marketValue,
                        m.redfinUrl,
                        m.redfinValue,
                        lat,
                        lon
                    };
                })
                .ToList();

            if (markers.Count == 0)
            {
                Console.WriteLine("No geocoded markers to plot.");
                return;
            }

            double centerLat = markers.Average(m => m.lat);
            double centerLon = markers.Average(m => m.lon);

            var markersJson = JsonSerializer.Serialize(markers);

            string html = $$"""
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8" />
    <title>Lots {{minWidth}}–{{maxWidth}} ft frontage</title>
    <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
    <style>
        html, body { margin: 0; height: 100%; }
        #map { height: 100%; }
        .leaflet-popup-content { font-family: sans-serif; font-size: 13px; }
        .leaflet-popup-content a { color: #1a73e8; text-decoration: none; }
        .price-label {
            background: rgba(255,255,255,0.92);
            border: 1px solid #1a73e8;
            border-radius: 4px;
            padding: 1px 4px;
            font-family: sans-serif;
            font-size: 11px;
            font-weight: 600;
            color: #1a73e8;
            white-space: nowrap;
            box-shadow: 0 1px 2px rgba(0,0,0,0.15);
        }
    </style>
</head>
<body>
    <div id="map"></div>
    <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
    <script>
        const markers = {{markersJson}};
        const map = L.map('map').setView([{{centerLat.ToString("G", CultureInfo.InvariantCulture)}}, {{centerLon.ToString("G", CultureInfo.InvariantCulture)}}], 14);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '© OpenStreetMap contributors'
        }).addTo(map);

        function fmtMoney(v) {
            if (!v || v <= 0) return '';
            if (v >= 1_000_000) return '$' + (v / 1_000_000).toFixed(2).replace(/\.?0+$/, '') + 'M';
            if (v >= 1_000) return '$' + Math.round(v / 1000) + 'K';
            return '$' + Math.round(v);
        }
        function fmtMoneyFull(v) {
            if (!v || v <= 0) return 'n/a';
            return '$' + Math.round(v).toLocaleString('en-US');
        }
        function zillowLink(m) {
            const slug = [m.addr, m.city, m.state, m.zip]
                .filter(x => x && String(x).trim().length > 0)
                .join(' ')
                .trim()
                .replace(/\s+/g, '-');
            return `https://www.zillow.com/homes/${encodeURI(slug)}_rb/`;
        }

        const bounds = [];
        for (const m of markers) {
            const displayValue = (m.redfinValue && m.redfinValue > 0) ? m.redfinValue : m.marketValue;
            const priceShort = fmtMoney(displayValue);
            const zUrl = zillowLink(m);
            const popup =
                `<b>${m.addr}</b><br/>` +
                `${m.city}, ${m.state} ${m.zip}<br/>` +
                `Width: ${m.width.toFixed(1)} ft, Depth: ${m.depth.toFixed(1)} ft<br/>` +
                `Redfin Estimate: ${fmtMoneyFull(m.redfinValue)}<br/>` +
                `Estimated Value: ${fmtMoneyFull(m.marketValue)}<br/>` +
                (m.propwire ? `<a href="${m.propwire}" target="_blank">Propwire</a>` : '') +
                (m.propwire ? ' &middot; ' : '') +
                `<a href="${zUrl}" target="_blank">Zillow</a>` +
                (m.redfinUrl ? ` &middot; <a href="${m.redfinUrl}" target="_blank">Redfin</a>` : '');
            const marker = L.marker([m.lat, m.lon]).addTo(map).bindPopup(popup);
            if (priceShort) {
                marker.bindTooltip(priceShort, {
                    permanent: true,
                    direction: 'right',
                    offset: [8, 0],
                    className: 'price-label'
                });
            }
            bounds.push([m.lat, m.lon]);
        }
        if (bounds.length > 1) map.fitBounds(bounds, { padding: [20, 20] });
    </script>
</body>
</html>
""";

            Directory.CreateDirectory(Path.GetDirectoryName(mapHtml)!);
            File.WriteAllText(mapHtml, html);
            Console.WriteLine();
            Console.WriteLine($"Wrote: {mapHtml}");
            Console.WriteLine($"Markers: {markers.Count}");
        }

        // -------------------------------------------------------------------
        // REDFIN ESTIMATE SCRAPER
        // -------------------------------------------------------------------
        //
        // Two-stage flow:
        //   1) Use Playwright (Chromium) to resolve "Address, City, State Zip"
        //      via Redfin's homepage search, which JS-redirects to the
        //      property URL containing the home id.
        //   2) Fetch that property page over plain HttpClient and parse the
        //      Redfin Estimate value from the embedded bootstrap JSON.
        //
        // Results are persisted to data\redfin_values.csv with columns:
        //   Id,redfin_url,redfin_value,status,fetched_at
        // Statuses: ok | not_found | no_estimate | error
        // On re-run, rows with status=ok|not_found are skipped; error and
        // no_estimate rows are retried (the page may have changed).
        //
        // Concurrency is intentionally low (2) and we pace requests to be
        // polite. Checkpoints every 25 rows make long runs restartable.

        private const int RedfinConcurrency = 2;
        private const int RedfinCheckpointEvery = 25;

        private static readonly Regex RedfinValueRegex = new(
            "\\\\?\"predictedValue\\\\?\"\\s*:\\s*(\\d+(?:\\.\\d+)?)",
            RegexOptions.Compiled);

        private static readonly Regex RedfinFallbackRegex = new(
            "Redfin\\s+Estimate[^$]{0,200}\\$([0-9,]+)",
            RegexOptions.Compiled);

        private static readonly Regex RedfinHomeUrlRegex = new(
            "https?://www\\.redfin\\.com/[A-Z]{2}/[^/\"\\s]+/[^/\"\\s]+/home/\\d+",
            RegexOptions.Compiled);

        static async Task RedfinMain(int? limit)
        {
            string dimsCsv = Path.Combine(AppContext.BaseDirectory, "data", "lots_with_dimensions.csv");
            string redfinCsv = Path.Combine(AppContext.BaseDirectory, "data", "redfin_values.csv");

            if (!File.Exists(dimsCsv))
            {
                Console.WriteLine($"Not found: {dimsCsv}");
                Console.WriteLine("Run default mode first to produce lots_with_dimensions.csv.");
                return;
            }

            var rows = ReadCsv(dimsCsv);
            if (rows.Count < 2)
            {
                Console.WriteLine("Empty dimensions CSV.");
                return;
            }

            var header = rows[0];
            int idIdx = header.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
            int addressIdx = header.FindIndex(h => h.Equals("Address", StringComparison.OrdinalIgnoreCase));
            int cityIdx = header.FindIndex(h => h.Equals("City", StringComparison.OrdinalIgnoreCase));
            int stateIdx = header.FindIndex(h => h.Equals("State", StringComparison.OrdinalIgnoreCase));
            int zipIdx = header.FindIndex(h => h.Equals("Zip", StringComparison.OrdinalIgnoreCase));
            if (idIdx < 0 || addressIdx < 0)
            {
                Console.WriteLine("Required columns Id/Address missing.");
                return;
            }

            var cache = LoadRedfinCache(redfinCsv);
            Console.WriteLine($"Redfin cache entries: {cache.Count}");

            // Rows that still need work: missing entry OR entry with status error/no_estimate (retry).
            var todo = new List<(string id, string addr, string city, string state, string zip)>();
            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                string id = Get(row, idIdx);
                string addr = Get(row, addressIdx);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(addr)) continue;

                if (cache.TryGetValue(id, out var existing) &&
                    (existing.status == "ok" || existing.status == "not_found"))
                {
                    continue;
                }

                todo.Add((id, addr, Get(row, cityIdx), Get(row, stateIdx), Get(row, zipIdx)));
            }

            if (limit.HasValue) todo = todo.Take(limit.Value).ToList();
            Console.WriteLine($"To fetch: {todo.Count}");
            if (todo.Count == 0)
            {
                Console.WriteLine("Nothing to do.");
                return;
            }

            using var playwright = await Playwright.CreateAsync();
            bool headless = Environment.GetEnvironmentVariable("REDFIN_HEADLESS") != "0";
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--disable-features=IsolateOrigins,site-per-process",
                }
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                Locale = "en-US",
            });

            // Hide webdriver flag, common bot-detection vector.
            await context.AddInitScriptAsync(@"
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                window.chrome = { runtime: {} };
                Object.defineProperty(navigator, 'plugins', { get: () => [1,2,3,4,5] });
                Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
            ");

            int done = 0, ok = 0, notFound = 0, noEstimate = 0, errors = 0;
            int sinceSave = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lockObj = new object();

            using var throttle = new SemaphoreSlim(RedfinConcurrency, RedfinConcurrency);

            var tasks = todo.Select(async lot =>
            {
                await throttle.WaitAsync();
                try
                {
                    var result = await FetchRedfinEstimateAsync(context, lot.addr, lot.city, lot.state, lot.zip);

                    lock (lockObj)
                    {
                        cache[lot.id] = (result.url, result.value, result.status, DateTime.UtcNow);
                        switch (result.status)
                        {
                            case "ok": ok++; break;
                            case "not_found": notFound++; break;
                            case "no_estimate": noEstimate++; break;
                            default: errors++; break;
                        }
                    }

                    int d = Interlocked.Increment(ref done);
                    int s = Interlocked.Increment(ref sinceSave);

                    if (d % 10 == 0 || d == todo.Count)
                    {
                        double rate = d / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        int remaining = todo.Count - d;
                        TimeSpan eta = TimeSpan.FromSeconds(remaining / Math.Max(0.001, rate));
                        Console.WriteLine($"[{d}/{todo.Count}] ok={ok} notFound={notFound} noEst={noEstimate} err={errors} rate={rate:F2}/s eta={eta:hh\\:mm\\:ss}");
                    }

                    if (s >= RedfinCheckpointEvery)
                    {
                        lock (lockObj)
                        {
                            if (sinceSave >= RedfinCheckpointEvery)
                            {
                                sinceSave = 0;
                                try { SaveRedfinCache(redfinCsv, cache); }
                                catch (IOException) { /* skip if locked */ }
                            }
                        }
                    }
                }
                finally
                {
                    throttle.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            SaveRedfinCache(redfinCsv, cache);

            Console.WriteLine();
            Console.WriteLine($"Done. ok={ok} not_found={notFound} no_estimate={noEstimate} errors={errors}");
            Console.WriteLine($"Wrote: {redfinCsv}");
        }

        static async Task<(string url, double value, string status)> FetchRedfinEstimateAsync(
            IBrowserContext context, string addr, string city, string state, string zip)
        {
            string fullAddress = string.Join(", ",
                new[] { addr, city, state, zip }.Where(s => !string.IsNullOrWhiteSpace(s)));

            string? finalUrl = null;
            string pageHtml = "";
            var page = await context.NewPageAsync();
            try
            {
                try
                {
                    await page.GotoAsync("https://www.redfin.com/", new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 25000
                    });
                }
                catch (Exception ex) when (ex is TimeoutException || ex is PlaywrightException)
                {
                    return ("", 0, "error");
                }

                try
                {
                    // Locate the homepage search box and type the address.
                    var searchBox = page.Locator("input[name='searchInputBox'], input[data-rf-test-name='search-box-input'], input[placeholder*='Address'], input[type='search']").First;
                    await searchBox.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
                    await searchBox.ClickAsync();
                    await searchBox.FillAsync(fullAddress);
                    await Task.Delay(1500);

                    // Wait for the autocomplete dropdown to appear and click the first row.
                    var firstSuggestion = page.Locator("[id^='search-box-results-item-'], .search-result-row, .item-row").First;
                    try
                    {
                        await firstSuggestion.WaitForAsync(new LocatorWaitForOptions { Timeout = 6000 });
                        await firstSuggestion.ClickAsync();
                    }
                    catch (TimeoutException)
                    {
                        // Fall back to pressing Enter.
                        await searchBox.PressAsync("Enter");
                    }

                    try
                    {
                        await page.WaitForURLAsync(new Regex(@"/home/\d+"), new PageWaitForURLOptions { Timeout = 15000 });
                        finalUrl = page.Url;
                    }
                    catch (TimeoutException)
                    {
                        return ("", 0, "not_found");
                    }
                }
                catch (Exception ex) when (ex is TimeoutException || ex is PlaywrightException)
                {
                    return ("", 0, "not_found");
                }

                if (string.IsNullOrEmpty(finalUrl)) return ("", 0, "not_found");

                if (!string.IsNullOrWhiteSpace(zip) && !finalUrl.Contains(zip.Trim()))
                {
                    return (finalUrl, 0, "not_found");
                }

                // Strip query/fragment for the HttpClient fetch.
                int qIdx = finalUrl.IndexOfAny(new[] { '?', '#' });
                string cleanUrl = qIdx > 0 ? finalUrl.Substring(0, qIdx) : finalUrl;

                using var req = new HttpRequestMessage(HttpMethod.Get, cleanUrl);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                req.Headers.Add("Upgrade-Insecure-Requests", "1");

                try
                {
                    using var httpResp = await Http.SendAsync(req);
                    if (!httpResp.IsSuccessStatusCode)
                    {
                        return (cleanUrl, 0, "error");
                    }
                    pageHtml = await httpResp.Content.ReadAsStringAsync();
                }
                catch (Exception)
                {
                    return (cleanUrl, 0, "error");
                }

                finalUrl = cleanUrl;
            }
            finally
            {
                await page.CloseAsync();
            }

            // Parse "predictedValue":<number> — the most stable embedded JSON token.
            var match = RedfinValueRegex.Match(pageHtml);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
                v > 0)
            {
                return (finalUrl, v, "ok");
            }

            // Fallback: "Redfin Estimate ... $1,234,567" in visible text.
            var fallback = RedfinFallbackRegex.Match(pageHtml);
            if (fallback.Success &&
                double.TryParse(fallback.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double fv) &&
                fv > 0)
            {
                return (finalUrl, fv, "ok");
            }

            return (finalUrl, 0, "no_estimate");
        }

        static Dictionary<string, (string url, double value, string status, DateTime fetchedAt)> LoadRedfinCache(string path)
        {
            var cache = new Dictionary<string, (string url, double value, string status, DateTime fetchedAt)>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return cache;

            var rows = ReadCsv(path);
            if (rows.Count < 2) return cache;

            var header = rows[0];
            int idIdx = header.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
            int urlIdx = header.FindIndex(h => h.Equals("redfin_url", StringComparison.OrdinalIgnoreCase));
            int valIdx = header.FindIndex(h => h.Equals("redfin_value", StringComparison.OrdinalIgnoreCase));
            int statusIdx = header.FindIndex(h => h.Equals("status", StringComparison.OrdinalIgnoreCase));
            int tsIdx = header.FindIndex(h => h.Equals("fetched_at", StringComparison.OrdinalIgnoreCase));
            if (idIdx < 0) return cache;

            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                string id = Get(row, idIdx);
                if (string.IsNullOrWhiteSpace(id)) continue;
                string url = Get(row, urlIdx);
                double.TryParse(Get(row, valIdx), NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
                string status = Get(row, statusIdx);
                DateTime.TryParse(Get(row, tsIdx), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime ts);
                cache[id] = (url, v, status, ts);
            }
            return cache;
        }

        static void SaveRedfinCache(string path, Dictionary<string, (string url, double value, string status, DateTime fetchedAt)> cache)
        {
            var rows = new List<List<string>>
            {
                new List<string> { "Id", "redfin_url", "redfin_value", "status", "fetched_at" }
            };
            foreach (var kv in cache.OrderBy(k => k.Key))
            {
                rows.Add(new List<string>
                {
                    kv.Key,
                    kv.Value.url ?? "",
                    kv.Value.value > 0 ? kv.Value.value.ToString("F0", CultureInfo.InvariantCulture) : "",
                    kv.Value.status ?? "",
                    kv.Value.fetchedAt == default ? "" : kv.Value.fetchedAt.ToString("O", CultureInfo.InvariantCulture)
                });
            }
            WriteCsv(path, rows);
        }

        static Dictionary<string, (double lat, double lon)> LoadGeocodeCache(string path)
        {
            var cache = new Dictionary<string, (double lat, double lon)>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return cache;

            var rows = ReadCsv(path);
            if (rows.Count < 2) return cache;

            var header = rows[0];
            int idIdx = header.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
            int latIdx = header.FindIndex(h => h.Equals("lat", StringComparison.OrdinalIgnoreCase));
            int lonIdx = header.FindIndex(h => h.Equals("lon", StringComparison.OrdinalIgnoreCase));
            if (idIdx < 0 || latIdx < 0 || lonIdx < 0) return cache;

            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                string id = Get(row, idIdx);
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!double.TryParse(Get(row, latIdx), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                if (!double.TryParse(Get(row, lonIdx), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                cache[id] = (lat, lon);
            }
            return cache;
        }

        static void SaveGeocodeCache(string path, Dictionary<string, (double lat, double lon)> cache)
        {
            var rows = new List<List<string>>
            {
                new List<string> { "Id", "lat", "lon" }
            };
            foreach (var kv in cache.OrderBy(k => k.Key))
            {
                rows.Add(new List<string>
                {
                    kv.Key,
                    kv.Value.lat.ToString("G", CultureInfo.InvariantCulture),
                    kv.Value.lon.ToString("G", CultureInfo.InvariantCulture)
                });
            }
            WriteCsv(path, rows);
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
