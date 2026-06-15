using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace WhisperSubs.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string WhisperModelPath { get; set; } = "";
        public string WhisperBinaryPath { get; set; } = "";
        public bool EnableAutoGeneration { get; set; } = false;

        /// <summary>
        /// Default language for subtitle generation.
        /// "auto" = detect from audio stream metadata, fall back to whisper auto-detection.
        /// Any ISO 639-1 code (e.g. "es", "en", "fr") forces that language.
        /// </summary>
        public string DefaultLanguage { get; set; } = "auto";

        /// <summary>
        /// Controls whether to generate full subtitles, forced-only subtitles, or both.
        /// </summary>
        [JsonConverter(typeof(SubtitleModeConverter))]
        public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.Full;

        /// <summary>
        /// When enabled, music libraries are scanned and audio tracks receive
        /// .lrc lyrics files generated via whisper transcription.
        /// Experimental: whisper models are optimized for speech, not singing.
        /// </summary>
        public bool EnableLyricsGeneration { get; set; } = false;

        /// <summary>
        /// When enabled, generates English subtitles via whisper's translate task
        /// for media that lacks an English audio track.
        /// Only applies when SubtitleMode includes Full subtitles.
        /// </summary>
        public bool EnableTranslation { get; set; } = false;

        /// <summary>
        /// Number of threads for local whisper.cpp inference. 0 = whisper default (4).
        /// Not used when RemoteWhisperApiUrl is set.
        /// </summary>
        public int WhisperThreadCount { get; set; } = 0;

        /// <summary>
        /// Base URL of your subgen instance.
        /// Example: http://192.168.1.100:8000
        ///
        /// subgen exposes:
        ///   POST /asr              – transcription / translation
        ///   POST /detect-language  – language detection
        /// </summary>
        public string RemoteWhisperApiUrl { get; set; } = "";

        /// <summary>
        /// Not used by subgen (subgen selects its model via its own configuration).
        /// Kept for UI display purposes only.
        /// </summary>
        public string RemoteWhisperModel { get; set; } = "";

        /// <summary>
        /// Optional Bearer token if subgen is placed behind an authenticating reverse proxy.
        /// Leave empty for a standard local subgen deployment.
        /// </summary>
        public string RemoteWhisperApiKey { get; set; } = "";

        /// <summary>
        /// When enabled, subtitle generation pauses while any user is actively
        /// playing media and resumes automatically when playback stops.
        /// </summary>
        public bool PauseOnPlayback { get; set; } = false;

        /// <summary>
        /// Extra arguments appended to every whisper-cli invocation (space-separated).
        /// Only applies to local whisper-cli, not the remote API.
        /// Example: --max-len 47 --split-on-word
        /// </summary>
        public string CustomWhisperArgs { get; set; } = "";

        /// <summary>
        /// When enabled, subtitle start times are snapped forward to detected speech onsets
        /// so a subtitle no longer appears during the silence before its line is spoken.
        /// whisper.cpp emits gapless segments (next.start == prev.end); this re-introduces
        /// the natural gaps using FFmpeg silence detection. Local whisper-cli only.
        ///
        /// Note: this is the older energy-based fallback. When <see cref="EnableVad"/> is on
        /// (the default), whisper.cpp's native Silero VAD handles speech-onset gaps far more
        /// reliably and this FFmpeg pass is skipped.
        /// </summary>
        public bool AlignSubtitlesToSpeech { get; set; } = true;

        /// <summary>
        /// When enabled, whisper-cli runs with native Silero Voice Activity Detection
        /// (<c>--vad</c>), which makes the emitted subtitles start at real speech onset instead
        /// of during the preceding silence (whisper.cpp otherwise chains segments gaplessly).
        /// Requires the Silero VAD model, which the plugin auto-downloads. Local whisper-cli only.
        /// </summary>
        public bool EnableVad { get; set; } = true;

        /// <summary>
        /// Filesystem path to the Silero VAD ggml model used by <see cref="EnableVad"/>.
        /// Set automatically when the plugin downloads the VAD model; can be overridden to point
        /// at a custom Silero VAD ggml file. When empty, the plugin looks in its default
        /// vad/ data directory and downloads the model on first use if missing.
        /// </summary>
        public string VadModelPath { get; set; } = "";

        /// <summary>
        /// When enabled, compensates for a container audio start-time offset (the audio stream
        /// not starting at 0:00) by shifting all subtitle timestamps forward by that offset,
        /// keeping subtitles in sync with playback. Local whisper-cli only.
        /// </summary>
        public bool CompensateAudioOffset { get; set; } = true;

        public List<string> EnabledLibraries { get; set; } = new List<string>();

        /// <summary>
        /// Optional webhook URL to call when the scheduled task completes.
        /// </summary>
        public string TaskCompletionWebhookUrl { get; set; } = "";

        public PluginConfiguration()
        {
        }
    }
}
