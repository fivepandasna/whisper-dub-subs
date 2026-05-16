using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// Sends audio to a running subgen instance via its HTTP API.
    ///
    /// Subgen endpoints used:
    ///   POST /asr          – transcribe (or translate) an audio file
    ///   POST /detect-language – detect the spoken language of an audio chunk
    ///
    /// Subgen does NOT implement the OpenAI /v1/audio/transcriptions schema;
    /// it uses its own multipart form fields documented below.
    /// </summary>
    public class SubgenProvider : ISubtitleProvider
    {
        public string Name => "Subgen";

        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly ILogger _logger;

        // subgen /asr query-parameter values
        private const string OutputFormat = "srt";  // we always want SRT back

        public SubgenProvider(string baseUrl, string? apiKey, ILogger logger)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _logger = logger;

            _http = new HttpClient { Timeout = TimeSpan.FromHours(4) };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        // ── ISubtitleProvider ────────────────────────────────────────────

        /// <summary>
        /// Transcribes (or translates) <paramref name="audioFilePath"/> and returns SRT text.
        ///
        /// Subgen /asr query params (all optional except audio_file):
        ///   task           – "transcribe" | "translate"
        ///   language       – ISO 639-1 code, or omit for auto-detect
        ///   output         – always "srt"
        ///   encode         – true (subgen re-encodes internally as needed)
        ///   word_timestamps – false  (not needed for subtitle use)
        /// </summary>
        public async Task<string> TranscribeAsync(
            string audioFilePath,
            string language,
            CancellationToken cancellationToken,
            bool translate = false)
        {
            var task = translate ? "translate" : "transcribe";

            // Build query string
            var query = $"?task={task}&output={OutputFormat}&encode=true&word_timestamps=false";
            if (!string.IsNullOrEmpty(language)
                && !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                query += $"&language={Uri.EscapeDataString(language)}";
            }

            var url = $"{_baseUrl}/asr{query}";

            _logger.LogInformation(
                "[SubgenProvider] POST {Url} — file={File}, task={Task}, lang={Lang}",
                url, Path.GetFileName(audioFilePath), task,
                string.IsNullOrEmpty(language) ? "auto" : language);

            await using var fileStream = File.OpenRead(audioFilePath);
            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            // subgen expects the part name "audio_file"
            content.Add(fileContent, "audio_file", Path.GetFileName(audioFilePath));

            using var response = await _http.PostAsync(url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"[SubgenProvider] /asr returned {(int)response.StatusCode}: {body}");
            }

            var srt = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogInformation(
                "[SubgenProvider] Transcription complete — {Chars} chars of SRT received",
                srt.Length);

            return srt;
        }

        /// <summary>
        /// Detects the spoken language of a short audio chunk via subgen's /detect-language endpoint.
        ///
        /// Returns: (ISO 639-1 language code, confidence 0-1).
        ///
        /// Subgen /detect-language response (JSON):
        /// {
        ///   "detected_language": "English",
        ///   "language_code": "en",
        ///   "confidence": 0.97
        /// }
        /// </summary>
        public async Task<(string Language, float Probability)> DetectLanguageAsync(
            string audioFilePath,
            CancellationToken cancellationToken)
        {
            var url = $"{_baseUrl}/detect-language";

            _logger.LogDebug("[SubgenProvider] POST {Url} — file={File}", url,
                Path.GetFileName(audioFilePath));

            await using var fileStream = File.OpenRead(audioFilePath);
            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "audio_file", Path.GetFileName(audioFilePath));

            using var response = await _http.PostAsync(url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"[SubgenProvider] /detect-language returned {(int)response.StatusCode}: {body}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Prefer "language_code" (ISO 639-1); fall back to parsing "detected_language"
            string langCode = "und";
            if (root.TryGetProperty("language_code", out var lc) && !string.IsNullOrEmpty(lc.GetString()))
            {
                langCode = lc.GetString()!.ToLowerInvariant();
            }
            else if (root.TryGetProperty("detected_language", out var dl))
            {
                langCode = MapLanguageNameToCode(dl.GetString() ?? "");
            }

            float probability = 0f;
            if (root.TryGetProperty("confidence", out var conf))
            {
                probability = conf.TryGetSingle(out var p) ? p : 0f;
            }

            _logger.LogDebug("[SubgenProvider] Detected language={Lang} (p={Prob:F3})",
                langCode, probability);

            return (langCode, probability);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Maps a full English language name (as returned by subgen) to an ISO 639-1 code.
        /// Falls back to a simple lower-case first-word extraction.
        /// </summary>
        private static string MapLanguageNameToCode(string name)
        {
            return name.ToLowerInvariant() switch
            {
                "english"    => "en",
                "spanish"    => "es",
                "french"     => "fr",
                "german"     => "de",
                "italian"    => "it",
                "portuguese" => "pt",
                "russian"    => "ru",
                "japanese"   => "ja",
                "chinese"    => "zh",
                "korean"     => "ko",
                "arabic"     => "ar",
                "hindi"      => "hi",
                "polish"     => "pl",
                "dutch"      => "nl",
                "turkish"    => "tr",
                "swedish"    => "sv",
                "danish"     => "da",
                "finnish"    => "fi",
                "norwegian"  => "no",
                "czech"      => "cs",
                "romanian"   => "ro",
                "hungarian"  => "hu",
                "greek"      => "el",
                "hebrew"     => "he",
                "thai"       => "th",
                "ukrainian"  => "uk",
                "vietnamese" => "vi",
                "indonesian" => "id",
                "catalan"    => "ca",
                _            => Regex.Match(name, @"^\w+").Value.ToLowerInvariant()
            };
        }
    }
}