using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LM = Com.Google.AI.Edge.Litertlm;

namespace Subsystem.HeuristicBroker
{
    // The agent's tool surface — the deterministic OS verbs the Heuristic Broker (Hb) is allowed to drive
    // (VOM-SPEC §1a: the Hb brokers heuristic intent → deterministic paths). Each tool is a LiteRT-LM
    // `OpenApiTool`: the engine injects its declaration as a Gemma 4 `<|tool>` block, parses the model's
    // `<|tool_call>`, calls Execute(), and feeds the `<|tool_response>` back — natively, with
    // automaticToolCalling. We do NOT hand-parse tool sentinels and we do NOT use M.E.AI's invoker.
    //
    // NT-faithful: a cmdlet's manifest IS its agent-tool schema, so the tool surface is projected
    // ENTIRELY from the Cm registry — one truth. Definitions live in DATA (shell/agent-tools.json), each a
    // \Capability\AgentTool\* record whose `command` runs in the runspace; `run_powershell` is the universal
    // escape hatch (all of pwsh + the Android cmdlets behind one tool). NO tools are hardcoded in C#.

    public enum AgentDeltaKind { Token, Think, ToolCall, ToolResult, Error }

    // One streamed event of an agent turn. Token/Think carry visible/thinking text; ToolCall/ToolResult
    // carry a tool name + a JSON payload so the UI can card the activity. An Error delta carries the
    // §3.1 structured fault record; Text duplicates its NativeDetail for display-only consumers.
    public readonly record struct AgentDelta(AgentDeltaKind Kind, string Text, string? Name = null, HbFault? Fault = null);

    // Sink a tool writes its call/result into so the turn's stream can surface tool activity as it happens.
    public interface IToolEventSink { void Report(AgentDelta delta); }

    // A C#-backed LiteRT-LM OpenApiTool. `descriptionJson` is the Gemini function-declaration JSON
    // ({name,description,parameters:{type,properties,required}}); `run` receives the raw args JSON the
    // model produced and returns a JSON result string. Reports its own invocation to the sink.
    public sealed class DeviceTool : Java.Lang.Object, LM.IOpenApiTool
    {
        private readonly string _name;
        private readonly string _descriptionJson;
        private readonly Func<string, string> _run;
        private readonly IToolEventSink _sink;

        public DeviceTool(string name, string descriptionJson, Func<string, string> run, IToolEventSink sink)
        {
            _name = name;
            _descriptionJson = descriptionJson;
            _run = run;
            _sink = sink;
        }

        public string Name => _name;

        // Kotlin `getToolDescriptionJsonString()` → C# property (getX() binds to a property).
        public string ToolDescriptionJsonString => _descriptionJson;

        // Kotlin `execute(String): String`. Called on the engine thread during generation when
        // automaticToolCalling is on. Never throws across JNI — failures degrade to a JSON error object.
        public string Execute(string paramsJson)
        {
            _sink.Report(new AgentDelta(AgentDeltaKind.ToolCall, paramsJson ?? "{}", _name));
            string result;
            try { result = _run(paramsJson ?? "{}") ?? "null"; }
            catch (Exception ex)
            {
                result = "{\"error\":\"" + (ex.Message ?? "tool failed").Replace("\"", "'") + "\"}";
                try { Subsystem.Dg.Log("hb", $"tool {_name} failed: {ex.Message}"); } catch { }
            }
            _sink.Report(new AgentDelta(AgentDeltaKind.ToolResult, result, _name));
            return result;
        }
    }

    public static class AgentTools
    {
        // Builds the tool providers for a turn, wiring each tool's events to `sink`. Returned as a Java list
        // ready for ConversationConfig. ALL tools are projected from the Cm registry — a capability whose
        // manifest declares an `agentTool` block becomes a tool by construction (the manifest IS
        // the agent-tool schema). Definitions live in data (shell/agent-tools.json), never hardcoded here.
        public static Android.Runtime.JavaList<LM.ToolProvider> Build(IToolEventSink sink)
        {
            var list = new Android.Runtime.JavaList<LM.ToolProvider>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in RegistryTools(sink)) { if (seen.Add(t.Name)) list.Add(LM.ToolKt.Tool(t)); }
            try { Subsystem.Dg.Log("hb", $"agent tools: {seen.Count} ({string.Join(",", seen)})"); } catch { }
            return list;
        }

        // Tools projected from the Cm registry. Any enabled capability whose ManifestJson contains an
        // `agentTool` object ({name, description, parameters, command}) is surfaced as a tool. The tool runs
        // the declared PowerShell `command` in the on-device runspace; the model's args JSON is exposed to
        // that command as `$ToolArgs` (e.g. command "Start-AndroidIntent -Package $ToolArgs.package").
        private static IEnumerable<DeviceTool> RegistryTools(IToolEventSink sink)
        {
            CapabilityRecordView[] recs;
            try { recs = Subsystem.Cm.Cm.List().Select(r => new CapabilityRecordView(r.ManifestJson, r.Enabled)).ToArray(); }
            catch { yield break; }

            foreach (var r in recs)
            {
                if (!r.Enabled || string.IsNullOrEmpty(r.ManifestJson)) continue;
                string name, decl, command;
                bool sensitive;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(r.ManifestJson!);
                    if (!doc.RootElement.TryGetProperty("agentTool", out var at) || at.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    name = at.TryGetProperty("name", out var nv) ? (nv.GetString() ?? "") : "";
                    command = at.TryGetProperty("command", out var cv) ? (cv.GetString() ?? "") : "";
                    if (name.Length == 0 || command.Length == 0) continue;
                    // A tool is "sensitive" (drives device hardware) if its manifest says so, or by the
                    // known-hardware name set. Sensitive tools fire only with possession of the consent
                    // capability — the authority gate between a stray tool_call and execution.
                    sensitive = (at.TryGetProperty("sensitive", out var sv) && sv.ValueKind == System.Text.Json.JsonValueKind.True)
                                || IsHardwareToolName(name);
                    // Clean function declaration for the model — name/description/parameters only (the
                    // `command` is our private execution detail and must NOT leak into the tool schema).
                    var description = at.TryGetProperty("description", out var dv) ? (dv.GetString() ?? "") : "";
                    var parameters = at.TryGetProperty("parameters", out var pv) ? pv.GetRawText() : "{\"type\":\"object\",\"properties\":{}}";
                    decl = "{\"name\":" + System.Text.Json.JsonSerializer.Serialize(name) +
                           ",\"description\":" + System.Text.Json.JsonSerializer.Serialize(description) +
                           ",\"parameters\":" + parameters + "}";
                }
                catch { continue; }
                var cmd = command;
                var toolName = name;
                var gated = sensitive;
                yield return new DeviceTool(name, decl,
                    p => {
                        // Possession gate (doctrine: a verb fires only with the capability). A sensitive
                        // hardware tool requires \Capability\Consent\Hardware Enabled — absent = denied, so
                        // a small model's spurious set_flashlight cannot reach the torch. Deny -> typed JSON
                        // error, recorded, NOT executed.
                        if (gated && !HardwareConsentGranted())
                        {
                            try { Subsystem.Dg.Log("hb", $"hardware tool '{toolName}' DENIED — \\Capability\\Consent\\Hardware not enabled"); } catch { }
                            return "{\"error\":\"hardware tool not consented\"}";
                        }
                        // Expose the model's args as $ToolArgs, then run the declared command.
                        var script = "$ToolArgs = @'\n" + (p ?? "{}") + "\n'@ | ConvertFrom-Json; " + cmd;
                        return Subsystem.SubsystemApi.ExecuteCommandAsJson(script).GetAwaiter().GetResult();
                    }, sink);
            }
        }

        // The consent possession check: \Capability\Consent\Hardware must exist AND be enabled. Absent or
        // disabled -> denied (safe default; no seeding — the torch starts un-grantable). Mirrors the
        // Firewall zone gate (absent -> deny) and Rs.Granted (Get(...) is { Enabled: true }).
        private static bool HardwareConsentGranted()
            => Subsystem.Cm.Cm.Get("\\Capability\\Consent\\Hardware") is { Enabled: true };

        // Known device-hardware tools that are sensitive by name even if a manifest omits `sensitive:true`.
        private static bool IsHardwareToolName(string name)
            => string.Equals(name, "set_flashlight", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "vibrate", StringComparison.OrdinalIgnoreCase);

        private readonly record struct CapabilityRecordView(string? ManifestJson, bool Enabled);

    }
}
