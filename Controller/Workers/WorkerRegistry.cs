using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using WhisperSubs.Configuration;
using WhisperSubs.Providers;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Builds the transcription worker pool from config (v4.0), backward-compatible via
    /// <see cref="WorkerPlan"/>: no config → one local worker (identical to today); a legacy single
    /// <c>RemoteWhisperApiUrl</c> → one remote worker (remote-only); an explicit <c>Workers</c> list →
    /// those + optionally the local host. Excluded from coverage — it news up providers from config, the
    /// same rationale as <see cref="SubtitleProviderFactory"/>. The composition decision (WorkerPlan) and
    /// the routing (WorkerScheduling) are the tested, pure parts.
    ///
    /// Dubtitles fork: every remote worker is a subgen instance (<see cref="SubgenProvider"/>) rather
    /// than a generic OpenAI-compatible endpoint — subgen selects its own model and doesn't support the
    /// job-timeout-scaling or upload-capping knobs RemoteWhisperProvider does, so those config values are
    /// intentionally unused for remote workers here.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Orchestration: constructs providers from config, like SubtitleProviderFactory")]
    public static class WorkerRegistry
    {
        public static IReadOnlyList<ITranscriptionWorker> BuildWorkers(PluginConfiguration config, ILoggerFactory loggerFactory)
        {
            var workers = new List<ITranscriptionWorker>();
            var plan = WorkerPlan.Decide(
                config.Workers?.Count ?? 0,
                !string.IsNullOrWhiteSpace(config.RemoteWhisperApiUrl),
                config.EnableLocalWorker);

            switch (plan.Source)
            {
                case WorkerSource.ExplicitList:
                    // ExplicitList is only returned by WorkerPlan.Decide when Workers.Count > 0, so it is non-null here.
                    // Collapse rows that point at the SAME physical endpoint first: whisper-server is single-request,
                    // so two rows (or an accidental duplicate) for one URL would give the pool two independent slots
                    // and oversubscribe that backend. CollapseByEndpoint keeps the first row and takes the min
                    // MaxConcurrency, so each physical endpoint becomes exactly one worker (v4.3.1).
                    var enabledRows = config.Workers!.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.ApiUrl));
                    foreach (var w in WorkerEndpointDedup.CollapseByEndpoint(enabledRows))
                    {
                        workers.Add(BuildRemote(
                            id: string.IsNullOrWhiteSpace(w.Id) ? w.ApiUrl : w.Id,
                            name: string.IsNullOrWhiteSpace(w.Name) ? w.ApiUrl : w.Name,
                            url: w.ApiUrl,
                            key: w.ApiKey ?? string.Empty,
                            model: string.IsNullOrWhiteSpace(w.Model) ? config.RemoteWhisperModel : w.Model,
                            maxConcurrency: w.MaxConcurrency,
                            costWeight: w.CostWeight,
                            canTranslate: w.CanTranslate,
                            maxUploadBytes: w.MaxUploadBytes,
                            uploadCodec: w.UploadCodec,
                            config: config,
                            loggerFactory: loggerFactory));
                    }
                    break;

                case WorkerSource.LegacyRemote:
                    // A pre-v4 single remote URL = the whole "remote" worker, remote-only.
                    workers.Add(BuildRemote(
                        id: "remote", name: "Remote",
                        url: config.RemoteWhisperApiUrl,
                        key: (config.RemoteWhisperApiKey ?? string.Empty).Trim(),
                        model: config.RemoteWhisperModel,
                        maxConcurrency: 1, costWeight: 0, canTranslate: true,
                        maxUploadBytes: 0, uploadCodec: null,
                        config: config, loggerFactory: loggerFactory));
                    break;
            }

            // Add the host's own local whisper unless the plan says otherwise. The fallback guarantees the
            // pool is never empty (e.g. an explicit list of all-disabled/blank workers with local off).
            if (plan.AddLocal || workers.Count == 0)
            {
                workers.Add(new TranscriptionWorker(
                    "local", "Local (this server)",
                    SubtitleProviderFactory.CreateLocal(config, loggerFactory),
                    new WorkerCapabilities { IsLocal = true, CostWeight = 0, MaxConcurrency = 1, CanTranslate = true }));
            }

            return workers;
        }

        // `model`, `maxUploadBytes`, `uploadCodec`, and `config`'s job-timeout-scaling fields are kept as
        // parameters (rather than trimming the signature) so ExplicitList/LegacyRemote call sites don't
        // need to change and so a future non-subgen remote provider can still be dropped in here — but
        // SubgenProvider itself doesn't use any of them: subgen picks its own model, and this fork keeps
        // SubgenProvider's fixed timeout/no upload-capping behavior rather than wiring those in.
        private static ITranscriptionWorker BuildRemote(
            string id, string name, string url, string key, string model,
            int maxConcurrency, double costWeight, bool canTranslate,
            long maxUploadBytes, string? uploadCodec,
            PluginConfiguration config, ILoggerFactory loggerFactory)
        {
            var provider = new SubgenProvider(
                url, key,
                loggerFactory.CreateLogger<SubgenProvider>());

            return new TranscriptionWorker(id, name, provider, new WorkerCapabilities
            {
                IsLocal = false,
                CostWeight = costWeight,
                MaxConcurrency = maxConcurrency < 1 ? 1 : maxConcurrency,
                CanTranslate = canTranslate
            });
        }
    }
}