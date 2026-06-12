using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Android.Content;
using LM = Com.Google.AI.Edge.Litertlm;

namespace Subsystem.HeuristicBroker
{
    // Real LiteRT-LM backend for Gemma 4 E2B. Loads .litertlm natively via the Engine/Conversation API,
    // registers the agent's tools so the engine does NATIVE function calling (automaticToolCalling: the
    // engine injects `<|tool>` declarations, parses the model's `<|tool_call>`, runs the tool, feeds the
    // `<|tool_response>` back, and continues — see HeuristicBroker/AgentTools.cs), and streams the turn as
    // structured AgentDelta events (visible tokens, the thinking channel, and tool call/result activity).
    //
    // HW-accelerated: tries the GPU (OpenCL) backend first, falls back to CPU. Engine.Initialize() is heavy
    // (~10s) so all model work runs off the UI thread. This client is the IToolEventSink the tools report
    // into; per-turn it points the sink at the active stream channel.
    public sealed class LiteRtChatClient : IDisposable, IToolEventSink
    {
        private LM.Engine? _engine;
        private LM.Conversation? _conversation;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _turnGate = new(1, 1);
        private readonly string _modelPath;
        private readonly string? _cacheDir;
        private readonly string? _systemInstruction;
        private volatile bool _ready;
        private HbFault? _initFault;                 // §3.1: typed bring-up fault; null = serviceable
        private readonly string _unitId;             // the \Capability\Model leaf this engine serves
        private readonly string[] _admittedBackends; // §4: rungs admitted by Admission.Plan — never probed beyond
        private string _backendName = "loading…";   // honest until the engine inits (then = the real rung: GPU/CPU)

        // The active turn's event channel — the tool sink forwards into this. Set under _turnGate so only
        // one turn writes at a time (the native engine is single-threaded per conversation anyway).
        private ChannelWriter<AgentDelta>? _activeSink;

        public LiteRtChatClient(Context context, string modelPath, string unitId, string[] admittedBackends, string? systemInstruction = null)
        {
            _modelPath = modelPath;
            _cacheDir = context.CacheDir?.AbsolutePath;
            _systemInstruction = systemInstruction;
            _unitId = unitId;
            _admittedBackends = admittedBackends is { Length: > 0 } ? admittedBackends : new[] { "CPU" };
        }

        // IToolEventSink — a tool's Execute() reports its call/result here; merged into the turn stream.
        public void Report(AgentDelta delta) { try { _activeSink?.TryWrite(delta); } catch { } }

        // §4/§6: bring-up over the ADMITTED rungs only (Admission.Plan decided placement before this
        // call — accelerator OOM is native and uncatchable, so no speculative accelerator probes).
        // Managed init failures ladder down within the admitted set. Returns the typed fault, or
        // null when the engine is serviceable. Idempotent.
        public HbFault? BringUp()
        {
            if (_ready) return null;
            if (_initFault != null) return _initFault;
            lock (_gate)
            {
                if (_ready) return null;
                if (_initFault != null) return _initFault;

                string? lastDetail = null;
                foreach (var name in _admittedBackends)
                {
                    LM.Backend backend;
                    switch (name)
                    {
                        case "NPU": backend = new LM.Backend.NPU(); break;
                        case "GPU": backend = new LM.Backend.GPU(); break;
                        default:    backend = new LM.Backend.CPU(); break;
                    }
                    try
                    {
                        var cfg = new LM.EngineConfig(_modelPath, backend, null, null, null, null, _cacheDir);
                        var engine = new LM.Engine(cfg);
                        engine.Initialize();

                        // Build the conversation WITH the agent's tools registered. automaticToolCalling
                        // defaults true: the engine runs the full tool loop natively. Tools report their
                        // activity into THIS client (the sink), which the active turn drains.
                        var tools = AgentTools.Build(this);
                        var sysContents = string.IsNullOrEmpty(_systemInstruction) ? null : MakeContents(_systemInstruction!);
                        var initialMessages = new Android.Runtime.JavaList<LM.Message>();
                        var convCfg = sysContents == null
                            ? new LM.ConversationConfig(MakeContents(""), initialMessages, tools)
                            : new LM.ConversationConfig(sysContents, initialMessages, tools);
                        _conversation = engine.CreateConversation(convCfg);

                        // §6(d): liveness verification — the conversation object must exist before the
                        // unit is published as serviceable. (A generation probe costs a prefill; the
                        // structural check is the zero-cost verification this layer can honestly make.)
                        if (_conversation == null)
                        {
                            try { engine.Close(); } catch { }
                            lastDetail = "CreateConversation returned null";
                            Subsystem.Dg.Log("engine", $"BRINGUP {_unitId} {name}: conversation null");
                            continue;
                        }

                        _engine = engine;
                        _backendName = name;
                        _ready = true;
                        Subsystem.Dg.Log("engine", $"BRINGUP {_unitId} verified on {name} (tools: native)");
                        return null;
                    }
                    catch (Exception ex)
                    {
                        // The single point (§3.1) where bring-up exception text is read: journaled and
                        // retained only as opaque NativeDetail.
                        lastDetail = ex.Message;
                        Subsystem.Dg.Log("engine", $"BRINGUP {_unitId} {name} failed: {ex.Message}");
                    }
                }

                _initFault = new HbFault(HbFaultClass.BringUpFailed, _unitId,
                    string.Join("/", _admittedBackends), lastDetail ?? "no admitted backend initialized");
                return _initFault;
            }
        }

        private void EnsureEngine() => BringUp();

        // Reach Contents.Companion.of(String) via JNI — the binding didn't expose a static accessor.
        private static LM.Contents MakeContents(string text)
        {
            IntPtr cls = Android.Runtime.JNIEnv.FindClass("com/google/ai/edge/litertlm/Contents");
            IntPtr fid = Android.Runtime.JNIEnv.GetStaticFieldID(cls, "Companion", "Lcom/google/ai/edge/litertlm/Contents$Companion;");
            IntPtr comp = Android.Runtime.JNIEnv.GetStaticObjectField(cls, fid);
            var companion = Java.Lang.Object.GetObject<LM.Contents.Companion>(comp, Android.Runtime.JniHandleOwnership.TransferLocalRef)!;
            return companion.Of(text);
        }

        public bool IsReady => _ready;
        public string BackendName => _backendName;
        public string UnitId => _unitId;

        // §3: serviceability — true only when bring-up verified and no fault is recorded. The §6
        // acquisition path consults this before publication and before every dispatch.
        public bool IsAlive => _ready && _initFault == null && _conversation != null;
        public HbFault? InitFault => _initFault;

        // Stream one turn as structured events. Visible text and the thinking channel are split out of the
        // model's text stream; tool call/result events arrive from the sink as the engine runs them.
        public async IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[]? audioBytes, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var fault = BringUp();
            if (fault != null)
            {
                yield return new AgentDelta(AgentDeltaKind.Error, fault.NativeDetail, Fault: fault);
                yield break;
            }

            var channel = Channel.CreateUnbounded<AgentDelta>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            var splitter = new ThinkingSplitter(channel.Writer);
            var callback = new StreamingCallback(splitter, channel.Writer, _unitId, _backendName);

            await _turnGate.WaitAsync(ct);
            _activeSink = channel.Writer;     // tools now report into this turn
            using var ctReg = ct.Register(() => { try { _conversation?.CancelProcess(); } catch { } });
            try
            {
                var kwargs = new Dictionary<string, Java.Lang.Object>();
                if (audioBytes != null && audioBytes.Length > 0) kwargs["audio"] = Java.Nio.ByteBuffer.Wrap(audioBytes);

                try { _conversation!.SendMessageAsync(prompt, callback, kwargs); }
                catch (Exception ex) { channel.Writer.TryComplete(ex); }

                await foreach (var delta in channel.Reader.ReadAllAsync(ct))
                    yield return delta;
            }
            finally { _activeSink = null; _turnGate.Release(); }
        }

        // Bridges the native MessageCallback to the turn channel. OnMessage delivers cumulative text;
        // the splitter diffs it and classifies into visible tokens vs the thinking channel.
        // OnError is the JNI boundary's single classification point (§3.1): native diagnostic text is
        // read HERE, mapped into the fault taxonomy, and carried onward only as opaque NativeDetail.
        private sealed class StreamingCallback : Java.Lang.Object, LM.IMessageCallback
        {
            private readonly ThinkingSplitter _splitter;
            private readonly ChannelWriter<AgentDelta> _writer;
            private readonly string _unitId;
            private readonly string _backend;
            public StreamingCallback(ThinkingSplitter splitter, ChannelWriter<AgentDelta> writer, string unitId, string backend)
            { _splitter = splitter; _writer = writer; _unitId = unitId; _backend = backend; }
            public void OnMessage(LM.Message message) { try { _splitter.Push(message?.ToString() ?? ""); } catch { } }
            public void OnDone() { _splitter.Flush(); _writer.TryComplete(); }
            public void OnError(Java.Lang.Throwable throwable)
            {
                var detail = throwable?.Message ?? "native streaming error";
                var cls = detail.Contains("not alive", StringComparison.OrdinalIgnoreCase) ? HbFaultClass.ConversationDefunct
                        : detail.Contains("cancel", StringComparison.OrdinalIgnoreCase)    ? HbFaultClass.DecodeCancelled
                        : HbFaultClass.DecodeFaulted;
                _writer.TryComplete(new HbFaultException(new HbFault(cls, _unitId, _backend, detail)));
            }
        }

        // Splits cumulative model text into a visible-answer stream and a thinking stream. Gemma 4 emits
        // its reasoning inside `<|channel>` … `<channel|>` (the chat template's thinking channel; enabled by
        // the `<|think|>` system prefix). We track how much of each we've emitted and push only new suffixes,
        // hiding a half-arrived marker so partial tokens never flash in the UI.
        private sealed class ThinkingSplitter
        {
            private const string Open = "<|channel>";
            private const string Close = "<channel|>";
            private readonly ChannelWriter<AgentDelta> _w;
            private int _emittedThink, _emittedAnswer;
            private string _buf = "";
            public ThinkingSplitter(ChannelWriter<AgentDelta> w) { _w = w; }

            public void Push(string chunk)
            {
                if (string.IsNullOrEmpty(chunk)) return;
                chunk = CleanText(chunk);
                // The native callback may deliver cumulative OR incremental text — normalize to cumulative.
                _buf = chunk.StartsWith(_buf, StringComparison.Ordinal) ? chunk : _buf + chunk;
                string full = _buf;
                string answer, think;
                int o = full.IndexOf(Open, StringComparison.Ordinal);
                if (o < 0) { answer = full; think = ""; }
                else
                {
                    int ts = o + Open.Length;
                    int c = full.IndexOf(Close, ts, StringComparison.Ordinal);
                    if (c < 0) { think = full.Substring(ts); answer = full.Substring(0, o); }
                    else { think = full.Substring(ts, c - ts); answer = full.Substring(0, o) + full.Substring(c + Close.Length); }
                }
                // Don't emit a trailing partial of either marker as visible text.
                answer = TrimTrailingPartialMarker(answer);

                if (think.Length > _emittedThink) { _w.TryWrite(new AgentDelta(AgentDeltaKind.Think, think.Substring(_emittedThink))); _emittedThink = think.Length; }
                if (answer.Length > _emittedAnswer) { _w.TryWrite(new AgentDelta(AgentDeltaKind.Token, answer.Substring(_emittedAnswer))); _emittedAnswer = answer.Length; }
            }

            public void Flush() { }

            // If the tail looks like the start of "<|channel>" or "<channel|>", hold it back.
            private static string TrimTrailingPartialMarker(string s)
            {
                foreach (var mk in new[] { Open, Close })
                    for (int len = Math.Min(mk.Length - 1, s.Length); len > 0; len--)
                        if (s.EndsWith(mk.Substring(0, len), StringComparison.Ordinal)) return s.Substring(0, s.Length - len);
                return s;
            }
        }

        // Message.ToString() may be a JsonMessage dump; pull the text out if so. MUST NOT trim — streaming
        // deltas carry significant leading spaces (the "▁am" token = " am").
        private static string CleanText(string raw)
        {
            var probe = raw.TrimStart();
            if (!probe.StartsWith("{") && !probe.StartsWith("[")) return raw;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("content", out var c))
                {
                    if (c.ValueKind == System.Text.Json.JsonValueKind.String) return c.GetString() ?? raw;
                    if (c.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var part in c.EnumerateArray())
                            if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                        if (sb.Length > 0) return sb.ToString();
                    }
                }
            }
            catch { }
            return raw;
        }

        // Snapshot of the last turn's native benchmark counters (drives tok/s in the UI + Measure cmdlet).
        public sealed record Benchmark(double InitSeconds, double TimeToFirstTokenSeconds,
            int PrefillTokens, double PrefillTokensPerSecond, int DecodeTokens, double DecodeTokensPerSecond);

        public Benchmark? GetBenchmark()
        {
            try
            {
                var b = _conversation?.BenchmarkInfo;
                if (b == null) return null;
                return new Benchmark(b.InitTimeInSecond, b.TimeToFirstTokenInSecond,
                    b.LastPrefillTokenCount, b.LastPrefillTokensPerSecond,
                    b.LastDecodeTokenCount, b.LastDecodeTokensPerSecond);
            }
            catch { return null; }
        }

        public void Dispose()
        {
            try { _conversation?.Close(); _engine?.Close(); } catch { }
        }
    }
}
