# BirminghamLots / LotFrontage — Program Design Reference

> A complete, self-contained design description of the `LotFrontage` console
> application. Written to be used as training/reference material for an AI
> assistant that will maintain or extend this codebase. It documents intent,
> architecture, data contracts, algorithms, external services, failure modes,
> and the reasoning behind each design decision.

---

## 1. Purpose

`LotFrontage` is a .NET 10 console application that builds a real-estate
analysis pipeline for residential lots in and around Birmingham, Michigan
(Oakland County). It does four things:

1. **Computes lot dimensions** (frontage width + depth) for each property by
   pulling the parcel polygon from a county GIS service and fitting a
   minimum-area oriented bounding rectangle.
2. **Enriches** the source CSV with `lot_width`, `lot_depth`, and a
   `propwire_link`, writing `lots_with_dimensions.csv`.
3. **Scrapes Redfin** for each property's "Redfin Estimate" using Playwright
   (browser automation) plus a follow-up HTML fetch, persisting results to
   `redfin_values.csv`.
4. **Generates interactive Leaflet/OpenStreetMap HTML maps** that plot lots
   filtered by width and value, with client-side filter controls and popups
   linking to Propwire, Zillow, and Redfin.

The end user is a person evaluating buildable/!investment lots: they want to
see, on a map, lots in a particular frontage band and price range, comparing
the county "Estimated Value" against the (more trusted) Redfin Estimate.

---

## 2. Tech stack & environment

- **Runtime / language:** .NET 10, C# with `ImplicitUsings` and `Nullable`
  enabled. Single-project console app.
- **Namespace:** `ParcelFrontage`; entry class `Program`. (Note: the assembly
  /folder is named `LotFrontage` but the root namespace is `ParcelFrontage`.)
- **Key NuGet package:** `Microsoft.Playwright` 1.60.0 (Chromium automation).
  Chromium is installed once via the generated `playwright.ps1 install chromium`
  script under `bin/.../playwright.ps1`.
- **Standard libraries:** `System.Net.Http` (single shared `HttpClient`),
  `System.Text.Json`, `System.Text.RegularExpressions`, `System.Globalization`
  (everything parses/formats with `CultureInfo.InvariantCulture`).
- **Solution:** `LotFrontage.slnx` (XML-based solution format).
- **Repo:** `https://github.com/spadapet/BirminghamLots`, default branch `main`.
- **Dev environment:** Visual Studio Enterprise 2026 (insiders), PowerShell.

All file paths are resolved relative to `AppContext.BaseDirectory` + `data`,
i.e. the app reads/writes the `data` folder **next to the built binary**
(`bin/<Config>/net10.0/data`). Source copies of the caches live under
`LotFrontage/data/` and are copied to output via `CopyToOutputDirectory`.

---

## 3. Project layout

```
LotFrontage/                     repo root (BirminghamLots)
├─ LotFrontage.slnx
├─ DESIGN.md                      this document
├─ LotFrontage/
│  ├─ LotFrontage.csproj          net10.0, Playwright ref, data copy rules
│  ├─ Program.cs                  ENTIRE application (single file)
│  └─ data/                       source caches (copied to output)
│     ├─ lots.csv                 INPUT: raw lot list (~7,350 rows)
│     ├─ lots_with_dimensions.csv enriched output cache
│     ├─ geocodes.csv             Id -> lat,lon cache
│     └─ redfin_values.csv        Id -> redfin estimate cache (csproj-referenced)
└─ LotFrontage/bin/<Config>/net10.0/data/
   ├─ (runtime copies of all the above)
   ├─ map_*.html                  generated static maps
   └─ map_interactive_55.html     generated interactive map
```

> **Important:** `Program.cs` is intentionally a single file with all logic.
> When editing, keep the region-comment structure (CONFIG, MAP MODE,
> INTERACTIVE MAP MODE, REDFIN ESTIMATE SCRAPER, PARCEL QUERY, FRONTAGE
> CALCULATION, CSV I/O, etc.).

---

## 4. CLI dispatch (modes)

`Main(string[] args)` switches on `args[0]`:

| Command | Method | Arguments | Purpose |
|--------|--------|-----------|---------|
| _(none)_ | inline batch in `Main` | — | Enrich `lots.csv` → `lots_with_dimensions.csv` (compute dimensions). |
| `map` | `MapMain` | `minWidth maxWidth [maxValue] [redfin]` | Generate one static filtered map. `redfin` (5th arg) filters by Redfin value instead of Estimated Value. |
| `mapi` | `MapInteractiveMain` | `[minWidth=55]` | Generate the interactive map: bakes all lots ≥ floor width, filters client-side. |
| `redfin` | `RedfinMain` | `[limit]` | Scrape Redfin estimates into `redfin_values.csv`. `limit` caps rows (used for smoke tests). |

All argument parsing uses `CultureInfo.InvariantCulture`. Examples:

```powershell
dotnet run --project LotFrontage -c Release                       # enrich dimensions
dotnet run --project LotFrontage -c Release -- map 58 100 800000  # static map (Estimated Value)
dotnet run --project LotFrontage -c Release -- map 58 100 800000 redfin  # static map (Redfin value)
dotnet run --project LotFrontage -c Release -- mapi 55            # interactive map
dotnet run --project LotFrontage -c Release -- redfin             # full Redfin scrape
dotnet run --project LotFrontage -c Release -- redfin 5           # Redfin smoke test (5 rows)
```

---

## 5. Data contracts (CSV schemas)

### 5.1 `lots.csv` (INPUT — do not regenerate)
Columns referenced by code (case-insensitive header match): `Id`, `Address`,
`City`, `State`, `Zip`, `Estimated Value`. Other columns may exist and are
preserved. `Estimated Value` is the county/aggregator estimate; it is known to
be inaccurate vs. Redfin/Zillow (the entire reason the Redfin scraper exists).

### 5.2 `lots_with_dimensions.csv` (enriched output / cache)
`lots.csv` columns **plus** three appended columns:
- `lot_width` — short side of the min-area rectangle, feet, `F1` format.
- `lot_depth` — long side of the min-area rectangle, feet, `F1` format.
- `propwire_link` — deep link to the Propwire property page.

Blank `lot_width`/`lot_depth` mean that row failed geocode/parcel lookup and is
eligible for reprocessing on the next default-mode run.

### 5.3 `geocodes.csv` (cache)
`Id,lat,lon`. Populated by `SaveGeocodeCache`, read by `LoadGeocodeCache`.
Shared by both map modes to avoid re-geocoding.

### 5.4 `redfin_values.csv` (cache)
`Id,redfin_url,redfin_value,status,fetched_at`.
- `status` ∈ { `ok`, `not_found`, `no_estimate`, `error` }.
- `fetched_at` is a UTC timestamp (round-trippable; parsed with
  `AssumeUniversal | AdjustToUniversal`).
- **Resume rule:** rows with status `ok` or `not_found` are skipped on restart;
  `no_estimate` and `error` are retried. This makes the scrape idempotent and
  restartable.

---

## 6. Pipeline stages in detail

### 6.1 Default mode — dimension enrichment (`Main`)

1. Read `lots.csv`. Find column indices by header name.
2. Load existing `lots_with_dimensions.csv` into a **dimension cache** keyed by
   `BuildCacheKey` (prefers `id:<Id>`; falls back to a normalized
   `addr:<ADDR>|<CITY>|<STATE>|<ZIP>` key). Cached width/depth are reused.
3. Append `lot_width`, `lot_depth`, `propwire_link` headers. For every row,
   compute the Propwire link immediately (cheap, deterministic) and fill
   width/depth from cache if present; otherwise queue the row index.
4. Process queued rows concurrently (`MaxConcurrency = 8`, `SemaphoreSlim`
   throttle). For each: `GeocodeAddress` → `QueryParcel` → `ComputeLotDimensions`.
   Errors leave the cells blank and increment an error counter (compact log).
5. **Checkpoint** the full CSV every `CheckpointEvery = 250` completed rows
   (guarded by a second semaphore + double-check, and tolerant of `IOException`
   if the file is open in Excel). Final write at the end.
6. Progress every 50 rows: `[done/total] errors rate eta`.

### 6.2 Geocoding (`GeocodeAddress`)

ArcGIS World GeocodeServer `findAddressCandidates`:
`https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/findAddressCandidates?SingleLine=<addr>&f=json&maxLocations=1`.
Returns `(lon, lat)` from `candidates[0].location.{x,y}`; throws if no
candidate. Optional `locationType` parameter is unused in current callers.

### 6.3 Parcel query (`QueryParcel`)

Oakland County ArcGIS MapServer layer 1 ("Tax Parcel Plus"):
`https://gisservices.oakgov.com/arcgis/rest/services/Enterprise/EnterpriseOpenParcelDataMapService/MapServer/1/query`.
Point-in-polygon intersect query (`geometryType=esriGeometryPoint`, `inSR=4326`,
`spatialRel=esriSpatialRelIntersects`, `returnGeometry=true`, `f=geojson`,
`outFields=*`). Reads `features[0].geometry.coordinates[0]` (the outer ring) and
returns a `List<Point>` of lon/lat vertices. Throws if no feature.

> Historical note: an earlier endpoint 404'd; this is the corrected one. If
> parcel lookups suddenly start failing wholesale, suspect the endpoint URL or
> layer index first.

### 6.4 Frontage algorithm (`ComputeLotDimensions`) — the core math

Goal: a robust, orientation-independent width/depth from an arbitrary parcel
polygon (lots are rarely axis-aligned and rings can be noisy).

1. **Project lat/lon → local feet.** `feetPerDegreeLat = 364000`;
   `feetPerDegreeLon = cos(avgLat) * feetPerDegreeLat`. Relative to the first
   vertex so coordinates are small. This is locally isotropic over one parcel.
2. Drop the duplicate closing vertex if present.
3. **Convex hull** via Andrew's monotone chain (`ConvexHull`, CCW, using
   `Cross`). The minimum-area enclosing rectangle of a polygon is always aligned
   to an edge of its convex hull.
4. **Rotating-calipers-style search:** for each hull edge, build a unit vector,
   project all hull points onto that axis and its perpendicular, take the
   axis-aligned bounding box (`maxU-minU` × `maxV-minV`), and keep the rectangle
   with the **smallest area**.
5. Return `(shortSide, longSide)` = `(width, depth)`.

> Rationale: width = frontage. Using the min-area rectangle's short side gives a
> stable frontage even for irregular/rotated lots, which a naive
> compass/bounding-box approach got wrong (it produced depth-like values). This
> was a key correctness fix.

Validated: `930 Woodlea Dr` ≈ 99.9 ft; `966 Woodlea St` ≈ 79.9 ft.

`DistanceFeet` is a helper using the same lat/lon→feet approximation (currently
not on the main dimension path but kept for distance needs).

### 6.5 Propwire links (`BuildPropwireLink` / `SlugifyForPropwire`)

Format: `https://propwire.com/realestate/<slug>/<Id>/property-details` where
`<slug>` = title-cased, hyphen-joined tokens of address + city, then raw
upper-cased state + raw zip (e.g. `123-Main-St-Birmingham-MI-48009`). Requires a
non-empty `Id`. `TitleCaseToken` upper-cases the first letter and lower-cases
the rest, leaving pure-digit tokens unchanged.

### 6.6 Redfin scraper (`RedfinMain` + `FetchRedfinEstimateAsync`)

**Why a browser is required:** Redfin's public `stingray` autocomplete/search
APIs are CloudFront-guarded and return 403 to plain `HttpClient` calls (even via
in-page `fetch` from headless Chromium). The working approach is to drive the
**homepage search box** in a real browser to resolve the property URL, then
fetch *that* property page over `HttpClient` and parse embedded JSON.

`RedfinMain(int? limit)`:
1. Read `lots_with_dimensions.csv`; load `redfin_values.csv` cache.
2. Build the to-do list (skip `ok`/`not_found`; retry the rest). Apply `limit`.
3. Launch Playwright Chromium. `Headless` defaults true but is disabled when
   env var `REDFIN_HEADLESS=0` (lets you watch the browser).
   Launch args: `--disable-blink-features=AutomationControlled`,
   `--disable-features=IsolateOrigins,site-per-process`.
   Context: desktop Chrome UA string, 1280×800 viewport, `en-US` locale.
4. **Anti-detection init script** injected per context: override
   `navigator.webdriver → undefined`, fake `window.chrome`, fake
   `navigator.plugins` and `navigator.languages`. Without these, even headed
   Chromium gets 403'd by the WAF.
5. Scrape concurrently with `RedfinConcurrency = 2` (deliberately low to avoid
   tripping rate limits). Checkpoint `redfin_values.csv` every
   `RedfinCheckpointEvery = 25` rows; final save at the end. Progress every 10
   rows with `ok/notFound/noEst/err` counters + rate + eta.

`FetchRedfinEstimateAsync(context, addr, city, state, zip)`:
1. New page → `https://www.redfin.com/` (DOMContentLoaded, 25s timeout). On
   timeout/Playwright error → `("", 0, "error")`.
2. Find the search box via a resilient multi-selector
   (`input[name='searchInputBox'], input[data-rf-test-name='search-box-input'],
   input[placeholder*='Address'], input[type='search']`), click, fill the full
   address, wait ~1.5s.
3. Wait for the autocomplete dropdown
   (`[id^='search-box-results-item-'], .search-result-row, .item-row`) and click
   the first row; if it never appears, press Enter as a fallback.
4. `WaitForURLAsync(/\/home\/\d+/, 15s)` to capture the property URL. Timeout →
   `not_found`.
5. **ZIP sanity check:** if the resolved URL doesn't contain the input ZIP,
   treat as `not_found` (guards against the search resolving to the wrong home).
6. Strip query/fragment, fetch the clean property URL via `HttpClient` with
   browser-like headers. Non-200 → `error`.
7. **Parse the estimate** from the HTML:
   - Primary: `RedfinValueRegex` matches `"predictedValue":<number>` **including
	 escaped quotes** — Redfin embeds JSON as escaped strings, so the pattern is
	 `\\?"predictedValue\\?"\s*:\s*(\d+(?:\.\d+)?)`. (The escaped-quote handling
	 was a required bug fix; the first version missed every page.)
   - Fallback: `RedfinFallbackRegex` matches visible text
	 `Redfin\s+Estimate[^$]{0,200}\$([0-9,]+)`.
   - No match → `no_estimate`.
8. Always close the page in `finally`.

`RedfinHomeUrlRegex` exists for matching full Redfin home URLs of the form
`https://www.redfin.com/<ST>/<city>/<addr>/home/<id>` (utility/validation).

Validated Redfin estimates extracted: ~$1,258,099 / $657,461 / $588,401 /
$723,300 / $943,368. Smoke tests returned 5/5 and 3/3 `ok`. Full scrape rate
≈ 0.25–0.30 rows/sec (≈ several hours for ~7,300 rows) — hence restartability is
essential.

### 6.7 Static map generation (`MapMain`)

`MapMain(minWidth, maxWidth, maxValue = +inf, useRedfinValue = false)`:
1. Build `Id → Estimated Value` lookup from `lots.csv`.
2. Build `Id → (redfin_url, redfin_value)` lookup from `redfin_values.csv`
   (only `status == ok`, value > 0).
3. Read `lots_with_dimensions.csv`, filter rows by `lot_width ∈ [minWidth,
   maxWidth]`. If `maxValue` finite, also require the chosen value source
   (Redfin if `useRedfinValue`, else Estimated) to be `> 0` and `< maxValue`.
   When filtering by Redfin with no max, still require a Redfin value > 0.
4. Geocode any missing rows (concurrency 8, checkpoint every 250), persist to
   `geocodes.csv`.
5. Emit a self-contained Leaflet HTML file. Output filename encodes the filter:
   `map_<minW>_<maxW>[_lt<maxValue>][_redfin].html`.

Popups show: address, city/state/zip, width/depth, **Redfin Estimate**,
**Estimated Value**, and links to Propwire, Zillow (slug-built client-side), and
Redfin (when a URL exists). A permanent price tooltip shows the display value
(prefers Redfin, falls back to Estimated).

### 6.8 Interactive map (`MapInteractiveMain`) — the recommended UX

`MapInteractiveMain(floorWidth)` bakes **every** lot with `lot_width ≥
floorWidth` (default 55) into one HTML file (`map_interactive_<floor>.html`),
then does **all filtering client-side** so the user never has to regenerate.

Controls (top-left panel): Min/Max width (ft), Min/Max value ($), value-source
radio (Redfin vs Estimated), "Require value" checkbox, "Show price labels"
checkbox, and a live "N lots shown (of total)" counter. Default control values:
min width 58, max width 100, min value 0, max value 800000, source = Redfin.

**Performance design (critical — was a bug fix):** the naive version rebuilt all
markers/popups/tooltips on every keystroke and toggled thousands of *permanent*
Leaflet tooltips, causing huge layout reflows (symptom: first keystroke very
slow, later ones fast). The fixed design:
- Build each `L.marker` **once** at load; cache it on the data object
  (`m._marker`).
- **Lazy popups:** popup HTML is generated only on `popupopen`
  (`setPopupContent`), not for all markers up front.
- **Add/remove only:** filtering adds/removes markers from a single
  `L.layerGroup` (`marker._shown` flag); nothing is recreated.
- **Tooltip diffing:** a marker's price tooltip is rebuilt only when its text
  actually changes (`marker._tipVal`); unchanged labels are left alone.
- **Debounced** input handler (120 ms) coalesces fast typing into one render.
- `preferCanvas: true` on the map for lighter rendering.
- "Show price labels" lets the user disable permanent labels entirely for
  instant filtering at any scale.

**Map controls:** both map modes create the map with `zoomControl: false` and
add `L.control.zoom({ position: 'topright' })` so the zoom buttons sit in the
**top-right** (the filter panel occupies the top-left and would otherwise cover
Leaflet's default top-left zoom control).

Map tiles: OpenStreetMap (`https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png`,
maxZoom 19). Leaflet 1.9.4 from unpkg CDN. Initial view fits all baked markers /
centers on their average lat/lon.

### 6.9 CSV utilities

- `ReadCsv` — minimal RFC-4180-ish parser (handles quoted fields, embedded
  commas/newlines, `""` escaping). Returns `List<List<string>>` (row 0 = header).
- `WriteCsv` / `EscapeCsv` — quote fields containing `,`, `"`, or newlines.
- `Get(row, idx)` — safe indexed access returning `""` for out-of-range/`-1`.

---

## 7. Concurrency & resilience model

- Two long-running stages (dimension enrichment, Redfin scrape) and map
  geocoding all use a `SemaphoreSlim` throttle + `Task.WhenAll`.
- Concurrency: dimension/geocode = `8`; Redfin = `2` (anti-rate-limit).
- **Checkpointing** writes the full cache CSV periodically (250 for
  dimensions/geocodes, 25 for Redfin), guarded against concurrent writes and
  tolerant of `IOException` (file open in Excel → skip this checkpoint, keep
  going). This makes every long job safely interruptible and resumable.
- Background runs in this project are routinely cancelled between sessions;
  resumability (skip already-`ok` rows) is what makes that acceptable. For a
  truly uninterrupted long scrape, run the command in a standalone terminal.

---

## 8. External dependencies & failure modes

| Dependency | Used for | Typical failure | Handling |
|-----------|----------|-----------------|----------|
| ArcGIS World Geocoder | address → lat/lon | no candidate / network | throws; row error, blank cells |
| Oakland County parcel MapServer | lat/lon → polygon | endpoint change / no feature | throws; row error, blank cells |
| Redfin (Playwright + HTTP) | estimate scrape | CloudFront 403, search miss, layout change | per-row status `error`/`not_found`/`no_estimate`; retried next run |
| Playwright Chromium | browser automation | not installed | run `playwright.ps1 install chromium` once |
| Leaflet 1.9.4 (unpkg) | map JS/CSS | CDN offline | maps need internet to render |
| OpenStreetMap tiles | base map | tile/rate limits | cosmetic only |

---

## 9. Conventions for future edits

- Keep everything in `Program.cs`; preserve the section banner comments.
- Always use `CultureInfo.InvariantCulture` for parse/format of numbers and
  timestamps. Money formats as `F0`-ish in JS; dimensions as `F1` in C#.
- Match CSV columns by **header name, case-insensitively** (`FindIndex` +
  `Equals(..., OrdinalIgnoreCase)`), never by fixed index — input column order
  may vary and extra columns must be preserved.
- New caches should be sidecar CSVs with an `Id` key, a `status` where retries
  matter, and checkpointed writes; follow the `redfin_values.csv` pattern.
- Map HTML is built with C# raw string interpolation (`$$""" ... """`), so JS
  template-literal `${...}` and CSS `{...}` are fine but C# interpolations use
  `{{ ... }}`. Be careful with brace escaping when editing the embedded HTML.
- When changing map filtering UX, preserve the "build markers once, add/remove
  only, diff tooltips, debounce input" performance pattern.
- `Estimated Value` (not "Market Value") is the canonical county value column.

---

## 10. Known-good results / regression anchors

- Frontage: `930 Woodlea Dr, Birmingham, MI` ≈ 99.9 ft; `966 Woodlea St,
  Birmingham, MI 48009` ≈ 79.9 ft.
- Lots with `lot_width ≥ 55`: 3,715 (baked into the interactive map).
- Static map marker counts (fuzzy width bands): 58–68 → 714, 68–78 → 778,
  78–88 → 782; width ≥ 55 & Estimated Value < $850k → 1,286.
- Redfin cache reached full coverage (~7,324 `ok` entries) over multiple
  resumable runs.

---

## 11. Glossary

- **Frontage / width:** the street-facing dimension of the lot; here the short
  side of the min-area oriented bounding rectangle.
- **Depth:** the long side of that rectangle.
- **Estimated Value:** the county/aggregator value from `lots.csv` (less
  accurate).
- **Redfin Estimate / `predictedValue`:** Redfin's algorithmic value, scraped
  from the property page's embedded JSON (preferred for filtering).
- **Min-area rectangle:** smallest-area enclosing rectangle of the convex hull;
  the basis for width/depth.
