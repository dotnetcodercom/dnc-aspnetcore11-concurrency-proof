using System.Net;
using System.Threading.RateLimiting;

var verify = args.Contains("--verify");
var port = DemoHost.Port(args, 5128);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{(verify ? 0 : port)}");
using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
{
    PermitLimit = 2, QueueLimit = 1, QueueProcessingOrder = QueueProcessingOrder.OldestFirst
});
var work = new WorkGate();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // One partition for this process: all /work/limited calls share two permits.
    options.AddPolicy("bounded-work", _ => RateLimitPartition.Get("one-process", _ => limiter));
});
var app = builder.Build();
app.UseRouting();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/work/limited", async Task<IResult> (HttpContext context) => await work.Execute(context))
    .RequireRateLimiting("bounded-work");
app.MapGet("/work/unbounded", async Task<IResult> (HttpContext context) => await work.Execute(context));
var runner = new ConcurrencyProof(limiter, work);
DemoHost.Map(app, runner.RunAsync, "ASP.NET Core concurrency limiting", new
{
    permitLimit = 2, queueLimit = 1, queueProcessingOrder = "OldestFirst",
    candidate = "DNC-RECOVERY-20261002-C02",
    scope = "Migration target only; legacy ConcurrencyLimiter middleware and distributed limits are not tested."
});
await app.StartAsync();
runner.BaseUrl = app.Urls.Single();
await DemoHost.FinishAsync(app, verify, runner.RunAsync);

sealed class WorkGate
{
    private TaskCompletionSource gate = NewGate();
    private int active, maximum, started;
    public int Active => Volatile.Read(ref active);
    public int Maximum => Volatile.Read(ref maximum);
    public int Started => Volatile.Read(ref started);
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Reset(bool hold)
    {
        if (Active != 0) throw new InvalidOperationException("Work still active.");
        gate = NewGate();
        active = maximum = started = 0;
        if (!hold) Release();
    }
    public void Release() => gate.TrySetResult();
    public async Task<IResult> Execute(HttpContext context)
    {
        var count = Interlocked.Increment(ref active);
        Interlocked.Increment(ref started);
        int previous;
        do { previous = Maximum; if (previous >= count) break; }
        while (Interlocked.CompareExchange(ref maximum, count, previous) != previous);
        try
        {
            await gate.Task.WaitAsync(context.RequestAborted);
            return context.Request.Query.ContainsKey("fail")
                ? Results.StatusCode(500) : Results.Ok(new { activeAtEntry = count });
        }
        finally { Interlocked.Decrement(ref active); }
    }
}

sealed class ConcurrencyProof(ConcurrencyLimiter limiter, WorkGate work)
{
    public string BaseUrl { get; set; } = "";
    public async Task<ProofReport> RunAsync()
    {
        var report = new ProofReport("DNC-Concurrency-Migration-Proof");
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 16 })
        { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            // Negative control: the same gate can observe four truly overlapping requests.
            work.Reset(hold: true);
            var controls = Enumerable.Range(0, 4).Select(_ => client.GetAsync("/work/unbounded")).ToArray();
            await Until(() => work.Active == 4, "four unbounded requests enter");
            report.Check("Negative control observes four concurrent handlers", work.Maximum == 4,
                new { active = work.Active, maximum = work.Maximum });
            work.Release();
            foreach (var response in await Task.WhenAll(controls)) response.Dispose();

            // Hold actual handlers; do not rely on delays to create saturation.
            work.Reset(hold: true);
            var first = client.GetAsync("/work/limited");
            var second = client.GetAsync("/work/limited");
            await Until(() => work.Active == 2, "two permits occupied");
            var third = client.GetAsync("/work/limited");
            await Until(() => limiter.GetStatistics()?.CurrentQueuedCount == 1, "one request queued");
            report.Check("Two requests execute and one waits in the real limiter queue",
                work.Active == 2 && !third.IsCompleted,
                new { active = work.Active, queued = limiter.GetStatistics()?.CurrentQueuedCount });
            using var fourth = await client.GetAsync("/work/limited");
            report.Check("A full queue rejects the next request with HTTP 429",
                fourth.StatusCode == HttpStatusCode.TooManyRequests,
                new { status = (int)fourth.StatusCode, available = limiter.GetStatistics()?.CurrentAvailablePermits });
            report.Check("Rejected work never enters the handler", work.Started == 2,
                new { started = work.Started });
            report.Observations["saturation"] = new
            { active = work.Active, queued = limiter.GetStatistics()?.CurrentQueuedCount, rejectedStatus = (int)fourth.StatusCode };
            work.Release();
            var successes = await Task.WhenAll(first, second, third);
            report.Check("The two active requests and queued request all complete with HTTP 200",
                successes.All(x => x.StatusCode == HttpStatusCode.OK),
                successes.Select(x => (int)x.StatusCode).ToArray());
            foreach (var response in successes) response.Dispose();
            report.Check("Peak protected concurrency never exceeds two", work.Maximum == 2,
                new { maximum = work.Maximum });
            await Until(() => limiter.GetStatistics()?.CurrentAvailablePermits == 2, "permits returned");
            report.Check("All permits are returned after success", limiter.GetStatistics()?.CurrentAvailablePermits == 2,
                limiter.GetStatistics());

            var sequential = new List<int>();
            for (var i = 0; i < 5; i++)
            { using var response = await client.GetAsync("/work/limited"); sequential.Add((int)response.StatusCode); }
            report.Check("Five sequential requests succeed: concurrency is not a per-time-window quota",
                sequential.All(x => x == 200), sequential);
            using var failure = await client.GetAsync("/work/limited?fail=true");
            await Until(() => limiter.GetStatistics()?.CurrentAvailablePermits == 2, "permit returned after failure");
            report.Check("HTTP 500 handler completion returns its permit",
                (int)failure.StatusCode == 500 && limiter.GetStatistics()?.CurrentAvailablePermits == 2,
                new { status = (int)failure.StatusCode, available = limiter.GetStatistics()?.CurrentAvailablePermits });

            work.Reset(hold: true);
            var activeA = client.GetAsync("/work/limited");
            var activeB = client.GetAsync("/work/limited");
            await Until(() => work.Active == 2, "cancellation setup occupies permits");
            using var queuedCancellation = new CancellationTokenSource();
            var cancelled = client.GetAsync("/work/limited", queuedCancellation.Token);
            await Until(() => limiter.GetStatistics()?.CurrentQueuedCount == 1, "cancellable request queued");
            queuedCancellation.Cancel();
            var observedCancellation = false;
            try { using var unexpected = await cancelled; }
            catch (OperationCanceledException) { observedCancellation = true; }
            await Until(() => limiter.GetStatistics()?.CurrentQueuedCount == 0, "cancelled queue slot released");
            report.Check("Cancelling queued work removes it without entering the handler",
                observedCancellation && work.Started == 2,
                new { cancelled = observedCancellation, started = work.Started, queued = limiter.GetStatistics()?.CurrentQueuedCount });
            work.Release();
            foreach (var response in await Task.WhenAll(activeA, activeB)) response.Dispose();
            await Until(() => limiter.GetStatistics()?.CurrentAvailablePermits == 2, "post-cancellation permits returned");
            using var after = await client.GetAsync("/work/limited");
            report.Check("A new request succeeds after queued cancellation", after.StatusCode == HttpStatusCode.OK,
                new { status = (int)after.StatusCode });
        }
        catch (Exception ex) { report.Check("Proof completed without an unexpected error", false, ex.ToString()); }
        finally { work.Release(); }
        report.Scope = "Observed HTTP requests through the ASP.NET Core RateLimiting middleware, one process, two permits, one queue slot. No legacy middleware comparison, Azure, Docker or distributed limit proof.";
        return await report.SaveAsync();
    }
    private static async Task Until(Func<bool> predicate, string message)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { while (!predicate()) await Task.Delay(20, deadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException(message); }
    }
}
