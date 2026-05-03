using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NVorbis;

namespace Encore.Services;

// Energy-based onset detection + autocorrelation BPM detector. Background-threaded, disk-cached.
public sealed class BgmAnalysisService : IDisposable
{
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, float> _cache = new();
    private readonly object _saveLock = new();

    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly IPluginLog _log;

    public BgmAnalysisService(string configDir, IPluginLog log)
    {
        _log = log;
        _cachePath = Path.Combine(configDir, "bgm_bpm_cache.json");
        LoadCache();
    }

    public float? TryGetBpm(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return _cache.TryGetValue(key, out var bpm) ? bpm : null;
    }

    public void QueueScdFromDisk(string scdPath)
    {
        if (string.IsNullOrEmpty(scdPath)) return;
        if (_cache.ContainsKey(scdPath)) return;
        if (!File.Exists(scdPath)) return;

        if (!_inFlight.TryAdd(scdPath, 0)) return;
        Task.Run(() =>
        {
            try
            {
                byte[] scdBytes;
                try { scdBytes = File.ReadAllBytes(scdPath); }
                catch (Exception ex) { _log.Debug($"[BgmAnalysis] {scdPath} read failed: {ex.Message}"); return; }

                float bpm = 0f;

                // OGG Vorbis (SCD format 6)
                var ogg = ScdOggExtractor.ExtractFirstOgg(scdBytes);
                if (ogg != null && ogg.Length >= 128)
                {
                    bpm = AnalyzeOgg(ogg);
                }
                else
                {
                    // MS-ADPCM (SCD format 12, common for sound-effect swaps)
                    var adpcm = ScdAdpcmExtractor.ExtractFirstAdpcmAsMono(scdBytes, 60f);
                    if (adpcm.IsValid)
                    {
                        bpm = AnalyzePcm(adpcm.Mono, adpcm.SampleRate);
                    }
                    else
                    {
                        _log.Debug($"[BgmAnalysis] {scdPath}: no OGG and no ADPCM (unsupported format)");
                        return;
                    }
                }

                if (bpm > 0)
                {
                    _cache[scdPath] = bpm;
                    SaveCache();
                    _log.Debug($"[BgmAnalysis] {scdPath}: {bpm:F1} BPM");
                }
                else
                {
                    _log.Debug($"[BgmAnalysis] {scdPath}: no BPM detected");
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"[BgmAnalysis] {scdPath} failed: {ex.Message}");
            }
            finally
            {
                _inFlight.TryRemove(scdPath, out _);
            }
        });
    }

    public void QueueAnalysis(string key, byte[] oggBytes)
    {
        if (string.IsNullOrEmpty(key) || oggBytes == null || oggBytes.Length < 128) return;
        if (_cache.ContainsKey(key)) return;
        if (!_inFlight.TryAdd(key, 0)) return;

        Task.Run(() =>
        {
            try
            {
                var bpm = AnalyzeOgg(oggBytes);
                if (bpm > 0)
                {
                    _cache[key] = bpm;
                    SaveCache();
                    _log.Debug($"[BgmAnalysis] {key}: {bpm:F1} BPM");
                }
                else
                {
                    _log.Debug($"[BgmAnalysis] {key}: no BPM detected");
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"[BgmAnalysis] {key} failed: {ex.Message}");
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        });
    }

    public void ClearCache()
    {
        _cache.Clear();
        try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { }
    }

    public void Dispose()
    {
        SaveCache();
    }

    private float AnalyzeOgg(byte[] ogg)
    {
        const float secondsToAnalyze = 45f;
        try
        {
            using var ms = new MemoryStream(ogg);
            using var reader = new VorbisReader(ms);

            int sampleRate = reader.SampleRate;
            int channels = reader.Channels;
            if (sampleRate < 8000 || channels < 1)
            {
                _log.Debug($"[BgmAnalysis] bad OGG header: sr={sampleRate} ch={channels}");
                return 0f;
            }

            int totalSamplesWanted = (int)(sampleRate * secondsToAnalyze);
            var buffer = new float[Math.Min(totalSamplesWanted * channels, 1 << 24)];
            int read = reader.ReadSamples(buffer, 0, buffer.Length);
            if (read < sampleRate)
            {
                _log.Debug($"[BgmAnalysis] too-short decode: {read} samples ({(float)read / sampleRate:F1}s)");
                return 0f;
            }

            int frameCount = read / channels;
            var mono = new float[frameCount];
            if (channels == 1)
            {
                Array.Copy(buffer, mono, frameCount);
            }
            else
            {
                for (int i = 0; i < frameCount; i++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                        sum += buffer[i * channels + c];
                    mono[i] = sum / channels;
                }
            }

            return AnalyzePcm(mono, sampleRate);
        }
        catch (Exception ex)
        {
            _log.Warning($"[BgmAnalysis] OGG decode failed: {ex.Message}");
            return 0f;
        }
    }

    // windowed energy -> half-wave-rectified onset -> autocorrelation -> peak pick + tempo prior
    private float AnalyzePcm(float[] mono, int sampleRate)
    {
        if (mono == null || mono.Length < sampleRate || sampleRate < 8000) return 0f;

        float result = 0f;
        try
        {
            int frameCount = mono.Length;

            // 1024-sample window, ~23ms at 44.1kHz
            const int windowSize = 1024;
            int windowCount = frameCount / windowSize;
            if (windowCount < 80) return 0f;

            var energy = new float[windowCount];
            for (int w = 0; w < windowCount; w++)
            {
                float sumSq = 0f;
                int start = w * windowSize;
                int end = start + windowSize;
                for (int i = start; i < end; i++)
                    sumSq += mono[i] * mono[i];
                // log-compress so loud and quiet passages contribute equally
                energy[w] = MathF.Log(1f + sumSq);
            }

            var onset = new float[windowCount];
            for (int w = 1; w < windowCount; w++)
            {
                float d = energy[w] - energy[w - 1];
                onset[w] = d > 0 ? d : 0f;
            }

            // zero-center for autocorrelation
            float windowsPerSec = sampleRate / (float)windowSize;
            int smoothWin = (int)MathF.Max(8, windowsPerSec * 0.3f); // ~300ms
            var onsetNorm = new float[windowCount];
            float runSum = 0f;
            int half = smoothWin / 2;
            for (int w = 0; w < windowCount; w++)
            {
                int lo = Math.Max(0, w - half);
                int hi = Math.Min(windowCount - 1, w + half);
                float m = 0f;
                for (int k = lo; k <= hi; k++) m += onset[k];
                m /= (hi - lo + 1);
                onsetNorm[w] = onset[w] - m;
            }

            // autocorrelate over 60-210 BPM lags
            int minLag = (int)MathF.Max(1f, windowsPerSec * 60f / 210f);
            int maxLag = (int)MathF.Min(windowCount / 2, windowsPerSec * 60f / 60f);
            if (maxLag <= minLag + 2) return 0f;

            var correlation = new float[maxLag + 1];
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                float acc = 0f;
                int limit = windowCount - lag;
                for (int i = 0; i < limit; i++)
                    acc += onsetNorm[i] * onsetNorm[i + lag];
                correlation[lag] = acc;
            }

            // sub-harmonic summation (2L, 3L only) + Gaussian prior at 128 BPM.
            // do NOT add corr[L/2]: it would credit the slower candidate with the faster's energy
            // (causes half-tempo bias).
            var scored = new float[maxLag + 1];
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                float s = correlation[lag];
                int l2 = lag * 2;
                if (l2 <= maxLag) s += 0.6f * correlation[l2];
                int l3 = lag * 3;
                if (l3 <= maxLag) s += 0.4f * correlation[l3];

                float bpm = 60f * windowsPerSec / lag;
                float zPrior = (bpm - 128f) / 40f;
                float priorGain = MathF.Exp(-0.5f * zPrior * zPrior);
                s *= (0.5f + 0.5f * priorGain);

                scored[lag] = s;
            }

            int bestLag = -1;
            float bestScore = float.NegativeInfinity;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                if (scored[lag] > bestScore)
                {
                    bestScore = scored[lag];
                    bestLag = lag;
                }
            }
            if (bestLag <= 0 || bestScore <= 0f) return 0f;

            float lagF = bestLag;
            if (bestLag > minLag && bestLag < maxLag)
            {
                float ym1 = scored[bestLag - 1];
                float y0 = scored[bestLag];
                float yp1 = scored[bestLag + 1];
                float denom = ym1 - 2 * y0 + yp1;
                if (MathF.Abs(denom) > 1e-9f)
                {
                    float adj = 0.5f * (ym1 - yp1) / denom;
                    if (adj > -1f && adj < 1f) lagF = bestLag + adj;
                }
            }

            float detected = 60f * windowsPerSec / lagF;
            while (detected < 60f) detected *= 2f;
            while (detected > 200f) detected *= 0.5f;
            result = detected;
        }
        catch (Exception ex)
        {
            _log.Warning($"[BgmAnalysis] decode/analyze failure: {ex.Message}");
            return 0f;
        }

        return result;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, float>>(json);
            if (data == null) return;
            foreach (var (k, v) in data)
                if (v > 30f && v < 250f) _cache[k] = v;
            _log.Debug($"[BgmAnalysis] Loaded {_cache.Count} cached BPM entries");
        }
        catch (Exception ex)
        {
            _log.Warning($"[BgmAnalysis] Failed to load cache: {ex.Message}");
        }
    }

    private void SaveCache()
    {
        lock (_saveLock)
        {
            try
            {
                var snapshot = new Dictionary<string, float>(_cache);
                var json = JsonSerializer.Serialize(snapshot,
                    new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(_cachePath, json);
            }
            catch (Exception ex)
            {
                _log.Warning($"[BgmAnalysis] Failed to save cache: {ex.Message}");
            }
        }
    }
}
