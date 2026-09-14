using System.Runtime.InteropServices;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace AkkaHost.Agent.Os;

/// <summary>
/// Playing a file is the one thing in the agent that is genuinely per-OS, so it sits behind an
/// interface and the host picks an implementation at startup. Today only Windows has one; on
/// anything else the tools report that plainly rather than pretending.
/// </summary>
public interface IMusicPlayer
{
    bool Available { get; }
    string Status { get; }
    /// <summary>Starts a file, replacing whatever was playing.</summary>
    bool Play(string path, out string error);
    bool Stop();
    bool Pause();
    bool Resume();
    /// <summary>The file currently loaded, or null.</summary>
    string? NowPlaying { get; }
    /// <summary>Playback position and length in milliseconds, when the backend can tell.</summary>
    (int positionMs, int lengthMs) Progress { get; }
}

/// <summary>
/// WinRT's <see cref="MediaPlayer"/>: the same projection this host already uses for BLE, so it
/// costs no new dependency, it decodes whatever Windows can decode, and it gives real transport
/// control (play / pause / position).
///
/// The obvious alternative, winmm's MCI, was tried first and fails outright on this machine -
/// "open ... type mpegvideo" returns MCIERR_CANNOT_LOAD_DRIVER (277) because Windows 11 no longer
/// ships an MCI mp3 driver. MediaPlayer plays the same file without complaint.
/// </summary>
public sealed class WindowsMediaPlayer : IMusicPlayer
{
    private readonly object _lock = new();
    private MediaPlayer? _player;
    private string? _current;

    public bool Available => OperatingSystem.IsWindows();
    public string Status => Available ? "WinRT MediaPlayer" : "not available on this OS";
    public string? NowPlaying { get { lock (_lock) return _current; } }

    public bool Play(string path, out string error)
    {
        error = "";
        if (!Available)
        {
            error = "playback is Windows-only in this host";
            return false;
        }
        if (!File.Exists(path))
        {
            error = "file not found";
            return false;
        }

        lock (_lock)
        {
            try
            {
                var player = Ensure();
                // One MediaPlayer reused across tracks: creating one per track leaks audio
                // sessions, and the watch changes its mind often.
                player.Source = MediaSource.CreateFromUri(new Uri(path));
                player.Play();
                _current = path;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public bool Stop()
    {
        lock (_lock)
        {
            if (_player == null || _current == null) return false;
            _player.Pause();
            _player.Source = null;
            _current = null;
            return true;
        }
    }

    public bool Pause()
    {
        lock (_lock)
        {
            if (_player == null || _current == null) return false;
            _player.Pause();
            return true;
        }
    }

    public bool Resume()
    {
        lock (_lock)
        {
            if (_player == null || _current == null) return false;
            _player.Play();
            return true;
        }
    }

    public (int positionMs, int lengthMs) Progress
    {
        get
        {
            lock (_lock)
            {
                if (_player?.PlaybackSession is not { } session || _current == null) return (0, 0);
                return ((int)session.Position.TotalMilliseconds,
                    (int)session.NaturalDuration.TotalMilliseconds);
            }
        }
    }

    private MediaPlayer Ensure()
    {
        if (_player != null) return _player;
        _player = new MediaPlayer { AutoPlay = false };
        // A track that runs to its end is no longer "now playing", and the watch asks.
        _player.MediaEnded += (_, _) => { lock (_lock) _current = null; };
        return _player;
    }
}

/// <summary>What every non-Windows host gets until someone writes the backend for it.</summary>
public sealed class UnsupportedMusicPlayer : IMusicPlayer
{
    public bool Available => false;
    public string Status => $"no playback backend for {RuntimeInformation.OSDescription}";
    public string? NowPlaying => null;
    public (int positionMs, int lengthMs) Progress => (0, 0);

    public bool Play(string path, out string error)
    {
        error = Status;
        return false;
    }

    public bool Stop() => false;
    public bool Pause() => false;
    public bool Resume() => false;
}

public static class MusicPlayerFactory
{
    public static IMusicPlayer Create() =>
        OperatingSystem.IsWindows() ? new WindowsMediaPlayer() : new UnsupportedMusicPlayer();
}
