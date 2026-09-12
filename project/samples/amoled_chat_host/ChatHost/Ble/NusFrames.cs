using System.Text;

namespace ChatHost.Ble;

/// <summary>
/// Wire format helpers for the NUS link (see PROTOCOL.md).
///   Text lines  : one ASCII tag + space + JSON + '\n'   (S/E host→device HUD, H host→device hello,
///                 A host→device answer, R device→host request)
///   Binary frame: 0xA5 | id(1) | seq(2 LE) | payload   (device→host audio chunk, one notification each)
/// </summary>
public static class NusFrames
{
    public const byte AudioMagic = 0xA5;

    public static bool IsAudioFrame(ReadOnlySpan<byte> pkt) => pkt.Length >= 4 && pkt[0] == AudioMagic;

    public static (int id, int seq, ReadOnlyMemory<byte> payload) ParseAudio(byte[] pkt)
        => (pkt[1], pkt[2] | (pkt[3] << 8), new ReadOnlyMemory<byte>(pkt, 4, pkt.Length - 4));

    /// <summary>Split text into pieces whose UTF-8 encoding is at most maxBytes, never cutting a code point.</summary>
    public static List<string> SplitUtf8(string text, int maxBytes)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(text)) { parts.Add(""); return parts; }
        var sb = new StringBuilder();
        int bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int n = rune.Utf8SequenceLength;
            if (bytes + n > maxBytes && sb.Length > 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                bytes = 0;
            }
            sb.Append(rune.ToString());
            bytes += n;
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }
}
