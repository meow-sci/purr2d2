using System.Text.Json;
using Purr2D2.Cutting;
using Purr2D2.Slicing.Detection;

// Offline only: inputs are copied target-local triangles and endpoint pairs.
// This never reads a live scene and cannot evaluate native proxy detectors.
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/SliceReplay -- fixture.json");
    return 2;
}
try
{
    var file = new FileInfo(args[0]);
    if (!file.Exists || file.Length > 16 * 1024 * 1024) throw new InvalidDataException("Fixture must exist and be at most 16 MiB.");
    var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(file.FullName),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty fixture.");
    if (fixture.Triangles == null || fixture.Intervals == null || fixture.Triangles.Length > 100000
        || fixture.Intervals.Length > 256) throw new InvalidDataException("Maximum is 100,000 triangles and 256 intervals.");
    if (fixture.Source == "runtime-copied-mesh" && fixture.Frame != "hit-part-local")
        throw new InvalidDataException("Runtime captures must use the declared hit-part-local frame.");
    if (fixture.Source == "runtime-copied-mesh")
        Console.Error.WriteLine($"Runtime fixture: hit-part-local mesh; detector {fixture.Detector ?? "unknown"}; full mesh scan {fixture.FullMeshScan == true}; recorded hit t={fixture.HitTime01?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}, s={fixture.AlongBlade01?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.");
    CutPoint Point(double[]? p)
    {
        if (p is not { Length: 3 } || p.Any(x => !double.IsFinite(x))) throw new InvalidDataException("Each point must have three finite coordinates.");
        return new(p[0], p[1], p[2]);
    }
    var triangles = fixture.Triangles.Select(t => t is { Length: 3 }
        ? new CutTriangle(Point(t[0]), Point(t[1]), Point(t[2]))
        : throw new InvalidDataException("Each triangle must contain three points.")).ToArray();
    // Validate and copy every interval before either strategy runs so both use
    // identical geometry, sample cadence and endpoint coordinates.
    var sweeps = fixture.Intervals.Select(s =>
    {
        if (s == null || !double.IsFinite(s.Tolerance) || s.Tolerance <= 0) throw new InvalidDataException("Tolerance must be positive and finite.");
        return new BladeSweep(Point(s.Base0), Point(s.Tip0), Point(s.Base1), Point(s.Tip1), s.Tolerance);
    }).ToArray();
    var geometry = new CachedRibbonGeometry(triangles);
    Console.WriteLine("interval,method,status,hit_time,along,triangle_tests,node_visits");
    for (int i = 0; i < sweeps.Length; i++)
    {
        var sweep = sweeps[i];
        if (sweep.Subdivisions > BladeSweep.MaximumSubdivisions) Console.WriteLine($"{i},legacy-ribbon,unsupported,,,0,0");
        else
        {
            double time = double.PositiveInfinity, along = double.PositiveInfinity;
            foreach (var triangle in triangles)
                if (sweep.TryHit(triangle, out _, out double a, out double t)
                    && (t < time - 1e-10 || Math.Abs(t - time) <= 1e-10 && a < along)) { time = t; along = a; }
            Console.WriteLine(double.IsFinite(time)
                ? FormattableString.Invariant($"{i},legacy-ribbon,hit,{time:R},{along:R},{triangles.Length},0")
                : $"{i},legacy-ribbon,miss,,,{triangles.Length},0");
        }
        var query = geometry.Begin(sweep);
        CachedRibbonGeometry.Progress result; int nodes = 0, tested = 0, updates = 0;
        do
        {
            result = query.Advance(new(4096, 4096)); nodes += result.NodesVisited; tested += result.TrianglesTested;
            if (++updates > 10000) throw new InvalidDataException("Replay exceeded its bounded update count.");
        } while (result.Status == CachedRibbonGeometry.QueryStatus.Pending);
        Console.WriteLine(result.Status == CachedRibbonGeometry.QueryStatus.UnsupportedMotion
            ? $"{i},cached-ribbon,unsupported,,,{tested},{nodes}"
            : result.Best is { } hit
                ? FormattableString.Invariant($"{i},cached-ribbon,hit,{hit.Time:R},{hit.Along:R},{tested},{nodes}")
                : $"{i},cached-ribbon,miss,,,{tested},{nodes}");
    }
    Console.Error.WriteLine("Copied mesh replay only; native clutter, frame time, queueing and world mutations are not measured.");
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine("Replay rejected: " + error.Message);
    return 1;
}

internal sealed class Fixture
{
    public string? Source { get; set; }
    public string? Frame { get; set; }
    public string? Detector { get; set; }
    public bool? FullMeshScan { get; set; }
    public double? HitTime01 { get; set; }
    public double? AlongBlade01 { get; set; }
    public double[][][]? Triangles { get; set; }
    public Interval[]? Intervals { get; set; }
}
internal sealed class Interval
{
    public double[]? Base0 { get; set; }
    public double[]? Tip0 { get; set; }
    public double[]? Base1 { get; set; }
    public double[]? Tip1 { get; set; }
    public double Tolerance { get; set; } = .0025;
}
