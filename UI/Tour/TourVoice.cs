using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Explorer2.UI;

/// <summary>
/// Reads the tour's lines aloud with a Windows voice for the script's language (offline, no audio
/// files). <see cref="SpeakAsync"/> completes when the sentence has been spoken, which keeps the
/// tour's actions in step with the voice.
/// </summary>
internal sealed class TourVoice : IDisposable
{
    private readonly SpeechSynthesizer? _synth;
    private readonly MediaPlayer? _player;
    private TaskCompletionSource? _ended;

    public TourVoice(string language)
    {
        try
        {
            IReadOnlyList<VoiceInformation> voices = SpeechSynthesizer.AllVoices;
            string primary = language.Split('-')[0] + "-";
            VoiceInformation? voice =
                voices.FirstOrDefault(v => v.Language.Equals(language, StringComparison.OrdinalIgnoreCase)) ??
                voices.FirstOrDefault(v => v.Language.StartsWith(primary, StringComparison.OrdinalIgnoreCase));
            if (voice is null)
            {
                return; // no voice for this language: captions only
            }

            _synth = new SpeechSynthesizer { Voice = voice };
            _player = new MediaPlayer { AutoPlay = false };
            _player.MediaEnded += (_, _) => _ended?.TrySetResult();
            _player.MediaFailed += (_, _) => _ended?.TrySetResult();
        }
        catch
        {
            _synth = null;
            _player = null;
        }
    }

    public bool IsAvailable => _player is not null;

    /// <summary>Speaks <paramref name="text"/>; completes when it was spoken, or at once on <see cref="Stop"/>.</summary>
    public async Task SpeakAsync(string text, Func<Task> beforePlay, CancellationToken token)
    {
        if (_synth is null || _player is null)
        {
            return;
        }

        SpeechSynthesisStream stream = await _synth.SynthesizeTextToStreamAsync(text);
        token.ThrowIfCancellationRequested();
        await beforePlay(); // waits while the tour is paused

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ended = ended;
        _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
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

    /// <summary>Stops the sentence being spoken (its <see cref="SpeakAsync"/> completes).</summary>
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
