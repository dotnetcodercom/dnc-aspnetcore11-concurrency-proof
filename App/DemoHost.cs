using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

sealed class ProofReport(string demo)
{
    public string Demo { get; } = demo;
    public string RunId { get; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; private set; }
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public string AspNetCore { get; } = typeof(WebApplication).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public string Os { get; } = RuntimeInformation.OSDescription;
    public string Scope { get; set; } = "";
    public string Status => Checks.Count > 0 && Checks.All(x => x.Pass) ? "PASS" : "FAIL";
    public List<ProofCheck> Checks { get; } = [];
    public Dictionary<string, object?> Observations { get; } = [];
    public Dictionary<string, string> SourceSha256 { get; } = [];
    public string EvidenceDirectory { get; private set; } = "";
    public void Check(string name, bool pass, object? evidence)
    {
        Checks.Add(new(name, pass, evidence));
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {name}");
    }
    public async Task<ProofReport> SaveAsync()
    {
        FinishedAt = DateTimeOffset.UtcNow;
        EvidenceDirectory = Path.GetFullPath(Path.Combine("..", "evidence", RunId));
        Directory.CreateDirectory(EvidenceDirectory);
        foreach (var path in Directory.GetFiles(Directory.GetCurrentDirectory(), "*", SearchOption.AllDirectories)
                     .Where(x => !x.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                     .Order(StringComparer.Ordinal))
            SourceSha256[Path.GetRelativePath(Directory.GetCurrentDirectory(), path).Replace('\\', '/')] =
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "receipt.json"),
            JsonSerializer.Serialize(this, DemoHost.JsonOptions));
        Console.WriteLine($"RESULT: {Status}; CHECKS: {Checks.Count}; EVIDENCE: {EvidenceDirectory}");
        return this;
    }
}
sealed record ProofCheck(string Name, bool Pass, object? Evidence);

static class DemoHost
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static int Port(string[] args, int defaultPort)
    {
        var index = Array.IndexOf(args, "--port");
        if (index < 0) return defaultPort;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var port) || port < 1024 || port > 65535)
            throw new ArgumentException("--port must be a number from 1024 through 65535.");
        return port;
    }
    public static void Map(WebApplication app, Func<Task<ProofReport>> proof, string title, object settings)
    {
        var mutex = new SemaphoreSlim(1, 1);
        ProofReport? last = null;
        app.MapGet("/proof/info", () => Results.Json(new
        { title, settings, runtime = RuntimeInformation.FrameworkDescription, status = last?.Status ?? "NOT_RUN" }))
            .ExcludeFromDescription();
        app.MapGet("/proof/result", () => last is null ? Results.NotFound() : Results.Json(last))
            .ExcludeFromDescription();
        app.MapGet("/proof/receipt", () => last is null ? Results.NotFound() : Results.File(
            JsonSerializer.SerializeToUtf8Bytes(last, JsonOptions), "application/json", "receipt-" + last.RunId + ".json"))
            .ExcludeFromDescription();
        app.MapPost("/proof/run", async (HttpContext context) =>
        {
            // Browser must make a same-origin fetch with a non-simple header.
            if (context.Request.Headers["X-DNC-Proof"] != "1") return Results.BadRequest();
            if (!await mutex.WaitAsync(0)) return Results.Conflict(new { error = "A proof is already running." });
            try { last = await proof(); return Results.Json(last); }
            finally { mutex.Release(); }
        }).ExcludeFromDescription();
    }
    public static async Task FinishAsync(WebApplication app, bool verify, Func<Task<ProofReport>> proof)
    {
        if (verify)
        {
            var report = await proof();
            await app.StopAsync();
            Environment.ExitCode = report.Status == "PASS" ? 0 : 1;
        }
        else
        {
            Console.WriteLine($"BROWSER: {app.Urls.Single()}");
            Console.WriteLine("Click Run verification. Keep this terminal open. Ctrl+C stops the local server.");
            await app.WaitForShutdownAsync();
        }
        await app.DisposeAsync();
    }
}
