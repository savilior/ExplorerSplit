using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Explorer2.UI;

/// <summary>
/// Reads the tour's lines aloud. A line is played from its recording in Tour\voice\&lt;language&gt;
/// (rendered offline with a neural voice by tools\render-tour-voice.py) when the recording was made
/// from the line's current text; otherwise, for example in a translation without recordings or after
/// a line was edited, the Windows voice for the language reads it. <see cref="SpeakAsync"/> completes
/// when the line has been spoken, which keeps the tour's actions in step with the voice.
/// </summary>
internal sealed class TourVoice : IDisposable
{
    private readonly string? _folder;
    private readonly Dictionary<string, string> _recorded = new(StringComparer.Ordinal);
    private readonly SpeechSynthesizer? _synth;
    private readonly MediaPlayer? _player;
    private TaskCompletionSource? _ended;

    public TourVoice(string language)
    {
        LoadRecordings(language, out _folder);
        try
        {
            IReadOnlyList<VoiceInformation> voices = SpeechSynthesizer.AllVoices;
            string primary = language.Split('-')[0] + "-";
            VoiceInformation? voice =
                voices.FirstOrDefault(v => v.Language.Equals(language, StringComparison.OrdinalIgnoreCase)) ??
                voices.FirstOrDefault(v => v.Language.StartsWith(primary, StringComparison.OrdinalIgnoreCase));
            if (voice is not null)
            {
                _synth = new SpeechSynthesizer { Voice = voice };
            }
        }
        catch
        {
            _synth = null;
        }

        if (_synth is null && _recorded.Count == 0)
        {
            return; // no voice for this language: captions only
        }

        try
        {
            _player = new MediaPlayer { AutoPlay = false };
            _player.MediaEnded += (_, _) => _ended?.TrySetResult();
            _player.MediaFailed += (_, _) => _ended?.TrySetResult();
        }
        catch
        {
            _player = null;
        }
    }

    public bool IsAvailable => _player is not null;

    /// <summary>
    /// Reads Tour\voice\&lt;language&gt;\voice.json: for each line id, a hash of the text its
    /// recording was made from.
    /// </summary>
    private void LoadRecordings(string language, out string? folder)
    {
        folder = null;
        try
        {
            string root = Path.Combine(AppContext.BaseDirectory, "Tour", "voice");
            foreach (string name in new[] { language, language.Split('-')[0] })
            {
                string candidate = Path.Combine(root, name);
                string manifest = Path.Combine(candidate, "voice.json");
                if (!File.Exists(manifest))
                {
                    continue;
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("lines", out JsonElement lines) && lines.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in lines.EnumerateObject())
                    {
                        _recorded[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                folder = candidate;
                return;
            }
        }
        catch
        {
            _recorded.Clear();
            folder = null;
        }
    }

    /// <summary>The same hash as tools\render-tour-voice.py: SHA-256 of the UTF-8 text, first 16 hex digits.</summary>
    private static string TextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>The recording of line <paramref name="key"/>, if it was made from <paramref name="text"/>.</summary>
    private string? Recording(string key, string text)
    {
        if (_folder is null || !_recorded.TryGetValue(key, out string? hash) || hash != TextHash(text))
        {
            return null;
        }

        string path = Path.Combine(_folder, key + ".mp3");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Speaks line <paramref name="key"/>; completes when it was spoken, or at once on <see cref="Stop"/>.</summary>
    public async Task SpeakAsync(string key, string text, Func<Task> beforePlay, CancellationToken token)
    {
        if (_player is null)
        {
            return;
        }

        MediaSource source;
        if (Recording(key, text) is { } path)
        {
            source = MediaSource.CreateFromUri(new Uri(path));
        }
        else if (_synth is not null)
        {
            SpeechSynthesisStream stream = await _synth.SynthesizeTextToStreamAsync(text);
            source = MediaSource.CreateFromStream(stream, stream.ContentType);
        }
        else
        {
            return; // no recording and no Windows voice for this line
        }

        token.ThrowIfCancellationRequested();
        await beforePlay(); // waits while the tour is paused

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ended = ended;
        _player.Source = source;
        _player.Play();
        using (token.Register(Stop))
        {
            await ended.Task;
        }

        token.ThrowIfCancellationRequested();
    }

    public void Pause()
    {
        if (_ended is { Task.IsCompleted: false })
        {
            _player?.Pause();
        }
    }

    public void Resume()
    {
        if (_ended is { Task.IsCompleted: false })
        {
            _player?.Play();
        }
    }

    /// <summary>Stops the line being spoken (its <see cref="SpeakAsync"/> completes).</summary>
    public void Stop()
    {
        _player?.Pause();
        _ended?.TrySetResult();
    }

    public void Dispose()
    {
        Stop();
        _player?.Dispose();
        _synth?.Dispose();
    }
}
