using Akka.Actor;
using Akka.Event;
using AkkaHost.Ble;

namespace AkkaHost.Actors;

/// <summary>
/// The Claude HUD channel: Claude Code's statusLine and hooks describe what a session is
/// doing, and the watch's HUD app renders it.
///
/// The device side needs nothing new - hud_ble consumes "S" and "E" lines itself, which is why
/// the HUD app keeps working unchanged. What changed is who writes them: this host owns the BLE
/// link now, so it has to carry this traffic as well as the two chat apps'. The hooks already
/// installed in ~/.claude keep posting to the same local port, so settings.json is untouched.
///
/// An actor rather than a direct write because the writes then serialise behind one mailbox
/// (a statusLine update and a hook event can arrive on different HTTP threads at once), and
/// because a later consumer - showing session state on AskBot's screen, say - subscribes here
/// instead of racing for the link.
/// </summary>
public sealed class HudActor : UntypedActor
{
    private readonly BleLink _link;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    /// <summary>Claude Code statusLine payload, verbatim.</summary>
    public sealed record Status(string Json);
    /// <summary>Claude Code hook payload, verbatim.</summary>
    public sealed record Event(string Json);

    public long Statuses, Events, Dropped;
    public string LastStatus = "";

    public HudActor(BleLink link) => _link = link;

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case Status status:
                LastStatus = status.Json;
                Statuses++;
                Send('S', status.Json);
                break;

            case Event hookEvent:
                Events++;
                Send('E', hookEvent.Json);
                break;

            default:
                Unhandled(message);
                break;
        }
    }

    private void Send(char tag, string json)
    {
        if (!_link.IsConnected)
        {
            // The watch is simply not in range; the HUD is a display, so dropping is right -
            // there is no value in replaying a status from ten minutes ago.
            Dropped++;
            return;
        }
        _ = _link.SendLineAsync($"{tag} {json}");
        _log.Debug("{0} line, {1} bytes", tag, json.Length);
    }
}
