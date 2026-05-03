using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;

namespace Encore.Services;

// reads game BGM (Orchestrion approach) -> Lumina BGM sheet -> SCD path -> BgmAnalysisService
public sealed class BgmTrackerService : IDisposable
{
    private readonly ISigScanner _sigScanner;
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;
    private readonly BgmAnalysisService _analysis;

    // Orchestrion sig. base -> manager, *(*base + 0xC0) = 12-entry BGMScene array
    private const string BgmBaseSig =
        "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 51 83 78 08 0B";

    private nint _baseAddress;
    private bool _sigResolved;

    private readonly ConcurrentDictionary<ushort, string?> _rowToScd = new();

    private ushort _lastSongId;
    private string? _currentScdPath;
    private float? _currentBpm;

    // takes precedence over game BGM while a preset's mod swaps in its own music
    private string? _modScdPath;

    public BgmTrackerService(ISigScanner sigScanner, IDataManager dataManager,
        IPluginLog log, BgmAnalysisService analysis)
    {
        _sigScanner = sigScanner;
        _dataManager = dataManager;
        _log = log;
        _analysis = analysis;

        ResolveSig();
    }

    public float? CurrentBpm
    {
        get
        {
            if (!string.IsNullOrEmpty(_modScdPath))
            {
                var modBpm = _analysis.TryGetBpm(_modScdPath);
                if (modBpm.HasValue) return modBpm;
            }
            return _currentBpm;
        }
    }

    public void SetModScd(string? scdPath)
    {
        if (string.IsNullOrEmpty(scdPath)) { _modScdPath = null; return; }
        _modScdPath = scdPath;
        _analysis.QueueScdFromDisk(scdPath);
    }

    public void ClearModScd() => _modScdPath = null;
    public string? ModScdPath => _modScdPath;
    public ushort CurrentSongId => _lastSongId;
    public string? CurrentScdPath => _currentScdPath;

    public void Update()
    {
        if (!_sigResolved) return;

        var songId = ReadCurrentSongId();
        if (songId == _lastSongId)
        {
            if (_currentScdPath != null)
            {
                var bpm = _analysis.TryGetBpm(_currentScdPath);
                if (bpm != _currentBpm) _currentBpm = bpm;
            }
            return;
        }

        _lastSongId = songId;
        _currentBpm = null;
        _currentScdPath = null;

        if (songId == 0) return;

        var path = ResolveScdPath(songId);
        if (string.IsNullOrEmpty(path)) return;
        _currentScdPath = path;

        var cached = _analysis.TryGetBpm(path);
        if (cached != null)
        {
            _currentBpm = cached;
            return;
        }

        TryQueueAnalysis(path);
    }

    public void Dispose() { }

    private void ResolveSig()
    {
        try
        {
            _baseAddress = _sigScanner.GetStaticAddressFromSig(BgmBaseSig);
            _sigResolved = _baseAddress != nint.Zero;
            if (_sigResolved)
                _log.Debug($"[BgmTracker] BGM base address resolved at {_baseAddress.ToInt64():X}");
            else
                _log.Warning("[BgmTracker] BGM base sig returned zero address; BPM tracking disabled");
        }
        catch (Exception ex)
        {
            _sigResolved = false;
            _log.Warning($"[BgmTracker] Sig scan failed; BPM tracking disabled: {ex.Message}");
        }
    }

    private unsafe ushort ReadCurrentSongId()
    {
        if (_baseAddress == nint.Zero) return 0;
        var baseObj = Marshal.ReadIntPtr(_baseAddress);
        if (baseObj == nint.Zero) return 0;
        var sceneList = Marshal.ReadIntPtr(baseObj + 0xC0);
        if (sceneList == nint.Zero) return 0;

        // BGMScene: stride 0x58, BgmReference at +0x0C, BgmId at +0x0E
        const int SceneStride = 0x58;
        const int BgmRefOffset = 0x0C;
        const int BgmIdOffset = 0x0E;

        var ptr = (byte*)sceneList.ToPointer();
        for (int i = 0; i < 12; i++)
        {
            byte* scene = ptr + i * SceneStride;
            ushort bgmRef = *(ushort*)(scene + BgmRefOffset);
            ushort bgmId = *(ushort*)(scene + BgmIdOffset);
            if (bgmRef == 0) continue;
            if (bgmId == 0 || bgmId == 9999) continue;
            return bgmId;
        }
        return 0;
    }

    private string? ResolveScdPath(ushort rowId)
    {
        if (_rowToScd.TryGetValue(rowId, out var cached)) return cached;

        string? path = null;
        try
        {
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.BGM>();
            if (sheet != null && sheet.TryGetRow(rowId, out var row))
            {
                var s = row.File.ToString();
                if (!string.IsNullOrWhiteSpace(s)) path = s;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"[BgmTracker] BGM row lookup failed for {rowId}: {ex.Message}");
        }

        _rowToScd[rowId] = path;
        return path;
    }

    private void TryQueueAnalysis(string path)
    {
        try
        {
            var scd = _dataManager.GetFile<ScdFile>(path);
            if (scd == null) return;
            if (scd.AudioDataCount == 0) return;
            var audio = scd.GetAudio(0);
            if (audio?.AudioData == null || audio.AudioData.Length < 256) return;

            // Lumina's GetAudio already de-XORs into a standalone OGG stream
            _analysis.QueueAnalysis(path, audio.AudioData);
        }
        catch (Exception ex)
        {
            _log.Debug($"[BgmTracker] SCD load/queue failed for {path}: {ex.Message}");
        }
    }
}
