using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if WINDOWS
using System.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
#endif
using StudyHub.Models;

namespace StudyHub.Services
{
    public class SoundChannel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "🎵";
        public string Description { get; set; } = "";
        public double Volume { get; set; } = 0; // 0 to 100
        public double LastActiveVolume { get; set; } = 0;
        public bool IsMuted { get; set; } = false;
        public bool IsCustom { get; set; } = false;
        public string? CustomFilePath { get; set; }

#if WINDOWS
        internal ChannelLoopSampleProvider? LoopProvider { get; set; }
#elif ANDROID
        internal Android.Media.MediaPlayer? AndroidPlayer { get; set; }
#endif
    }

#if WINDOWS
    /// <summary>
    /// Thread-safe gapless sample provider for infinite looping with smooth gain ramping.
    /// Supports both pre-decoded memory buffers (built-in sounds) and streaming audio files (custom MP3/WAV).
    /// </summary>
    public class ChannelLoopSampleProvider : ISampleProvider
    {
        private readonly WaveFormat _format;
        private readonly object _lock = new();

        private readonly float[]? _memoryBuffer;
        private int _memoryPosition;

        private readonly WaveStream? _waveStream;
        private readonly ISampleProvider? _sourceProvider;

        private float _currentGain = 0f;
        private float _targetGain = 0f;

        public WaveFormat WaveFormat => _format;

        // Constructor for in-memory float buffers (zero disk I/O, 100% gapless)
        public ChannelLoopSampleProvider(float[] buffer, WaveFormat format)
        {
            _memoryBuffer = buffer;
            _format = format;
        }

        // Constructor for streaming files
        public ChannelLoopSampleProvider(WaveStream waveStream, ISampleProvider sourceProvider, WaveFormat format)
        {
            _waveStream = waveStream;
            _sourceProvider = sourceProvider;
            _format = format;
        }

        public void SetTargetGain(float targetGain)
        {
            _targetGain = Math.Clamp(targetGain, 0f, 1f);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // If completely silent, return zeroed buffer quickly
            if (_currentGain <= 0.0001f && _targetGain <= 0.0001f)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            lock (_lock)
            {
                if (_memoryBuffer != null && _memoryBuffer.Length > 0)
                {
                    int filled = 0;
                    while (filled < count)
                    {
                        int available = _memoryBuffer.Length - _memoryPosition;
                        int toCopy = Math.Min(count - filled, available);
                        Array.Copy(_memoryBuffer, _memoryPosition, buffer, offset + filled, toCopy);
                        filled += toCopy;
                        _memoryPosition = (_memoryPosition + toCopy) % _memoryBuffer.Length;
                    }
                }
                else if (_sourceProvider != null && _waveStream != null)
                {
                    int filled = 0;
                    while (filled < count)
                    {
                        int read = _sourceProvider.Read(buffer, offset + filled, count - filled);
                        if (read == 0)
                        {
                            try
                            {
                                _waveStream.Position = 0;
                            }
                            catch
                            {
                                break;
                            }
                            int retry = _sourceProvider.Read(buffer, offset + filled, count - filled);
                            if (retry == 0) break;
                            read = retry;
                        }
                        filled += read;
                    }
                    if (filled < count)
                    {
                        Array.Clear(buffer, offset + filled, count - filled);
                    }
                }
                else
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                // Smooth gain ramp over the buffer chunk to prevent clicks
                float step = (_targetGain - _currentGain) / Math.Min(count, 512);
                for (int i = 0; i < count; i++)
                {
                    if (Math.Abs(_currentGain - _targetGain) > 0.00001f)
                    {
                        _currentGain += step;
                    }
                    else
                    {
                        _currentGain = _targetGain;
                    }
                    buffer[offset + i] *= _currentGain;
                }

                return count;
            }
        }

        public void DisposeStream()
        {
            lock (_lock)
            {
                _targetGain = 0f;
                _currentGain = 0f;
                try
                {
                    _waveStream?.Dispose();
                }
                catch { }
            }
        }
    }
#endif

    public class AudioStudioService : IDisposable
    {
        public double MasterVolume { get; set; } = 80; // 0 to 100
        public bool IsMasterMuted { get; set; } = false;
        public bool IsMasterPaused { get; set; } = true;

        public event Action? PlaybackStateChanged;

        public List<SoundChannel> Channels { get; } = new();

        private readonly string _soundsDir;
#if WINDOWS
        private readonly WaveFormat _mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        private readonly MixingSampleProvider _mixer;
        private WaveOutEvent? _waveOut;
#endif
        private readonly object _audioLock = new();

        public AudioStudioService(string soundsDir)
        {
            _soundsDir = soundsDir;
            Directory.CreateDirectory(_soundsDir);

#if WINDOWS
            _mixer = new MixingSampleProvider(_mixerFormat) { ReadFully = true };
#endif

            // Exact 9 built-in channels from Study Hub design specifications
            Channels.Add(new SoundChannel
            {
                Id = "zen_video",
                Name = "Zen Sanctuary Pad",
                Icon = "🌸",
                Description = "Peaceful Tibetan singing bowl & warm sanctuary pad"
            });
            Channels.Add(new SoundChannel
            {
                Id = "rain",
                Name = "Gentle Rain",
                Icon = "🌧️",
                Description = "Authentic soft rainfall & gentle window droplets"
            });
            Channels.Add(new SoundChannel
            {
                Id = "campfire",
                Name = "Campfire Hearth",
                Icon = "🔥",
                Description = "Cozy wood-burning fireplace & glowing ember snaps"
            });
            Channels.Add(new SoundChannel
            {
                Id = "wind",
                Name = "Bamboo Forest Wind",
                Icon = "🍃",
                Description = "Breezy air sweeping through leaves"
            });
            Channels.Add(new SoundChannel
            {
                Id = "ocean",
                Name = "Ocean Waves & Surf",
                Icon = "🌊",
                Description = "Surging coastal surf, foaming crash & water backwash"
            });
            Channels.Add(new SoundChannel
            {
                Id = "airplane",
                Name = "Airplane Cabin Drone",
                Icon = "✈️",
                Description = "Jet cruising cabin white rumble & deep focus masking"
            });
            Channels.Add(new SoundChannel
            {
                Id = "crickets",
                Name = "Night Sanctuary",
                Icon = "🌙",
                Description = "Authentic summer night crickets & evening air"
            });
            Channels.Add(new SoundChannel
            {
                Id = "brown_noise",
                Name = "Deep Brown Noise",
                Icon = "🎧",
                Description = "Low-frequency focus masking"
            });
            Channels.Add(new SoundChannel
            {
                Id = "pink_noise",
                Name = "Soft Pink Noise",
                Icon = "☁️",
                Description = "Balanced acoustic background blanket"
            });

            // Initialize audio engine and pre-load built-in seamless audio buffers
            InitializeBuiltInAudioFiles();
        }

        private void EnsureWaveOutDevice()
        {
#if WINDOWS
            lock (_audioLock)
            {
                try
                {
                    if (_waveOut == null)
                    {
                        _waveOut = new WaveOutEvent
                        {
                            DesiredLatency = 120,
                            NumberOfBuffers = 3
                        };
                        _waveOut.Init(_mixer);
                        _waveOut.Play();
                    }
                    else if (_waveOut.PlaybackState != PlaybackState.Playing)
                    {
                        _waveOut.Play();
                    }
                }
                catch
                {
                    // Graceful fallback if no audio hardware is present
                }
            }
#endif
        }

        private void InitializeBuiltInAudioFiles()
        {
            try
            {
                foreach (var ch in Channels)
                {
                    string wavPath = Path.Combine(_soundsDir, $"{ch.Id}.wav");
                    bool needsRegen = !File.Exists(wavPath) || new FileInfo(wavPath).Length < 1_000_000;

                    if (needsRegen)
                    {
                        GenerateHighQualityProceduralWav(wavPath, ch.Id);
                    }

                    SetupChannelPlayer(ch, wavPath);
                }
            }
            catch
            {
                // Audio engine gracefully continues
            }
        }

        private void SetupChannelPlayer(SoundChannel ch, string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                lock (_audioLock)
                {
#if WINDOWS
                    // If channel already had a provider attached, remove and dispose it cleanly
                    if (ch.LoopProvider != null)
                    {
                        _mixer.RemoveMixerInput(ch.LoopProvider);
                        ch.LoopProvider.DisposeStream();
                        ch.LoopProvider = null;
                    }

                    // Check if file is small (built-in 16s WAV) -> load into memory float[] for 100% gapless playback!
                    var fi = new FileInfo(filePath);
                    if (filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && fi.Length < 10_000_000)
                    {
                        using var reader = new AudioFileReader(filePath);
                        ISampleProvider sp = reader;
                        if (sp.WaveFormat.SampleRate != _mixerFormat.SampleRate)
                        {
                            sp = new WdlResamplingSampleProvider(sp, _mixerFormat.SampleRate);
                        }
                        if (sp.WaveFormat.Channels == 1)
                        {
                            sp = new MonoToStereoSampleProvider(sp);
                        }

                        int totalSamples = (int)(reader.TotalTime.TotalSeconds * _mixerFormat.SampleRate * _mixerFormat.Channels);
                        if (totalSamples <= 0) totalSamples = (int)(fi.Length / 2);
                        var buffer = new float[totalSamples];
                        int read = 0;
                        int r;
                        while (read < buffer.Length && (r = sp.Read(buffer, read, buffer.Length - read)) > 0)
                        {
                            read += r;
                        }

                        if (read > 0)
                        {
                            if (read < buffer.Length) Array.Resize(ref buffer, read);
                            var loopProvider = new ChannelLoopSampleProvider(buffer, _mixerFormat);
                            ch.LoopProvider = loopProvider;
                            ch.CustomFilePath = filePath;
                            _mixer.AddMixerInput(loopProvider);
                            return;
                        }
                    }

                    // Streaming reader for custom MP3s or large audio files
                    var streamReader = new AudioFileReader(filePath);
                    ISampleProvider streamSp = streamReader;
                    if (streamSp.WaveFormat.SampleRate != _mixerFormat.SampleRate)
                    {
                        streamSp = new WdlResamplingSampleProvider(streamSp, _mixerFormat.SampleRate);
                    }
                    if (streamSp.WaveFormat.Channels == 1)
                    {
                        streamSp = new MonoToStereoSampleProvider(streamSp);
                    }

                    var streamingLoopProvider = new ChannelLoopSampleProvider(streamReader, streamSp, _mixerFormat);
                    ch.LoopProvider = streamingLoopProvider;
                    ch.CustomFilePath = filePath;
                    _mixer.AddMixerInput(streamingLoopProvider);
#elif ANDROID
                    if (ch.AndroidPlayer != null)
                    {
                        try
                        {
                            ch.AndroidPlayer.Stop();
                            ch.AndroidPlayer.Release();
                        }
                        catch { }
                        ch.AndroidPlayer.Dispose();
                        ch.AndroidPlayer = null;
                    }

                    var mp = new Android.Media.MediaPlayer();
                    mp.SetDataSource(filePath);
                    mp.Looping = true;
                    mp.Prepare();
                    mp.SetVolume(0f, 0f);
                    ch.AndroidPlayer = mp;
                    ch.CustomFilePath = filePath;
#endif
                }
            }
            catch
            {
                // Ignore player setup failure for individual corrupted sound file
            }
        }

        public void SyncCustomSounds(List<CustomSoundItem>? customSounds, Dictionary<string, int>? savedLevels, double masterVol = 80)
        {
            MasterVolume = Math.Clamp(masterVol, 0, 100);

            if (customSounds != null)
            {
                foreach (var cs in customSounds)
                {
                    var existing = Channels.Find(c => c.Id == cs.Id);
                    string fullPath = Path.Combine(_soundsDir, cs.Filename ?? "");

                    if (existing == null)
                    {
                        var newCh = new SoundChannel
                        {
                            Id = cs.Id,
                            Name = cs.Name ?? cs.OriginalName ?? "Custom Sound",
                            Icon = "🎵",
                            Description = "Custom imported audio loop",
                            Volume = 0,
                            LastActiveVolume = 0,
                            IsCustom = true
                        };

                        if (File.Exists(fullPath))
                        {
                            SetupChannelPlayer(newCh, fullPath);
                        }

                        Channels.Add(newCh);
                    }
                    else if (
#if WINDOWS
                        existing.LoopProvider == null
#elif ANDROID
                        existing.AndroidPlayer == null
#else
                        false
#endif
                        && File.Exists(fullPath))
                    {
                        SetupChannelPlayer(existing, fullPath);
                    }
                }
            }

            // Restore saved mixer levels into Volume & LastActiveVolume
            if (savedLevels != null)
            {
                foreach (var kvp in savedLevels)
                {
                    var ch = Channels.Find(c => c.Id == kvp.Key);
                    if (ch != null && kvp.Value > 0)
                    {
                        ch.LastActiveVolume = kvp.Value;
                        ch.Volume = kvp.Value;
                    }
                }
            }

            ApplyVolumes();
        }

        public int GetActiveChannelsCount()
        {
            return Channels.Count(c => c.Volume > 0 && !c.IsMuted);
        }

        public void SetChannelVolume(string channelId, double vol)
        {
            var ch = Channels.Find(c => c.Id == channelId);
            if (ch == null) return;
            ch.Volume = Math.Clamp(vol, 0, 100);

            if (ch.Volume > 0)
            {
                ch.LastActiveVolume = ch.Volume;
                ch.IsMuted = false;
                IsMasterPaused = false;
                EnsureWaveOutDevice();
            }
            else
            {
                ch.LastActiveVolume = 0;
            }

            ApplyVolumes();
            PlaybackStateChanged?.Invoke();
        }

        public void ToggleChannelMute(string channelId)
        {
            var ch = Channels.Find(c => c.Id == channelId);
            if (ch == null) return;
            ch.IsMuted = !ch.IsMuted;

            if (!ch.IsMuted && ch.Volume <= 0)
            {
                ch.Volume = ch.LastActiveVolume > 0 ? ch.LastActiveVolume : 50;
                IsMasterPaused = false;
                EnsureWaveOutDevice();
            }

            ApplyVolumes();
            PlaybackStateChanged?.Invoke();
        }

        public void SetMasterVolume(double vol)
        {
            MasterVolume = Math.Clamp(vol, 0, 100);
            if (MasterVolume > 0 && IsMasterMuted)
            {
                IsMasterMuted = false;
            }
            ApplyVolumes();
            PlaybackStateChanged?.Invoke();
        }

        public void ToggleMasterMute()
        {
            IsMasterMuted = !IsMasterMuted;
            ApplyVolumes();
            PlaybackStateChanged?.Invoke();
        }

        public void ToggleMasterPlayPause()
        {
            if (IsActivePlaying())
            {
                PauseAll();
            }
            else
            {
                ResumeAll();
            }
        }

        public bool IsActivePlaying()
        {
            if (IsMasterPaused || IsMasterMuted || MasterVolume <= 0) return false;
            return Channels.Exists(c => c.Volume > 0 && !c.IsMuted);
        }

        public void ResumeAll()
        {
            IsMasterPaused = false;
            IsMasterMuted = false;
            if (MasterVolume <= 0) MasterVolume = 80;
            EnsureWaveOutDevice();

            // Check if any channel has non-zero volume
            bool anyPlaying = Channels.Exists(c => c.Volume > 0 && !c.IsMuted);
            if (!anyPlaying)
            {
                // Restore previous volumes for all channels that had a last active volume
                bool restoredAny = false;
                foreach (var ch in Channels)
                {
                    if (ch.LastActiveVolume > 0)
                    {
                        ch.Volume = ch.LastActiveVolume;
                        ch.IsMuted = false;
                        restoredAny = true;
                    }
                }

                // If none had an active volume from a previous session, activate default Rainy Study preset
                if (!restoredAny)
                {
                    var rain = Channels.Find(c => c.Id == "rain");
                    if (rain != null) { rain.Volume = 65; rain.LastActiveVolume = 65; }
                    var zen = Channels.Find(c => c.Id == "zen_video");
                    if (zen != null) { zen.Volume = 35; zen.LastActiveVolume = 35; }
                }
            }

            ApplyVolumes();
            PlaybackStateChanged?.Invoke();
        }

        public void PauseAll()
        {
            IsMasterPaused = true;
            foreach (var ch in Channels)
            {
                ch.LastActiveVolume = (ch.Volume > 0 && !ch.IsMuted) ? ch.Volume : 0;
            }

            ApplyVolumes();
            PlaybackStateChanged?.Invoke();
        }

        public void StopAndResetAll()
        {
            IsMasterPaused = true;
            bool anyActive = Channels.Exists(c => c.Volume > 0 && !c.IsMuted);
            foreach (var ch in Channels)
            {
                if (anyActive)
                {
                    ch.LastActiveVolume = (ch.Volume > 0 && !ch.IsMuted) ? ch.Volume : 0;
                }
                ch.Volume = 0;
                ch.IsMuted = false;
            }

            ApplyVolumes();
            PlaybackStateChanged?.Invoke();
        }

        public void ApplyPreset(string presetKey)
        {
            foreach (var ch in Channels)
            {
                ch.Volume = 0;
                ch.LastActiveVolume = 0;
                ch.IsMuted = false;
            }

            switch (presetKey.ToLowerInvariant())
            {
                case "rainy_zen":
                    SetPresetLevel("rain", 65);
                    SetPresetLevel("zen_video", 35);
                    break;
                case "cozy_hearth":
                    SetPresetLevel("campfire", 70);
                    SetPresetLevel("rain", 30);
                    SetPresetLevel("wind", 20);
                    break;
                case "sanctuary":
                    SetPresetLevel("zen_video", 55);
                    SetPresetLevel("wind", 35);
                    SetPresetLevel("crickets", 25);
                    break;
                case "deep_brown":
                    SetPresetLevel("brown_noise", 65);
                    SetPresetLevel("ocean", 25);
                    break;
            }

            IsMasterPaused = false;
            IsMasterMuted = false;
            if (MasterVolume <= 0) MasterVolume = 80;
            EnsureWaveOutDevice();
            ApplyVolumes();
            PlaybackStateChanged?.Invoke();
        }

        public void ApplyCustomPreset(Dictionary<string, int> levels)
        {
            foreach (var ch in Channels)
            {
                ch.Volume = 0;
                ch.LastActiveVolume = 0;
                ch.IsMuted = false;
            }

            foreach (var kvp in levels)
            {
                SetPresetLevel(kvp.Key, kvp.Value);
            }

            IsMasterPaused = false;
            IsMasterMuted = false;
            if (MasterVolume <= 0) MasterVolume = 80;
            EnsureWaveOutDevice();
            ApplyVolumes();
            PlaybackStateChanged?.Invoke();
        }

        private void SetPresetLevel(string id, int level)
        {
            var ch = Channels.Find(c => c.Id == id);
            if (ch != null)
            {
                ch.Volume = Math.Clamp(level, 0, 100);
                ch.LastActiveVolume = ch.Volume;
                ch.IsMuted = false;
            }
        }

        public void ApplyVolumes()
        {
            double masterScale = (IsMasterPaused || IsMasterMuted) ? 0 : (MasterVolume / 100.0);

            foreach (var ch in Channels)
            {
                double eff = ch.IsMuted ? 0 : ((ch.Volume / 100.0) * masterScale);
#if WINDOWS
                if (ch.LoopProvider == null) continue;
                ch.LoopProvider.SetTargetGain((float)eff);
#elif ANDROID
                if (ch.AndroidPlayer == null && !string.IsNullOrEmpty(ch.CustomFilePath) && File.Exists(ch.CustomFilePath) && eff > 0.0001)
                {
                    SetupChannelPlayer(ch, ch.CustomFilePath);
                }
                if (ch.AndroidPlayer == null) continue;
                try
                {
                    float g = (float)Math.Clamp(eff, 0.0, 1.0);
                    ch.AndroidPlayer.SetVolume(g, g);
                    if (g > 0.001f)
                    {
                        if (!ch.AndroidPlayer.IsPlaying) ch.AndroidPlayer.Start();
                    }
                    else
                    {
                        if (ch.AndroidPlayer.IsPlaying) ch.AndroidPlayer.Pause();
                    }
                }
                catch { }
#endif
            }
        }

        public void PlayNotificationChime()
        {
            try
            {
#if WINDOWS
                SystemSounds.Asterisk.Play();
#elif ANDROID
                using var tg = new Android.Media.ToneGenerator(Android.Media.Stream.Notification, 85);
                tg.StartTone(Android.Media.Tone.PropBeep2, 240);
#endif
            }
            catch { }
        }

        public void RemoveCustomSound(string channelId)
        {
            var ch = Channels.Find(c => c.Id == channelId && c.IsCustom);
            if (ch == null) return;

            lock (_audioLock)
            {
#if WINDOWS
                if (ch.LoopProvider != null)
                {
                    _mixer.RemoveMixerInput(ch.LoopProvider);
                    ch.LoopProvider.DisposeStream();
                    ch.LoopProvider = null;
                }
#elif ANDROID
                if (ch.AndroidPlayer != null)
                {
                    try { ch.AndroidPlayer.Stop(); ch.AndroidPlayer.Release(); } catch { }
                    ch.AndroidPlayer.Dispose();
                    ch.AndroidPlayer = null;
                }
#endif
                Channels.Remove(ch);
            }
            PlaybackStateChanged?.Invoke();
        }

        public void Dispose()
        {
            lock (_audioLock)
            {
                try
                {
#if WINDOWS
                    _waveOut?.Stop();
                    _waveOut?.Dispose();
                    _waveOut = null;
                    foreach (var ch in Channels)
                    {
                        ch.LoopProvider?.DisposeStream();
                    }
#elif ANDROID
                    foreach (var ch in Channels)
                    {
                        if (ch.AndroidPlayer != null)
                        {
                            try { ch.AndroidPlayer.Stop(); ch.AndroidPlayer.Release(); } catch { }
                            ch.AndroidPlayer.Dispose();
                            ch.AndroidPlayer = null;
                        }
                    }
#endif
                }
                catch { }
            }
        }

        // ================= PROCEDURAL 44.1kHz STEREO WAV SYNTHESIS =================
        // Ports the WebAudio synthesis algorithms from graduation_study_hub.html lines 9540–9938
        // Creates 16-second seamless stereo 44.1kHz loopable WAVs with equal-power circular crossfade.
        private static void GenerateHighQualityProceduralWav(string outputPath, string type)
        {
            int sampleRate = 44100;
            double durationSec = 16.0;
            double fadeSec = 2.5;

            int loopLen = (int)(sampleRate * durationSec);
            int fadeLen = (int)(sampleRate * fadeSec);
            int rawLen = loopLen + fadeLen;

            float[][] channels = new float[2][];
            channels[0] = new float[rawLen];
            channels[1] = new float[rawLen];

            var rand = new Random(type.GetHashCode());

            // 1. Generate raw synthesized texture across rawLen for each stereo channel
            for (int ch = 0; ch < 2; ch++)
            {
                float[] raw = channels[ch];
                double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
                double dropEnv = 0, dropFreq = 1200, dropPhase = 0;
                double popEnv = 0, popFreq = 750, popPhase = 0, simmer = 0;
                double lastBrown = 0;

                for (int i = 0; i < rawLen; i++)
                {
                    double t = (double)i / sampleRate;
                    double w = rand.NextDouble() * 2.0 - 1.0;

                    if (type == "zen_video")
                    {
                        // Tibetan singing bowl harmonic modes (D3 144Hz + solfeggio resonant overtones + warm analog pad)
                        double baseFreq = 144.0;
                        double detuneCh = (ch == 0) ? 0.0 : 0.32;
                        double bowl = 0;

                        double s1 = 0.88 + 0.12 * Math.Sin(2 * Math.PI * 0.14 * t + 0);
                        bowl += Math.Sin(2 * Math.PI * (baseFreq + detuneCh) * t) * 0.090 * s1;

                        double s2 = 0.88 + 0.12 * Math.Sin(2 * Math.PI * 0.14 * t + 1);
                        bowl += Math.Sin(2 * Math.PI * (2.76 * baseFreq + detuneCh) * t) * 0.055 * s2;

                        double s3 = 0.88 + 0.12 * Math.Sin(2 * Math.PI * 0.14 * t + 2);
                        bowl += Math.Sin(2 * Math.PI * (4.02 * baseFreq) * t) * 0.038 * s3;

                        bowl += Math.Sin(2 * Math.PI * (6.00 * baseFreq) * t) * 0.022;
                        bowl += Math.Sin(2 * Math.PI * (8.04 * baseFreq) * t) * 0.012;

                        double pad1 = Math.Sin(2 * Math.PI * 144.0 * t) * 0.040;
                        double pad2 = Math.Sin(2 * Math.PI * 216.0 * t) * 0.030;
                        double pad3 = Math.Sin(2 * Math.PI * 288.0 * t) * 0.022;

                        b0 = (b0 + 0.018 * w) / 1.018;
                        raw[i] = (float)(bowl + pad1 + pad2 + pad3 + b0 * 0.035);
                    }
                    else if (type == "rain")
                    {
                        // Soft rain shower sheet + organic Poisson window micro-droplets
                        b0 = 0.992 * b0 + w * 0.09;
                        b1 = 0.950 * b1 + w * 0.18;
                        b2 = 0.780 * b2 + w * 0.32;
                        double rainBed = (b0 + b1 + b2) * 0.065;

                        if (dropEnv < 0.001 && rand.NextDouble() < 0.0032)
                        {
                            dropEnv = 0.22 + rand.NextDouble() * 0.35;
                            dropFreq = 850 + rand.NextDouble() * 950;
                        }
                        dropEnv *= 0.978;
                        dropPhase += (2 * Math.PI * dropFreq) / sampleRate;
                        double dropImpact = Math.Sin(dropPhase) * dropEnv * (0.45 + 0.55 * w);

                        raw[i] = (float)(rainBed + dropImpact * 0.40);
                    }
                    else if (type == "campfire")
                    {
                        // Warm wood-burning fireplace & glowing ember snaps
                        b0 = (b0 + 0.024 * w) / 1.024; // flame bed
                        simmer = 0.85 * simmer + 0.15 * (rand.NextDouble() < 0.015 ? w : 0);

                        if (popEnv < 0.002 && rand.NextDouble() < 0.0011)
                        {
                            popEnv = 0.25 + rand.NextDouble() * 0.40;
                            popFreq = 500 + rand.NextDouble() * 900;
                        }
                        popEnv *= 0.965;
                        popPhase += (2 * Math.PI * popFreq) / sampleRate;
                        double woodPop = (Math.Sin(popPhase) + w * 0.35) * popEnv;

                        raw[i] = (float)(b0 * 1.85 + simmer * 0.45 + woodPop * 0.65);
                    }
                    else if (type == "wind")
                    {
                        // Breezy air sweeping through leaves (dual swept filter bank simulation)
                        b0 = 0.997 * b0 + w * 0.06;
                        b1 = 0.985 * b1 + w * 0.10;
                        b2 = 0.940 * b2 + w * 0.18;
                        b3 = 0.810 * b3 + w * 0.30;
                        b4 = 0.520 * b4 + w * 0.45;
                        double air = (b0 + b1 + b2 + b3 + b4) * 0.065;

                        double lfoA = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.11 * t);
                        double lfoB = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.085 * t + Math.PI);
                        raw[i] = (float)(air * (0.65 * lfoA + 0.50 * lfoB));
                    }
                    else if (type == "ocean" || type == "waves")
                    {
                        // Surging coastal surf, foaming crash & water backwash (8s cycle)
                        double wavePeriod = 8.0;
                        double phaseOffset = (ch == 0) ? 0.0 : 0.85;
                        b0 = (b0 + 0.028 * w) / 1.028;

                        double cycleTime = (t + phaseOffset) % wavePeriod;
                        double normTime = cycleTime / wavePeriod;

                        double waveEnergy = 0;
                        if (normTime < 0.40)
                        {
                            double swell = Math.Sin((normTime / 0.40) * Math.PI * 0.5);
                            waveEnergy = swell * 0.35;
                        }
                        else if (normTime < 0.65)
                        {
                            double crashProg = (normTime - 0.40) / 0.25;
                            double crash = Math.Exp(-crashProg * 3.5);
                            waveEnergy = 0.35 + crash * 0.65;
                        }
                        else
                        {
                            double recedeProg = (normTime - 0.65) / 0.35;
                            waveEnergy = Math.Max(0.12, 0.40 * Math.Exp(-recedeProg * 2.5));
                        }

                        double foam = (w * 0.12) * Math.Sin(normTime * Math.PI);
                        raw[i] = (float)((b0 * 1.2 + w * 0.32) * waveEnergy + foam);
                    }
                    else if (type == "airplane")
                    {
                        // Jet cruising cabin rumble + pink air circulation
                        b0 = 0.998 * b0 + w * 0.055;
                        b1 = 0.992 * b1 + w * 0.075;
                        b2 = 0.965 * b2 + w * 0.150;
                        b3 = 0.860 * b3 + w * 0.300;
                        b4 = 0.540 * b4 + w * 0.520;
                        double fuselage = (b0 + b1 + b2 + b3 + b4) * 0.072;

                        double engineFreq = (ch == 0) ? 84.5 : 85.5;
                        double hum1 = Math.Sin(2 * Math.PI * engineFreq * t) * 0.065;
                        double hum2 = Math.Sin(2 * Math.PI * (engineFreq * 2) * t) * 0.032;

                        raw[i] = (float)(fuselage + hum1 + hum2);
                    }
                    else if (type == "crickets")
                    {
                        // Authentic summer night crickets & evening air
                        b0 = (b0 + 0.022 * w) / 1.022; // air bed
                        double cricketFreq = (ch == 0) ? 4320.0 : 4480.0;
                        double chirpPeriod = 1.35;
                        double pulseRate = 28.0;
                        double motifDuration = 0.14;

                        double cycleT = (t + (ch * 0.42)) % chirpPeriod;
                        double cricketChirp = 0;
                        if (cycleT < motifDuration)
                        {
                            double sylPhase = cycleT * pulseRate * 2 * Math.PI;
                            double sylEnv = Math.Max(0, Math.Sin(sylPhase));
                            double motifEnv = Math.Sin((cycleT / motifDuration) * Math.PI);
                            double env = sylEnv * motifEnv * 0.075;
                            cricketChirp = Math.Sin(2 * Math.PI * cricketFreq * t) * env;
                        }

                        raw[i] = (float)(b0 * 0.65 + cricketChirp);
                    }
                    else if (type == "brown_noise")
                    {
                        // True leaky-integrator deep brown noise (-6 dB/oct)
                        lastBrown = (lastBrown + 0.022 * w) / 1.022;
                        raw[i] = (float)(lastBrown * 2.85);
                    }
                    else if (type == "pink_noise")
                    {
                        // Paul Kellet -3 dB/oct soft pink noise
                        b0 = 0.99886 * b0 + w * 0.0555179;
                        b1 = 0.99332 * b1 + w * 0.0750759;
                        b2 = 0.96900 * b2 + w * 0.1538520;
                        b3 = 0.86650 * b3 + w * 0.3104856;
                        b4 = 0.55000 * b4 + w * 0.5329522;
                        b5 = -0.7616 * b5 - w * 0.0168980;
                        raw[i] = (float)((b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.045);
                        b6 = w * 0.115926;
                    }
                    else
                    {
                        b0 = 0.99 * b0 + w * 0.05;
                        raw[i] = (float)(b0 * 0.5);
                    }
                }

                // 2. Remove DC offset and gently normalize RMS to balanced studio loudness
                double sum = 0;
                for (int i = 0; i < rawLen; i++) sum += raw[i];
                float mean = (float)(sum / rawLen);

                double sqSum = 0;
                for (int i = 0; i < rawLen; i++)
                {
                    raw[i] -= mean;
                    sqSum += raw[i] * raw[i];
                }

                double rms = Math.Sqrt(sqSum / rawLen);
                if (rms > 0.0001)
                {
                    // Balance level cleanly around 0.16 RMS with soft saturation safety
                    float normScale = (float)Math.Clamp(0.16 / rms, 0.4, 4.5);
                    for (int i = 0; i < rawLen; i++)
                    {
                        raw[i] = (float)Math.Tanh(raw[i] * normScale);
                    }
                }
            }

            // 3. Write 16-second loopable WAV with equal-power circular crossfade
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            int numChannels = 2;
            int bytesPerSample = 2; // 16-bit PCM
            int subChunk2Size = loopLen * numChannels * bytesPerSample;
            int chunkSize = 36 + subChunk2Size;

            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(chunkSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16); // Subchunk1Size
            bw.Write((short)1); // AudioFormat (PCM)
            bw.Write((short)numChannels);
            bw.Write(sampleRate);
            bw.Write(sampleRate * numChannels * bytesPerSample); // ByteRate
            bw.Write((short)(numChannels * bytesPerSample)); // BlockAlign
            bw.Write((short)16); // BitsPerSample
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(subChunk2Size);

            for (int i = 0; i < loopLen; i++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    float s;
                    if (i < fadeLen)
                    {
                        double p = (double)i / fadeLen;
                        double fadeIn = Math.Sin(p * Math.PI * 0.5);
                        double fadeOut = Math.Cos(p * Math.PI * 0.5);
                        s = (float)(channels[ch][i] * fadeIn + channels[ch][loopLen + i] * fadeOut);
                    }
                    else
                    {
                        s = channels[ch][i];
                    }

                    short pcm = (short)Math.Clamp(s * 28000.0f, -32767.0f, 32767.0f);
                    bw.Write(pcm);
                }
            }
        }
    }
}
