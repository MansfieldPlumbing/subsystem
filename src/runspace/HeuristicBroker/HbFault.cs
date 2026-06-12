using System;

namespace Subsystem.HeuristicBroker
{
    // §3.1 — the inference subsystem's typed fault surface. Interior code branches on Class ONLY;
    // NativeDetail is opaque payload (journal/UI) and is interpreted nowhere past the JNI boundary.
    public enum HbFaultClass
    {
        AdmissionRefused,      // §4: budget evaluation refused every requested backend
        BringUpFailed,         // engine/conversation construction failed on all admitted rungs
        VerificationFailed,    // §6(d): bring-up reported success but the liveness check failed
        EngineReclaimed,       // the engine object was rundown (model switch, trim, teardown)
        ConversationDefunct,   // native conversation no longer serviceable (was: "Conversation is not alive")
        DecodeCancelled,       // in-flight decode interrupted via CancelProcess
        DecodeFaulted,         // native decode error other than cancellation
        BackendUnavailable,    // requested backend absent on this device (no OpenCL/NPU runtime)
    }

    public sealed record HbFault(HbFaultClass Class, string UnitId, string Backend, string NativeDetail);

    // Carrier for the fault record across throw boundaries. Message is for logs; consumers use Fault.
    public sealed class HbFaultException : Exception
    {
        public HbFault Fault { get; }
        public HbFaultException(HbFault fault)
            : base($"{fault.Class} [{fault.UnitId}/{fault.Backend}] {fault.NativeDetail}")
            => Fault = fault;
    }

    // §4 — admission control: backend placement is a single software decision made BEFORE load.
    // Accelerator out-of-memory is a native fault that is not deliverable to the managed handler,
    // so accelerated rungs are admitted only when the declared commit fits the budget; they are
    // never probed speculatively. Managed init failures may still ladder DOWN within the admitted
    // set (those are catchable); what is forbidden is attempting a rung the budget rules out.
    public static class Admission
    {
        // Conservative accelerator commit ceiling. Measured on the target device (11.1 GB RAM,
        // Adreno): the 2.59 GB unit initializes on GPU; the 3.66 GB unit faults natively. The
        // ceiling sits between, biased low — a refused accelerator costs latency; an admitted
        // oversized load costs the process.
        private const long AcceleratorCommitCeilingBytes = 3_000_000_000;

        // Ordered backend rungs admitted for this unit on this device. The verdict is journaled.
        public static string[] Plan(Android.Content.Context context, Subsystem.ModelSpec spec)
        {
            long commit = DeclaredCommitBytes(context, spec);
            string[] rungs;
            string verdict;
            if (spec.HeavyForLowRam && Subsystem.ModelCatalog.IsLowRamDevice(context))
            {
                rungs = new[] { "CPU" };
                verdict = "heavy unit on low-RAM device";
            }
            else if (commit > AcceleratorCommitCeilingBytes)
            {
                rungs = new[] { "CPU" };
                verdict = $"commit {commit} B exceeds accelerator ceiling {AcceleratorCommitCeilingBytes} B";
            }
            else
            {
                rungs = new[] { "NPU", "GPU", "CPU" };
                verdict = "within accelerator budget";
            }
            Subsystem.Dg.Log("engine", $"ADMISSION {spec.Id}: commit={commit} -> [{string.Join(",", rungs)}] ({verdict})");
            return rungs;
        }

        // Declared commit: the weight file length (weights dominate resident commit; runtime
        // overhead is absorbed by the conservative ceiling). MinBytes is the floor when the file
        // is not yet present.
        private static long DeclaredCommitBytes(Android.Content.Context context, Subsystem.ModelSpec spec)
        {
            try
            {
                var fi = new System.IO.FileInfo(Subsystem.ModelCatalog.LocalPath(context, spec));
                if (fi.Exists) return fi.Length;
            }
            catch { }
            return spec.MinBytes;
        }
    }
}
