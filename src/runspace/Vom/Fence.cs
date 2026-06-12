using System.Threading;

namespace Subsystem.Vom;

// The doorbell (VOM-SPEC Mailbox/Doorbell). A monotonic u64 timeline barrier — named for the
// D3D12/Vulkan fence it mirrors, NOT a counting Semaphore (resource counter) and NOT an Event
// (binary flip). Abstract base (no `I` prefix, per the Cutler naming law): CpuFence is the Tier-1
// backend today; VulkanFence (timeline semaphore) slots in at the GPU/NPU tier with the SAME surface
// and zero driver changes.
public abstract class Fence
{
    public abstract ulong CompletedValue { get; }
    public abstract void  Signal(ulong value);   // producer doorbell: advance the timeline
    public abstract void  Wait(ulong value);      // park until CompletedValue >= value (GPU queue-wait tier later)
    public abstract void  CpuWait(ulong value);   // OS-scheduler wait; final readback only
}

// Tier-1 CPU fence: an Interlocked u64 timeline + a futex-backed park. Monitor on Linux/Android is
// futex-backed, so Wait() parks the thread in the kernel (effectively zero-CPU) until Signal()
// advances the value. This is the correct primitive for CPU->CPU handoffs (e.g. \Capture\Mic -> Hb);
// reach for VulkanFence only when data crosses to the GPU/NPU.
public sealed class CpuFence : Fence
{
    private long _value;
    private readonly object _gate = new();

    public override ulong CompletedValue => (ulong)Interlocked.Read(ref _value);

    public override void Signal(ulong value)
    {
        lock (_gate)
        {
            if ((long)value > _value) _value = (long)value;
            Monitor.PulseAll(_gate);
        }
    }

    public override void Wait(ulong value)
    {
        lock (_gate)
        {
            while ((ulong)_value < value) Monitor.Wait(_gate);
        }
    }

    public override void CpuWait(ulong value) => Wait(value);
}
