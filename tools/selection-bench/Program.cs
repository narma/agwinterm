using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using Agwinterm.Core;

Console.WriteLine($"Runtime={Environment.Version}; CPU threads={Environment.ProcessorCount}; ServerGC={GCSettings.IsServerGC}");
VerifyParity();
var results = new List<object>();
foreach (var (cols, rows) in new[] { (120, 40), (240, 100), (400, 200) })
foreach (var scenario in new[] { "create-word", "create-all", "create-copy-all", "cached-copy-all", "idle-word", "status-word", "scroll-word", "absent-word", "ambiguous-all", "repeat-status-all", "similar-status-all" }
    .Where(name => args.Length < 2 || name == args[1]))
foreach (bool baseline in new[] { true, false })
{
    var screen = new ScreenBuffer(cols, rows);
    bool repeated = scenario is "ambiguous-all" or "repeat-status-all" or "similar-status-all";
    for (int r = 0; r < rows; r++)
    {
        string prefix = $"{r:D6}__";
        for (int c = 0; c < cols; c++)
            screen[r, c] = Cell.Empty with { Rune = repeated ? 'x' : (c < 8 ? prefix[c] : 'a' + (r * 7 + c) % 26) };
        if (scenario == "similar-status-all")
            for (int c = 0; c < 6; c++) screen[r, cols - 6 + c] = Cell.Empty with { Rune = prefix[c] };
    }
    int first = scenario.Contains("all") ? 0 : rows / 2;
    int last = scenario.Contains("all") ? rows - 1 : first;
    int end = scenario.Contains("all") ? cols - 1 : 7;
    Func<object> create = baseline
        ? () => new BaselineContentSelection(screen, first, 0, last, end, false)
        : () => new ContentSelection(screen, first, 0, last, end, false);
    object selection = create();
    Func<string> copy = baseline ? () => ((BaselineContentSelection)selection).Text : () => ((ContentSelection)selection).Text;
    if (scenario == "cached-copy-all") _ = copy();
    Func<bool> locate = baseline ? () => ((BaselineContentSelection)selection).TryLocate(screen, out _)
        : () => ((ContentSelection)selection).TryLocate(screen, out _);
    if (scenario == "absent-word") screen.Clear();
    if (scenario == "ambiguous-all") screen[rows / 2, 0] = Cell.Empty;
    int iteration = 0, sink = 0;
    var saved = new Cell[cols];
    Action operation = scenario.StartsWith("create") ? () =>
    { selection = create(); if (scenario == "create-copy-all") sink += copy().Length; else sink++; }
    : scenario == "cached-copy-all" ? () => { sink += copy().Length; } : () =>
    {
        if (scenario is "status-word" or "repeat-status-all" or "similar-status-all")
            screen[rows - 1, cols - 1] = Cell.Empty with { Rune = (iteration++ & 1) == 0 ? '!' : '?' };
        if (scenario == "scroll-word")
        {
            screen.CopyRowTo(0, saved);
            screen.MoveRows(1, 0, rows - 1);
            for (int c = 0; c < cols; c++) screen[rows - 1, c] = saved[c];
        }
        if (locate()) sink++;
    };
    for (int i = 0; i < 40; i++) operation();
    var calibration = Stopwatch.StartNew();
    for (int i = 0; i < 20; i++) operation();
    int count = Math.Clamp((int)(0.075 / Math.Max(calibration.Elapsed.TotalSeconds / 20, 1e-9)), 5, 20000);
    var timings = new double[7];
    var allocations = new double[7];
    for (int sample = 0; sample < timings.Length; sample++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++) operation();
        timings[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / count;
        allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)count;
    }
    Array.Sort(timings); Array.Sort(allocations);
    var item = new { cols, rows, scenario, baseline, medianUs = timings[3], maxSampleUs = timings[^1], bytesPerOp = allocations[3], count, sink };
    results.Add(item);
    Console.WriteLine(JsonSerializer.Serialize(item));
}
File.WriteAllText(args.Length > 0 ? args[0] : "results.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

static void VerifyParity()
{
    var random = new Random(7519);
    int checks = 0;
    for (int trial = 0; trial < 150; trial++)
    {
        int cols = random.Next(5, 25), rows = random.Next(2, 12);
        var screen = new ScreenBuffer(cols, rows);
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++) screen[row, col] = Cell.Empty with { Rune = random.Next(3) == 0 ? ' ' : 'a' + random.Next(3) };
        int first = random.Next(rows), last = random.Next(first, rows);
        int from = random.Next(cols), to = random.Next(first == last ? from : 0, cols);
        bool block = random.Next(2) == 0;
        var before = new BaselineContentSelection(screen, first, from, last, to, block);
        var after = new ContentSelection(screen, first, from, last, to, block);
        for (int step = 0; step < 120; step++)
        {
            switch (random.Next(5))
            {
                case 0: screen.MoveRows(1, 0, rows - 1); screen.FillRow(rows - 1, Cell.Empty); break;
                case 1: screen.MoveRows(0, 1, rows - 1); screen.FillRow(0, Cell.Empty); break;
                case 2: screen[random.Next(rows), random.Next(cols)] = Cell.Empty with { Rune = 'a' + random.Next(3) }; break;
                case 3: screen.Clear(); break;
            }
            bool expected = before.TryLocate(screen, out int expectedRow), actual = after.TryLocate(screen, out int actualRow);
            if (expected != actual || (actual && expectedRow != actualRow) || before.Text != after.Text)
                throw new Exception($"Parity failed at trial {trial}, step {step}: {expected}/{expectedRow} != {actual}/{actualRow}");
            checks++;
        }
    }
    Console.WriteLine($"Differential checks against baseline: {checks} passed");
}
