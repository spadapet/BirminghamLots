# LotFrontage — Agent Context

This file is the authoritative project briefing for AI coding agents (Copilot, etc.) and human collaborators returning to this repo. Read it before making changes, and append a short note under **Session Log** at the end of each working session.

---

## Project Purpose

Analyze residential lots in Birmingham, Michigan to identify properties suitable for redevelopment or splitting, based on lot **frontage (width)**, depth, and estimated value. Outputs:

1. An enriched CSV of every lot with computed dimensions and a Propwire link.
2. Interactive HTML maps (Leaflet + OpenStreetMap) of lots matching width / value filters, with permanent price labels and per-property popups that link to Propwire and Zillow.

The tool is a single .NET 10 console app (`LotFrontage/Program.cs`).

---

## Repo Layout

```
LotFrontage.slnx
LotFrontage/
  Program.cs                       <- all logic
  LotFrontage.csproj               <- net10.0; copies data\*.csv to output
  data/
	lots.csv                       <- INPUT: Birmingham lots (~7,352 rows)
	lots_with_dimensions.csv       <- CACHED OUTPUT: enriched lot data
	geocodes.csv                   <- CACHED OUTPUT: Id -> (lat, lon)
	redfin_values.csv              <- CACHED OUTPUT: Id -> Redfin Estimate
AGENTS.md                          <- this file
```

The three `*.csv` cache files in `LotFrontage/data/` are committed so the project is **resumable on a fresh clone without re-querying parcel/geocoding/Redfin services**. They are also marked `CopyToOutputDirectory=PreserveNewest` so they appear next to the binary at runtime.

At runtime, the program reads/writes from `AppContext.BaseDirectory\data\`, which is `bin\<Config>\net10.0\data\`. The source `data\` folder is the authoritative copy committed to git; the bin copy is a working copy.

---

## How to Run

```powershell
# Default mode: enrich data\lots.csv -> data\lots_with_dimensions.csv
dotnet run --project LotFrontage -c Release

# Map mode: emit data\map_<minWidth>_<maxWidth>[_lt<maxValue>].html
dotnet run --project LotFrontage -c Release -- map <minWidth> <maxWidth> [maxValue]

# Redfin scrape mode: populate data\redfin_values.csv with Redfin Estimate per lot.
#   Optional <limit> caps how many rows to process this run (handy for smoke tests).
#   First run downloads Chromium via Playwright. Set REDFIN_HEADLESS=0 to watch it.
dotnet run --project LotFrontage -c Release -- redfin [limit]

# Examples used in this project:
dotnet run --project LotFrontage -c Release -- map 50 80            # original 50-80 ft band
dotnet run --project LotFrontage -c Release -- map 58 68            # ~60 ft fuzzy
dotnet run --project LotFrontage -c Release -- map 68 78            # ~70 ft fuzzy
dotnet run --project LotFrontage -c Release -- map 78 88            # ~80 ft fuzzy
dotnet run --project LotFrontage -c Release -- map 55 10000 850000  # width >= 55 ft AND est. value < $850k
```

`maxValue` is optional; when supplied, lots with no/zero Estimated Value are excluded.

---

## Data Flow

1. **`data\lots.csv`** — Birmingham lots, raw export. Key columns the app uses: `Id`, `Address`, `City`, `State`, `Zip`, `Estimated Value`.
2. **Default mode** — for each row:
   - Geocode address (ArcGIS World Geocoder).
   - Query Oakland County parcel service to get the lot polygon.
   - Compute lot dimensions via **minimum-area oriented bounding rectangle** of the parcel polygon (convex hull + rotating-calipers-style search). The shorter side is `lot_width` (frontage), the longer is `lot_depth`.
   - Build a Propwire URL: `https://propwire.com/realestate/<TitleCased-Addr-City-STATE-ZIP>/<Id>/property-details`.
   - Write `lot_width`, `lot_depth`, `propwire_link` columns to `data\lots_with_dimensions.csv`.
   - Uses `SemaphoreSlim` (concurrency 8) and checkpoints every 250 rows so the run is resumable.
3. **Map mode** — `MapMain(minWidth, maxWidth, maxValue)`:
   - Loads `lots_with_dimensions.csv`.
   - Joins `Estimated Value` from `lots.csv` on `Id`.
   - Joins `redfin_value` from `redfin_values.csv` on `Id` (only rows with `status=ok`).
   - Filters by width range and (optionally) max estimated value.
   - Geocodes any matching rows missing from `data\geocodes.csv`, then persists them.
   - Emits `data\map_<min>_<max>[_lt<value>].html` — a self-contained Leaflet page with:
	 - A permanent price label next to each marker (e.g. `$850K`, `$1.2M`). Uses Redfin Estimate when available; falls back to `Estimated Value`.
	 - A popup with address, lot W×D, **both** Redfin Estimate and Estimated Value, and **Propwire + Zillow + Redfin** links.
	 - Zillow link format: `https://www.zillow.com/homes/<address-slug>_rb/` (built client-side from the marker's address fields).
4. **Redfin scrape mode** — `RedfinMain(limit?)`:
   - For each Id in `lots_with_dimensions.csv` not already in `redfin_values.csv` with status `ok`/`not_found`:
	 - Use **Playwright (Chromium)** to load `https://www.redfin.com/`, type the address into the search box, click the first autocomplete suggestion, wait for the URL to match `/home/<id>` — this is the only way to resolve an address to a Redfin property URL, because the `stingray` API is CloudFront-blocked for headless and even headed Chromium without anti-detection.
	 - The browser context injects an init script that hides `navigator.webdriver` and patches `chrome`, `plugins`, `languages` so Redfin's bot detection treats the browser as real. **Without this, even headed Chromium gets a CloudFront 403 on the autocomplete endpoint.**
	 - Once the property URL is known, fetch it via plain `HttpClient` (much faster than Playwright nav) and regex-extract `"predictedValue":<number>` (escaped or unescaped) from the embedded bootstrap JSON. Visible-text `Redfin Estimate $X` is the fallback.
   - Persists results to `data\redfin_values.csv` with columns `Id,redfin_url,redfin_value,status,fetched_at`. Statuses: `ok | not_found | no_estimate | error`. On re-run, `ok`/`not_found` are skipped; `error`/`no_estimate` are retried.
   - Concurrency is intentionally low (`RedfinConcurrency = 2`) to avoid rate limiting; checkpoints every 25 rows. Throughput is ~0.2 rows/sec, so the full Birmingham dataset takes ~9 hours on first run.

---

## Key Decisions / Conventions

- **Frontage definition:** shorter side of the *minimum-area oriented bounding rectangle* of the parcel polygon. This was chosen after the original "edge nearest the street" heuristic produced bad results (e.g. it returned ~127.9 ft depth for 930 Woodlea Dr, instead of the correct ~99.9 ft frontage).
- **Parcel data source:** `https://gisservices.oakgov.com/arcgis/rest/services/Enterprise/EnterpriseOpenParcelDataMapService/MapServer/1/query` (Oakland County Tax Parcel Plus, layer 1). An earlier URL returned 404 and was replaced.
- **Value field:** use **`Estimated Value`** (NOT `Market Value`). Both exist in `lots.csv` but `Estimated Value` is what the user wants on the maps.
- **Propwire link format** is sensitive — must be title-cased slug parts joined with `-`, then the row `Id`, then `/property-details`. Real sample: `https://propwire.com/realestate/1751-W-Lincoln-St-Birmingham-MI-48009/845594/property-details`.
- **Caches are source of truth on re-run.** `LoadDimensionCache` and `LoadGeocodeCache` skip web calls for any `Id` already present. To force a recompute, delete the corresponding row from the cache CSV.
- **No Google Maps API key needed.** Maps use Leaflet + OpenStreetMap tiles.

---

## Known Issues

- **`1658 E LINCOLN ST` (Id `172859647`)** — parcel geometry parsing produced `"requested operation requires an element of type 'Number', but the target element has type 'Array'"`. The row is blank in `lots_with_dimensions.csv`. Not yet fixed; everything else processed cleanly.

## Playwright Setup Notes

First-time setup on a fresh machine:

```powershell
dotnet build LotFrontage -c Release
# Installs Chromium + headless shell (~290 MB total) under %LocalAppData%\ms-playwright
& 'LotFrontage\bin\Release\net10.0\playwright.ps1' install chromium
```

Anti-detection: the `redfin` mode uses a stealth init script (hides `navigator.webdriver`, fakes `plugins` and `languages`, stubs `window.chrome`). Without it, Redfin's CloudFront WAF returns 403 on the autocomplete endpoint even in headed Chromium.

Env vars: `REDFIN_HEADLESS=0` runs the browser visibly (useful when something breaks). `REDFIN_DEBUG=1` was used during initial development to dump page state; the hooks have been removed but can be re-added if needed.

---

## Validated Outcomes

- 930 Woodlea Dr, Birmingham, MI ≈ **99.9 ft** frontage.
- 966 Woodlea St, Birmingham, MI 48009 ≈ **79.9 ft** frontage.
- Full Birmingham batch: 7,351 of 7,352 rows enriched.
- Map runs (after geocode cache warmed):
  - `map 58 68` → 714 markers
  - `map 68 78` → 778 markers
  - `map 78 88` → 782 markers
  - `map 55 10000 850000` → 1,286 markers
- Geocode cache currently holds **2,924** entries.

---

## Future Ideas (Not Yet Implemented)

- Add an **Elementary school** column to the enriched CSV (Pembroke / Pierce / Quarton / Harlan), based on lot coordinates and BPS attendance boundaries.
- Fix the `1658 E LINCOLN ST` geometry edge case in `ComputeLotDimensions`.
- Optional: commit generated `map_*.html` files and publish via GitHub Pages for shareable links.

---

## Session Log

When resuming work, append a one-paragraph summary here (newest at the bottom). This is what lets a fresh chat session pick up exactly where the previous one left off.

- **Initial build:** fixed broken parcel endpoint, replaced edge-heuristic frontage with min-area oriented bounding rectangle, batch-processed Birmingham CSV with concurrency/checkpointing, added cached re-runs, Propwire links, and the `map` mode with permanent price labels and Propwire/Zillow popups. Added optional `maxValue` filter to `map`. Committed `lots_with_dimensions.csv` and `geocodes.csv` into `LotFrontage/data/` (and to `.csproj` `CopyToOutputDirectory`) so the project is resumable from a fresh clone.
- **Redfin scrape:** added `redfin` CLI mode using Microsoft.Playwright 1.60.0 + stealth init script to bypass Redfin's CloudFront bot detection. Two-stage flow: Playwright resolves address → property URL by typing into the search box, then plain HttpClient fetches the property page and a regex extracts `"predictedValue"`. Results persisted to `data\redfin_values.csv` (status `ok`/`not_found`/`no_estimate`/`error`, checkpoint every 25 rows, restartable). Smoke-tested 5/5. Full scrape (~7,349 rows, ~9h at ~0.22 row/s) was kicked off; maps now display Redfin Estimate alongside Estimated Value with a Redfin link in each popup. Price label prefers Redfin when available.
