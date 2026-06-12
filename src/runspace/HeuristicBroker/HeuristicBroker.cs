using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;

namespace Subsystem;

// Hb — the Heuristic Broker (VOM-SPEC §1a). Brokers heuristic input (audio · vision · natural
// language) across the integrity boundary to deterministic OS paths (PowerShell cmdlets / VOM verbs).
//
// §3 (Resource Governance spec): the engine is an EXECUTIVE OBJECT, not a static. Each loaded engine
// is registered in the object manager at \Agent\Hb\Engine\<unitId> with a reclaim routine that closes
// the native engine. This type holds NO engine reference (SS003); every access resolves the active
// model's registry record to a namespace path and acquires through the handle table. A failed or
// rundown engine is unreachable by construction — the dispatch-against-disposed-engine condition (D2)
// is unrepresentable rather than handled.
public static class Hb
{
    public const string OwnerPath = "\\Agent\\Hb";
    private static readonly SemaphoreSlim _gate = new(1, 1);

    private static Subsystem.Vom.Owner EngineOwner =>
        Subsystem.Vom.Vom.CreateOwner(OwnerPath, maxBytes: 8L * 1024 * 1024 * 1024);

    private static string EnginePath(string unitId) => $"{OwnerPath}\\Engine\\{unitId}";

    // Resolve the active unit's engine through the handle table. False when no engine object exists
    // or the registered object is not serviceable. Never constructs.
    private static bool TryAcquire(Context ctx, out Subsystem.HeuristicBroker.Broker broker)
    {
        broker = null!;
        try
        {
            var spec = ModelCatalog.Active(ctx);
            if (Subsystem.Vom.Vom.TryGetByPath(EngineOwner, EnginePath(spec.Id), out var h) &&
                GCHandle.FromIntPtr(h.Resource).Target is Subsystem.HeuristicBroker.Broker b)
            {
                broker = b;
                return true;
            }
        }
        catch { }
        return false;
    }

    // Telemetry surface (Dg.Snapshot / the state texture): reads through the handle table.
    public static bool IsReady
    {
        get { try { return TryAcquire(Android.App.Application.Context, out var b) && b.IsAlive; } catch { return false; } }
    }

    public static string? BackendName
    {
        get { try { return TryAcquire(Android.App.Application.Context, out var b) ? b.BackendName : null; } catch { return null; } }
    }

    // Acquire the active unit's serviceable engine, constructing it under §4 admission control and
    // §6 verification when absent. Throws HbFaultException (typed, §3.1) on bring-up failure after
    // demoting the unit's registry record (§5).
    public static async Task<Subsystem.HeuristicBroker.Broker> GetAsync(Context ctx, Func<string, Task>? report = null, CancellationToken ct = default)
    {
        if (TryAcquire(ctx, out var live) && live.IsAlive) return live;

        await _gate.WaitAsync(ct);
        try
        {
            var spec = ModelCatalog.Active(ctx);
            var owner = EngineOwner;
            var path = EnginePath(spec.Id);

            // Re-check under the gate; rundown a registered-but-unserviceable object first.
            if (Subsystem.Vom.Vom.TryGetByPath(owner, path, out var h) &&
                GCHandle.FromIntPtr(h.Resource).Target is Subsystem.HeuristicBroker.Broker again)
            {
                if (again.IsAlive) return again;
                Subsystem.Vom.Vom.Close(owner, path);
                Dg.Log("engine", $"RUNDOWN {spec.Id}: unserviceable engine object reclaimed before rebuild");
            }

            var file = await ModelCatalog.EnsureAsync(ctx, spec, report ?? (_ => Task.CompletedTask), ct);
            var rungs = Subsystem.HeuristicBroker.Admission.Plan(ctx, spec);
            var broker = new Subsystem.HeuristicBroker.Broker(ctx, file, spec.Id, rungs);

            // Bring-up + verification (§6d) BEFORE publication into the namespace. Off the caller's
            // synchronization context — engine init is ~10 s of native work.
            var fault = await Task.Run(() => broker.BringUp(), ct);
            if (fault != null)
            {
                try { broker.Dispose(); } catch { }
                ModelCatalog.Demote(ctx, spec.Id, fault);
                throw new Subsystem.HeuristicBroker.HbFaultException(fault);
            }

            Subsystem.Vom.Vom.Register(owner, "Engine", broker,
                onReclaim: () => { try { broker.Dispose(); } catch { } },
                subdir: "Engine", name: spec.Id);
            Dg.Log("engine", $"PUBLISH {spec.Id} on {broker.BackendName} -> {path}");
            return broker;
        }
        finally { _gate.Release(); }
    }

    // One-shot generation (assist gesture, background tasks). Loads the engine on first cold call.
    public static async Task<string> GenerateAsync(Context ctx, string prompt, CancellationToken ct = default)
    {
        var a = await GetAsync(ctx, null, ct);
        var sb = new StringBuilder();
        await foreach (var t in a.SendMessageStreamAsync(prompt, ct: ct)) sb.Append(t);
        return sb.ToString().Trim();
    }

    // Rundown of every engine object (model switch, trim response, teardown). Reclaim closes the
    // native engines; weights/KV are released before any successor loads — two multi-GB units are
    // never resident at once. In-flight turns finish against their own acquired reference.
    public static void Reset()
    {
        var (n, bytes) = Subsystem.Vom.Vom.DropPrefix(OwnerPath + "\\Engine");
        if (n > 0) Dg.Log("engine", $"RUNDOWN engines: {n} handle(s) reclaimed");
    }
}
