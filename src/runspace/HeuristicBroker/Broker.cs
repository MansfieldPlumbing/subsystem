using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Subsystem.HeuristicBroker
{
    // Broker — the resident agent (AGENT-SPEC §13; the LOCKED name): the conversational face of the
    // Heuristic Broker (Hb). Owns the LiteRT-LM client and the persona; streams a turn as structured
    // AgentDelta events (tokens, thinking, tool activity). Tools are NATIVE LiteRT-LM function calls
    // (see AgentTools.cs) — no M.E.AI invoker, no hand-parsed sentinels.
    public class Broker : IDisposable
    {
        private readonly LiteRtChatClient _client;

        // Releases the engine + conversation (model switch / shutdown). Safe to call once; the
        // owner (Hb.Reset) swaps the reference out before disposing.
        public void Dispose() => _client.Dispose();

        // The persona. Tool *declarations* are injected by the engine (automaticToolCalling) — we do NOT
        // list a tool schema here. We only set voice + when to reach for tools. Kept short: E2B is small,
        // and a tight prompt tool-calls more reliably. `<|think|>` enables Gemma 4's thinking channel.
        private const string SystemPrompt =
@"You are Broker — a friendly, capable assistant living on the user's Android phone. You actually operate
the device through your tools: read the battery, toggle the flashlight, check memory, set volume, vibrate,
show notifications. Your most powerful tool is run_powershell: the phone IS a live PowerShell runspace
with hundreds of cmdlets, so when no specific tool fits, COMPOSE a command. Examples: Get-AndroidNetwork,
Get-AndroidStorage, Get-InstalledApp, Get-Clipboard, Send-Morse, Out-Speech, Get-Capability.

Each user message begins with a [HUD …] line — a live sitrep of the device (time, battery, network,
memory, your model). It is injected by the system, not typed by the user. TRUST it over your memory of
earlier turns, use it instead of calling a tool for those vitals, and never echo or mention it.

How to behave:
- When the user asks you to DO something or asks about the device's state, CALL the right tool — don't
  just describe the command. After it returns, tell the user the result in one short, warm sentence.
- REFLECT on what a tool returns. If a command errors or gives an unexpected result, read the error,
  adjust, and try a corrected command — don't stop after one failed attempt. You have the whole runspace.
- When you need something the named tools don't cover, reach for run_powershell rather than saying you
  can't — if a cmdlet might exist, try it (Get-Command shows what's available).
- When the user just chats or asks a general question, answer directly and briefly.
- If asked what you can do, explain plainly in a sentence or two (control the phone, check its state, and
  run any phone command) — no jargon.
Keep replies concise and natural. You are talking on a phone screen.";

        // Backend placement is decided by Admission.Plan (§4) BEFORE construction; the admitted
        // rungs arrive here as data. unitId names the \Capability\Model record this engine serves.
        public Broker(Android.Content.Context context, string modelPath, string unitId, string[] admittedBackends)
        {
            _client = new LiteRtChatClient(context, modelPath, unitId, admittedBackends, "<|think|>\n" + SystemPrompt);
        }

        public LiteRtChatClient.Benchmark? GetBenchmark() => _client.GetBenchmark();
        public string BackendName => _client.BackendName;
        public string UnitId => _client.UnitId;

        // §3/§6: serviceability surface — acquisition and verification consult these.
        public bool IsAlive => _client.IsAlive;
        public HbFault? BringUp() => _client.BringUp();

        // Structured stream — the /agent WS and the Invoke-Agent cmdlet consume this.
        public IAsyncEnumerable<AgentDelta> SendTurnAsync(string text, byte[]? audioData = null, CancellationToken ct = default)
            => _client.StreamTurnAsync(text, audioData != null && audioData.Length > 0 ? audioData : null, ct);

        // Back-compat plain-text stream (visible answer tokens only) for callers that don't need the
        // structured events (Hb.GenerateAsync one-shots, ss-ask). Thinking + tool chatter are dropped.
        public async IAsyncEnumerable<string> SendMessageStreamAsync(string text, byte[]? audioData = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var d in SendTurnAsync(text, audioData, ct))
                if (d.Kind == AgentDeltaKind.Token && !string.IsNullOrEmpty(d.Text))
                    yield return d.Text;
        }
    }
}
