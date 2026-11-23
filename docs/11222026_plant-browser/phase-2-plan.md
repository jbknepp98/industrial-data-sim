<plan title="Phase 2: Time series API">

<overview>
Phase 2 of the Plant browser plan (`plan.md`). Adds a Historian read to the runtime and two
browser endpoints: the plant's tag tree and a trend read for up to 4 tags over a start/end
range, thinned to at most 1,000 points per tag.
</overview>

<context>
Assumes Phase 1 is complete: `PlantBrowser.Find(name)` returns only plants this page created.
The runtime has no Historian read today. The read endpoint is documented in
`docs/timebase-api-findings.md:143`:

- `GET /api/datasets/{dataset}/data?tagname=A&tagname=B&start=<ISO>&end=<ISO>`
- Response `{ "s": …, "e": …, "tl": [ { "t": { …tag metadata incl. name… }, "d": [ { "t": "<ISO>", "v": <value>, "q": <int> } ] } ] }`
- May include one leading boundary point before `start`.
- An empty range can return a placeholder `{ "t": "0001-01-01T00:00:00Z", "q": 0 }` with no `v`.

Step 0 of this phase confirms the exact shape live before coding the parser.
</context>

<changes-required>

<change component="Live shape probe (before coding)">
**File**: scratchpad script (not committed)
**Changes**: With the private env loaded (never printed), using a Dataset Claude creates:
1. `POST /api/datasets` `{ "n": "PlanTestReadProbe", "pa": 365 }`.
2. Write three tags (`PlanTestReadProbe.Num` double, `.Flag` bool, `.Text` string), 20 points
   each at 1-minute spacing, ending 10 minutes ago.
3. `GET …/data` for all three over (a) the full range, (b) a range starting mid-way (expect a
   boundary point), (c) an empty range before the first point (expect the placeholder), and
   (d) no start/end (expect the latest point).
4. Record the exact field names of `tl[].t` (which field holds the tag name) and of the points
   in this plan's notes section below, then `DELETE /api/datasets/PlanTestReadProbe`.
Never read or write any other Dataset.
</change>

<change component="HistorianClient.ReadTagData">
**File**: `src/IndustrialDataSim.Runtime/HistorianClient.cs`
**Changes**:

```csharp
public sealed record TagPoint(DateTimeOffset Time, JsonElement Value, int Quality);
public sealed record TagSeries(string Tag, IReadOnlyList<TagPoint> Points);

/// <summary>
/// Reads raw points for up to 4 tags between startUtc and endUtc
/// (GET /api/datasets/{dataset}/data). The leading boundary point before the
/// range and the empty-range placeholder (year 1, no value) are dropped. The
/// response is read up to 32 MB; a larger answer is refused so the page can ask
/// for a shorter range. 30-second deadline. Never retried; error bodies are never echoed.
/// </summary>
public async Task<IReadOnlyList<TagSeries>> ReadTagData(string dataset, IReadOnlyList<string> tags,
    DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken stop)
```

- Build the query with `Uri.EscapeDataString` for every value; dataset via `DatasetPath`.
- Status ≠ 200 → `RequireOk(response, "Tag read")` (same `historian.http_status` style).
- Body over 32 MB → `RuntimeFailure("historian.read_too_large", "Historian returned more than 32 MB for this range. Choose a shorter time range or fewer tags.")`.
- Malformed JSON → `RuntimeFailure("historian.read_unexpected", "Historian answered the tag read in an unexpected format. …")` without the body.
- Tags missing from `tl` come back with an empty point list.
</change>

<change component="Thinning">
**File**: `src/IndustrialDataSim.Web/TrendThinning.cs` (new)
**Changes**: Pure function, unit-tested.

```csharp
public static class TrendThinning
{
    public const int MaxPoints = 1000;
    /// Numeric: split [start, end] into 500 equal buckets; from each non-empty bucket keep
    /// its minimum and maximum point in time order (so spikes survive). Fewer than 1,000
    /// raw points: returned unchanged.
    /// Boolean and text: keep only points where the value changes (plus the first);
    /// if still over 1,000, keep the value at each of 1,000 evenly spaced times.
    public static (IReadOnlyList<TagPoint> Points, bool Thinned) Thin(IReadOnlyList<TagPoint> raw,
        DateTimeOffset start, DateTimeOffset end, string valueType);
}
```
</change>

<change component="Browser endpoints">
**Files**: `src/IndustrialDataSim.Web/PlantBrowser.cs`, `WebApp.cs`, `WebServices.cs`, `Program.cs`
**Changes**:
- `WebServices`: add `public Func<HistorianClient?>? HistorianReader { get; init; }` — one shared
  read client (tests pass a client over a fake handler; null means no Historian).
  `Program.cs` creates it lazily from `HistorianConnection.FromEnvironment()` and disposes it on stop.
- `GET /api/browse/{name}/series`:

```json
{ "dataset": "PlanTestX", "range": { "minUtc": "<StartUtc>", "maxUtc": "<TagCursorUtc or now>" },
  "lines": [ { "id": "L1", "name": "…",
      "tags": [ { "name": "PlanTestX.L1.Speed", "measure": "cases/min", "valueType": "double" } ],
      "equipment": [ { "id": "LB-202", "name": "…", "tags": [ … ] } ] } ],
  "plantTags": [ … ] }
```
  Grouping uses `PlantTag.Line` and `PlantTag.Equipment`; order follows the model.
- `GET /api/browse/{name}/trend?tag=…&tag=…&start=<ISO>&end=<ISO>`:
  - 1–4 `tag` values, each one of the plant's own tags; else 400 `browse.bad_tags`.
  - `start` < `end`, span ≤ 31 days, `start` ≥ plant `StartUtc`, `end` ≤ now + 5 minutes;
    else 400 `browse.bad_range` stating the allowed window.
  - Dataset: registry entry's Dataset, else `services.Dataset` (rc.1 plants); null → 409
    `browse.dataset_unknown`.
  - `HistorianReader` null → 503 `browse.historian_unavailable`.
  - Response:

```json
{ "dataset": "PlanTestX", "start": "…", "end": "…",
  "tags": [ { "name": "…", "measure": "…", "valueType": "double",
              "points": [ ["2026-11-22T10:00:00Z", 4.2], … ], "rawPoints": 8640, "thinned": true } ] }
```
  - Historian failures map to 502 with the RuntimeFailure code and message.
</change>

<change component="Tests">
**Files**: `tests/IndustrialDataSim.Tests/HistorianReadTests.cs` (new), `TrendThinningTests.cs` (new), `BrowseTests.cs`
**Changes**:
- Fake Historian (pattern of `HistorianDatasetTests.Historian`) serving `GET …/data`:
  boundary point dropped; placeholder dropped; missing tag → empty; 33 MB body → `historian.read_too_large`;
  500 → `historian.http_status` without the body; query string escapes a tag name with spaces and `&`.
- Thinning: 10,000 numeric points → ≤ 1,000, includes global min and max, time-ordered;
  under 1,000 unchanged; booleans keep only changes; long text series capped at 1,000.
- Endpoint tests (in-process, fake Historian): another plant's tag → 400 `browse.bad_tags`;
  5 tags → 400; 32-day span → 400 `browse.bad_range`; start before `StartUtc` → 400;
  no Historian → 503; non-owned plant → 404 `browse.not_found`; series tree groups equipment tags.
</change>

</changes-required>

<success-criteria>

<automated-verification>
- [ ] Builds: `dotnet build -c Release`
- [ ] Full suite passes with `FLOWSIM_TEST_PG` set: `dotnet test -c Release`
- [ ] New test classes run: `--filter "FullyQualifiedName~HistorianReadTests|FullyQualifiedName~TrendThinningTests"`
</automated-verification>

<end-to-end-verification>
- [ ] Live shape probe done; notes below filled in; `PlanTestReadProbe` deleted (`GET /api/datasets/PlanTestReadProbe/exists` → false).
- [ ] The parser in `ReadTagData` matches the recorded live shape (tag-name field, point fields).
- [ ] No other Dataset was read or written (probe script lists only `PlanTestReadProbe` URLs).
</end-to-end-verification>

</success-criteria>

<notes>
Live shape (fill in during Step 0): tag-name field in `tl[].t` = …; boundary point observed = …;
placeholder observed = …; latest-point read = ….
</notes>

<post-verification>
Commit: "Plant browser API: tag tree and thinned trend reads from the plant's own Dataset".
</post-verification>

</plan>
