using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Soroush.Mediator;
using Soroush.Mediator.Abstractions;
using Soroush.Mediator.Diagnostics;

internal static class Benchmarks
{
    internal static async Task Run()
    {
        Console.WriteLine("Smoke benchmark; elapsed time and process allocations, not a load test.");
        using var raw = Services().BuildServiceProvider();
        using var disabled = Services().AddSoroushDiagnostics(o =>
        { o.EnableLogging = false; o.EnableTracing = false; o.EnableMetrics = false; }).BuildServiceProvider();
        using var enabled = Services().AddSoroushDiagnostics(o => o.EnableLogging = false).BuildServiceProvider();
        await Measure("raw", raw);
        await Measure("disabled", disabled);
        await Measure("no-listener", enabled);
        using var exporter = new CountingExporter();
        using var sdk = Sdk.CreateTracerProviderBuilder().AddSource(SoroushDiagnostics.ActivitySourceName)
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        await Measure("sdk-exporter", enabled);
        Console.WriteLine($"Exporter received {exporter.Count} activities.");
    }
    private static IServiceCollection Services() => new ServiceCollection().AddSoroushMediator(typeof(CreateOrderHandler).Assembly);
    private static async Task Measure(string mode, ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new CreateOrder(10);
        const int iterations = 10000;
        for (var i = 0; i < 1000; i++) await sender.Send(request);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) await sender.Send(request);
        clock.Stop();
        Console.WriteLine($"{mode,-14} request {clock.Elapsed.TotalMilliseconds / iterations * 1000:F2} us/op, {(GC.GetTotalAllocatedBytes(true) - allocated) / iterations} B/op");
        var stream = new OrderStream();
        for (var i = 0; i < 100; i++) await foreach (var _ in sender.CreateStream(stream)) { }
        allocated = GC.GetTotalAllocatedBytes(true);
        clock.Restart();
        for (var i = 0; i < 1000; i++) await foreach (var _ in sender.CreateStream(stream)) { }
        clock.Stop();
        Console.WriteLine($"{mode,-14} stream  {clock.Elapsed.TotalMilliseconds * 1000 / 1000:F2} us/enumeration, {(GC.GetTotalAllocatedBytes(true) - allocated) / 1000} B/enumeration");
    }
    private sealed class CountingExporter : BaseExporter<Activity>
    {
        public long Count;
        public override ExportResult Export(in Batch<Activity> batch)
        { foreach (var _ in batch) Interlocked.Increment(ref Count); return ExportResult.Success; }
    }
}
