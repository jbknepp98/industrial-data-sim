using System.Text;
using System.Text.Json;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

// Offline demonstration evidence, bounded to 100000 candidate slots and 16 MiB.
// Uses the unchanged full model and the production window generator. Shortening
// only the preview fence preserves all source/gate clocks and random schedules.
// An optional second argument raises the preview fence (default 72 hours, at
// most 96) so a backfill-plus-live acceptance model can be reviewed end to end.
// The slot and byte budgets below still apply, so long previews need a coarser
// preview copy of the sampling interval.
int previewHours = 72;
if (args.Length is < 1 or > 2 || (args.Length == 2 && (!int.TryParse(args[1], out previewHours) || previewHours is < 1 or > 96)))
{
    Console.WriteLine("{\"valid\":false,\"code\":\"preview.usage\",\"message\":\"Supply one model file and optionally a preview length of 1-96 hours (default 72). The probe never connects to a Historian.\"}");
    return 2;
}
try
{
    using var file = File.OpenRead(args[0]);
    var bytes = new byte[1024 * 1024 + 1];
    int read = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
    if (read == bytes.Length) return Fail("preview.model_limit", "Model exceeds 1 MiB. Reduce its schedule before previewing.");
    var loaded = SimulationDefinitionLoader.Load(Encoding.UTF8.GetString(bytes, 0, read));
    if (!loaded.IsValid)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { valid = false, errors = loaded.Errors }));
        return 1;
    }
    var model = loaded.Definition!;
    var end = model.Session.EndUtc;
    if ((end - model.Session.StartUtc) > TimeSpan.FromHours(previewHours)) end = model.Session.StartUtc.AddHours(previewHours);
    long interval = model.SamplingIntervalMs * TimeSpan.TicksPerMillisecond;
    long slots = ((end - model.Session.StartUtc).Ticks - 1) / interval + 1;
    if (slots > 100000 / model.Session.OutputTags.Count)
        return Fail("preview.slot_limit", "The previewed hours exceed 100000 candidate slots. Increase the preview copy's sampling interval or request fewer hours.");
    var data = model.Session.OutputTags.ToDictionary(t => t.Name, _ => new List<TvqPoint>());
    long cursor = 0, size = 0;
    while (true)
    {
        var window = GenerationWindow.Generate(model, cursor, 10000, 1000, 1024 * 1024, end.AddTicks(-1));
        if (window.Error is { } error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { valid = false, errors = new[] { error } }));
            return 1;
        }
        size += Encoding.UTF8.GetByteCount(window.Payload);
        if (size > 15 * 1024 * 1024) return Fail("preview.output_limit", "Generated data exceeds the preview byte budget. Shorten the preview model's end time or increase its sample interval.");
        foreach (var (name, points) in JsonSerializer.Deserialize<Dictionary<string, List<TvqPoint>>>(window.Payload)!) data[name].AddRange(points);
        if (cursor == window.NextSlot || window.NextSlot == GenerationWindow.TotalSlots(model)) break;
        cursor = window.NextSlot;
    }
    string result = JsonSerializer.Serialize(new { valid = true, mode = "offline", startUtc = model.Session.StartUtc,
        endUtc = end, candidateSlots = slots * model.Session.OutputTags.Count, pointCount = data.Values.Sum(p => p.Count), data });
    if (Encoding.UTF8.GetByteCount(result) > 16 * 1024 * 1024)
        return Fail("preview.output_limit", "Preview exceeds 16 MiB. Shorten the preview model's end time or increase its sample interval.");
    Console.WriteLine(result);
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{
    return Fail("preview.file_access", "Cannot read the model or write the preview. Check file existence, permissions and output storage; no Historian connection was made.");
}
static int Fail(string code, string message)
{
    Console.WriteLine(JsonSerializer.Serialize(new { valid = false, code, message }));
    return 1;
}
