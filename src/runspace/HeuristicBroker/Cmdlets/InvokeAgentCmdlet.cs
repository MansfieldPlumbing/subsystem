using System;
using System.Management.Automation;
using System.Threading;

namespace Subsystem.HeuristicBroker.Cmdlets;

[Cmdlet(VerbsLifecycle.Invoke, "Agent")]
public class InvokeAgentCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    public string Prompt { get; set; } = string.Empty;

    // Return the full reply as ONE pipeline string instead of streaming it to the console host. This
    // is the programmatic path (the Morse chat loop, scripts, remote PSRP callers): Host.UI output
    // doesn't cross the PSRP seam, a WriteObject string does.
    [Parameter] public SwitchParameter AsText { get; set; }

    protected override void ProcessRecord()
    {
        var ctx = Subsystem.MainActivity.Instance ?? Android.App.Application.Context;

        if (!AsText.IsPresent)
            Host.UI.WriteLine(ConsoleColor.DarkGray, Host.UI.RawUI.BackgroundColor, "[Agent Initializing...]");

        // Synchronously fetch the shared Heuristic Broker (Hb) instance
        var assistant = Subsystem.Hb.GetAsync(ctx).GetAwaiter().GetResult();

        if (!AsText.IsPresent)
            Host.UI.WriteLine(ConsoleColor.DarkGray, Host.UI.RawUI.BackgroundColor, "[Agent Thinking...]");

        var cts = new CancellationTokenSource();
        var stream = assistant.SendMessageStreamAsync(Prompt, ct: cts.Token);
        var enumerator = stream.GetAsyncEnumerator(cts.Token);
        var acc = AsText.IsPresent ? new System.Text.StringBuilder() : null;

        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var text = enumerator.Current;
                if (string.IsNullOrEmpty(text)) continue;
                if (acc != null) acc.Append(text);
                else Host.UI.Write(ConsoleColor.Cyan, Host.UI.RawUI.BackgroundColor, text);
            }
        }
        catch (Exception ex)
        {
            if (acc != null) throw;   // surface it as an error record to the programmatic caller
            Host.UI.WriteLine();
            Host.UI.WriteLine(ConsoleColor.Red, Host.UI.RawUI.BackgroundColor, $"[Error: {ex.Message}]");
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (acc != null) WriteObject(acc.ToString().Trim());
        else Host.UI.WriteLine();
    }
}
