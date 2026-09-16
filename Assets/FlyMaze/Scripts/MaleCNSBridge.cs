using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace FlyMaze
{
    public readonly struct MaleCNSSensoryFrame
    {
        public readonly float FrontObstacle;
        public readonly float LeftObstacle;
        public readonly float RightObstacle;
        public readonly float TargetBearing;
        public readonly float TargetStrength;
        public readonly int TargetKind;

        public MaleCNSSensoryFrame(float frontObstacle, float leftObstacle, float rightObstacle, float targetBearing, float targetStrength, int targetKind)
        {
            FrontObstacle = Mathf.Clamp01(frontObstacle);
            LeftObstacle = Mathf.Clamp01(leftObstacle);
            RightObstacle = Mathf.Clamp01(rightObstacle);
            TargetBearing = Mathf.Clamp(targetBearing, -1f, 1f);
            TargetStrength = Mathf.Clamp01(targetStrength);
            TargetKind = targetKind;
        }
    }

    [DisallowMultipleComponent]
    public sealed class MaleCNSBridge : MonoBehaviour
    {
        [Header("MaleCNS v1.0 Runtime")]
        [SerializeField] private bool autoStart = true;
        [SerializeField, Range(2f, 30f)] private float commandRateHz = 12f;

        public bool IsReady { get; private set; }
        public bool HasRuntimeData => File.Exists(GetWeightsPath()) && File.Exists(GetMetaPath());
        public string Status { get; private set; } = "MALECNS v1.0 / NOT SET UP";
        public float ForwardOutput { get; private set; }
        public float TurnOutput { get; private set; }
        public float EscapeOutput { get; private set; }
        public int LastSpikeCount { get; private set; }
        public int NeuronCount { get; private set; }
        public long ConnectionCount { get; private set; }
        public float DNa02Left { get; private set; }
        public float DNa02Right { get; private set; }
        public float DNa01Left { get; private set; }
        public float DNa01Right { get; private set; }
        public float ForwardLeft { get; private set; }
        public float ForwardRight { get; private set; }
        public float PamActivity { get; private set; }
        public float Ppl1Activity { get; private set; }
        public float LastRewardPulse { get; private set; }
        public float LastPunishmentPulse { get; private set; }
        public int ReinforcementEventCount { get; private set; }
        public MaleCNSSensoryFrame LatestSensoryFrame => _latestFrame;

        private readonly ConcurrentQueue<string> _stdout = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _stderr = new ConcurrentQueue<string>();

        private Process _process;
        private MaleCNSSensoryFrame _latestFrame;
        private bool _hasFrame;
        private float _nextCommandTime;
        private bool _quitting;
        private float _rewardDisplayUntil;
        private float _punishmentDisplayUntil;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private void Start()
        {
            if (autoStart)
                StartRuntime();
        }

        private void Update()
        {
            DrainProcessOutput();

            if (Time.unscaledTime > _rewardDisplayUntil)
                LastRewardPulse = 0f;
            if (Time.unscaledTime > _punishmentDisplayUntil)
                LastPunishmentPulse = 0f;

            if (!IsReady || _process == null || _process.HasExited || !_hasFrame)
                return;

            if (Time.unscaledTime < _nextCommandTime)
                return;

            _nextCommandTime = Time.unscaledTime + 1f / Mathf.Max(2f, commandRateHz);
            SendFrame(_latestFrame);
        }

        public void SubmitSensory(MaleCNSSensoryFrame frame)
        {
            _latestFrame = frame;
            _hasFrame = true;
        }

        public void GiveReward(float amount = 1f)
        {
            amount = Mathf.Clamp(amount, 0f, 2f);
            if (amount <= 0f)
                return;

            LastRewardPulse = amount;
            _rewardDisplayUntil = Time.unscaledTime + 1.1f;
            ReinforcementEventCount++;
            if (IsReady)
                SendRaw("E|" + amount.ToString("0.0000", Invariant) + "|0.0000");
        }

        public void GivePunishment(float amount = 1f)
        {
            amount = Mathf.Clamp(amount, 0f, 2f);
            if (amount <= 0f)
                return;

            LastPunishmentPulse = amount;
            _punishmentDisplayUntil = Time.unscaledTime + 1.1f;
            ReinforcementEventCount++;
            if (IsReady)
                SendRaw("E|0.0000|" + amount.ToString("0.0000", Invariant));
        }

        public void StartRuntime()
        {
            if (_process != null && !_process.HasExited)
                return;

            string python = GetPythonPath();
            string script = GetRuntimeScriptPath();
            string cache = GetCacheDirectory();

            if (!File.Exists(GetWeightsPath()) || !File.Exists(GetMetaPath()))
            {
                Status = "MALECNS v1.0 / RUN SETUP";
                IsReady = false;
                return;
            }

            if (!File.Exists(python))
            {
                Status = "MALECNS v1.0 / PYTHON VENV MISSING";
                IsReady = false;
                return;
            }

            if (!File.Exists(script))
            {
                Status = "MALECNS v1.0 / RUNTIME SCRIPT MISSING";
                IsReady = false;
                return;
            }

            StopRuntime();
            _quitting = false;

            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{script}\" --data \"{cache}\"",
                WorkingDirectory = GetProjectRoot(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                _process = new Process { StartInfo = info, EnableRaisingEvents = true };
                _process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                        _stdout.Enqueue(args.Data);
                };
                _process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                        _stderr.Enqueue(args.Data);
                };
                _process.Exited += (_, __) =>
                {
                    if (!_quitting)
                        _stdout.Enqueue("PROCESS_EXITED");
                };

                if (!_process.Start())
                    throw new InvalidOperationException("Process.Start returned false.");

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                Status = "MALECNS v1.0 / LOADING 166K BRAIN...";
            }
            catch (Exception ex)
            {
                Status = "MALECNS v1.0 / START FAILED";
                UnityEngine.Debug.LogError($"[FlyMaze] MaleCNS runtime failed to start: {ex.Message}");
                StopRuntime();
            }
        }

        public void ResetNetwork()
        {
            ForwardOutput = 0f;
            TurnOutput = 0f;
            EscapeOutput = 0f;
            LastSpikeCount = 0;
            DNa02Left = DNa02Right = 0f;
            DNa01Left = DNa01Right = 0f;
            ForwardLeft = ForwardRight = 0f;
            PamActivity = Ppl1Activity = 0f;
            LastRewardPulse = LastPunishmentPulse = 0f;
            if (IsReady)
                SendRaw("R");
        }

        private void SendFrame(MaleCNSSensoryFrame frame)
        {
            string line = string.Join("|",
                "S",
                frame.FrontObstacle.ToString("0.0000", Invariant),
                frame.LeftObstacle.ToString("0.0000", Invariant),
                frame.RightObstacle.ToString("0.0000", Invariant),
                frame.TargetBearing.ToString("0.0000", Invariant),
                frame.TargetStrength.ToString("0.0000", Invariant),
                frame.TargetKind.ToString(Invariant));
            SendRaw(line);
        }

        private void SendRaw(string line)
        {
            try
            {
                if (_process == null || _process.HasExited)
                    return;
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                Status = "MALECNS v1.0 / PIPE ERROR";
                IsReady = false;
                UnityEngine.Debug.LogWarning($"[FlyMaze] MaleCNS pipe error: {ex.Message}");
            }
        }

        private void DrainProcessOutput()
        {
            while (_stdout.TryDequeue(out string line))
            {
                if (line == "PROCESS_EXITED")
                {
                    if (!_quitting)
                    {
                        Status = "MALECNS v1.0 / RUNTIME EXITED";
                        IsReady = false;
                    }
                    continue;
                }

                if (line.StartsWith("READY|", StringComparison.Ordinal))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 3 && int.TryParse(parts[1], NumberStyles.Integer, Invariant, out int neurons) && long.TryParse(parts[2], NumberStyles.Integer, Invariant, out long connections))
                    {
                        NeuronCount = neurons;
                        ConnectionCount = connections;
                        IsReady = true;
                        Status = $"MALECNS v1.0 / {neurons:N0} NEURONS / ONLINE";
                        UnityEngine.Debug.Log($"[FlyMaze] MaleCNS v1.0 online: {neurons:N0} neurons, {connections:N0} connections.");
                    }
                    continue;
                }

                if (line.StartsWith("M|", StringComparison.Ordinal))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 11)
                    {
                        TryFloat(parts[1], out float forward);
                        TryFloat(parts[2], out float turn);
                        int.TryParse(parts[3], NumberStyles.Integer, Invariant, out int spikes);
                        TryFloat(parts[4], out float d02Left);
                        TryFloat(parts[5], out float d02Right);
                        TryFloat(parts[6], out float escape);
                        TryFloat(parts[7], out float d01Left);
                        TryFloat(parts[8], out float d01Right);
                        TryFloat(parts[9], out float forwardLeft);
                        TryFloat(parts[10], out float forwardRight);

                        ForwardOutput = Mathf.Clamp01(forward);
                        TurnOutput = Mathf.Clamp(turn, -1f, 1f);
                        LastSpikeCount = Mathf.Max(0, spikes);
                        DNa02Left = Mathf.Max(0f, d02Left);
                        DNa02Right = Mathf.Max(0f, d02Right);
                        DNa01Left = Mathf.Max(0f, d01Left);
                        DNa01Right = Mathf.Max(0f, d01Right);
                        ForwardLeft = Mathf.Max(0f, forwardLeft);
                        ForwardRight = Mathf.Max(0f, forwardRight);
                        EscapeOutput = Mathf.Clamp01(escape);

                        if (parts.Length >= 13)
                        {
                            TryFloat(parts[11], out float pam);
                            TryFloat(parts[12], out float ppl1);
                            PamActivity = Mathf.Clamp01(pam);
                            Ppl1Activity = Mathf.Clamp01(ppl1);
                        }
                    }
                    continue;
                }

                if (line.StartsWith("ERR|", StringComparison.Ordinal))
                {
                    Status = "MALECNS v1.0 / RUNTIME ERROR";
                    UnityEngine.Debug.LogWarning("[FlyMaze] " + line);
                }
            }

            int errorLines = 0;
            while (_stderr.TryDequeue(out string line))
            {
                if (errorLines++ < 4)
                    UnityEngine.Debug.LogWarning("[MaleCNS Python] " + line);
            }
        }

        private static bool TryFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, Invariant, out value);
        }

        private void StopRuntime()
        {
            IsReady = false;
            if (_process == null)
                return;

            try
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        _process.StandardInput.WriteLine("Q");
                        _process.StandardInput.Flush();
                    }
                    catch { }

                    if (!_process.WaitForExit(500))
                        _process.Kill();
                }
            }
            catch { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        private void OnDestroy()
        {
            _quitting = true;
            StopRuntime();
        }

        private void OnApplicationQuit()
        {
            _quitting = true;
            StopRuntime();
        }

        public static string GetProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string GetCacheDirectory() => Path.Combine(GetProjectRoot(), "Library", "MaleCNS");
        public static string GetWeightsPath() => Path.Combine(GetCacheDirectory(), "malecns_weights.npz");
        public static string GetMetaPath() => Path.Combine(GetCacheDirectory(), "malecns_meta.npz");
        public static string GetRuntimeScriptPath() => Path.Combine(GetProjectRoot(), "Tools", "MaleCNS", "malecns_runtime.py");

        public static string GetPythonPath()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return Path.Combine(GetCacheDirectory(), "venv", "Scripts", "python.exe");
#else
            return Path.Combine(GetCacheDirectory(), "venv", "bin", "python");
#endif
        }
    }
}
