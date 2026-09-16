using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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
        public string Status { get; private set; } = "MALECNS v1.0 / CHECKING";
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
        public int LearnedSynapseCount { get; private set; }
        public float PlasticityMagnitude { get; private set; }
        public MaleCNSSensoryFrame LatestSensoryFrame => _latestFrame;

        public bool SetupInProgress { get; private set; }
        public float SetupProgress { get; private set; }
        public string SetupStage { get; private set; } = "CHECKING MALECNS v1.0";
        public string SetupDetail { get; private set; } = string.Empty;
        public string SetupError { get; private set; } = string.Empty;
        public bool ShouldShowSetupUI => SetupInProgress || !string.IsNullOrEmpty(SetupError) || (!HasRuntimeData && !IsReady);

        private readonly ConcurrentQueue<string> _stdout = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _stderr = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _setupStdout = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _setupStderr = new ConcurrentQueue<string>();

        private Process _process;
        private Process _setupProcess;
        private MaleCNSSensoryFrame _latestFrame;
        private bool _hasFrame;
        private float _nextCommandTime;
        private bool _quitting;
        private float _rewardDisplayUntil;
        private float _punishmentDisplayUntil;
        private int _setupDownloadIndex = -1;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly Regex PercentRegex = new Regex(@"(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
        private static readonly Regex ScanRegex = new Regex(@"scanned\s+([\d,]+)\s*/\s*([\d,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private void Start()
        {
            if (!autoStart)
                return;

            if (HasRuntimeData && File.Exists(GetPythonPath()))
                StartRuntime();
            else
                BeginAutomaticSetup();
        }

        private void Update()
        {
            DrainSetupOutput();
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

        public void BeginAutomaticSetup()
        {
            if (_setupProcess != null)
            {
                try
                {
                    if (!_setupProcess.HasExited)
                        return;
                }
                catch { }
            }

            if (HasRuntimeData && File.Exists(GetPythonPath()))
            {
                SetupProgress = 1f;
                SetupStage = "MALECNS v1.0 READY";
                SetupDetail = "Runtime cache already exists.";
                SetupError = string.Empty;
                StartRuntime();
                return;
            }

            string root = GetProjectRoot();
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string script = Path.Combine(root, "Tools", "MaleCNS", "setup-malecns.ps1");
            string executable = "powershell.exe";
            string arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
#else
            string script = Path.Combine(root, "Tools", "MaleCNS", "setup-malecns.sh");
            string executable = "/bin/bash";
            string arguments = $"\"{script}\"";
#endif
            if (!File.Exists(script))
            {
                FailSetup("Setup script is missing: " + script);
                return;
            }

            SetupInProgress = true;
            SetupProgress = 0.01f;
            SetupStage = "PREPARING MALECNS v1.0";
            SetupDetail = "First launch downloads the official dataset and builds the local runtime cache.";
            SetupError = string.Empty;
            Status = "MALECNS v1.0 / AUTO SETUP";
            _setupDownloadIndex = -1;

            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                _setupProcess = new Process { StartInfo = info, EnableRaisingEvents = true };
                _setupProcess.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                        _setupStdout.Enqueue(args.Data);
                };
                _setupProcess.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                        _setupStderr.Enqueue(args.Data);
                };
                _setupProcess.Exited += (_, __) =>
                {
                    int code = -1;
                    try { code = _setupProcess.ExitCode; } catch { }
                    _setupStdout.Enqueue("__FM_SETUP_EXIT__|" + code.ToString(Invariant));
                };

                if (!_setupProcess.Start())
                    throw new InvalidOperationException("Process.Start returned false.");

                _setupProcess.BeginOutputReadLine();
                _setupProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                FailSetup("Could not start automatic setup: " + ex.Message);
            }
        }

        private void DrainSetupOutput()
        {
            while (_setupStdout.TryDequeue(out string line))
            {
                if (line.StartsWith("__FM_SETUP_EXIT__|", StringComparison.Ordinal))
                {
                    string[] parts = line.Split('|');
                    int code = parts.Length > 1 && int.TryParse(parts[1], out int parsed) ? parsed : -1;
                    FinalizeSetup(code);
                    continue;
                }

                ParseSetupProgress(line);
            }

            int errors = 0;
            while (_setupStderr.TryDequeue(out string line))
            {
                SetupDetail = line;
                if (errors++ < 3)
                    UnityEngine.Debug.LogWarning("[MaleCNS Setup] " + line);
            }
        }

        private void ParseSetupProgress(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            SetupDetail = line.Trim();

            if (line.StartsWith("FM_STAGE|", StringComparison.Ordinal))
            {
                string[] parts = line.Split('|');
                if (parts.Length >= 3 && float.TryParse(parts[1], NumberStyles.Float, Invariant, out float percent))
                    SetupProgress = Mathf.Max(SetupProgress, Mathf.Clamp01(percent / 100f));
                if (parts.Length >= 3)
                    SetupStage = parts[2];
                if (parts.Length >= 4)
                    SetupDetail = parts[3];
                return;
            }

            if (line.Contains("Installing Python packages", StringComparison.OrdinalIgnoreCase))
            {
                SetupStage = "INSTALLING PYTHON RUNTIME";
                SetupProgress = Mathf.Max(SetupProgress, 0.07f);
            }
            else if (line.Contains("Downloading body-annotations", StringComparison.OrdinalIgnoreCase))
            {
                _setupDownloadIndex = 0;
                SetupStage = "DOWNLOADING NEURON ANNOTATIONS";
                SetupProgress = Mathf.Max(SetupProgress, 0.14f);
            }
            else if (line.Contains("Downloading body-neurotransmitters", StringComparison.OrdinalIgnoreCase))
            {
                _setupDownloadIndex = 1;
                SetupStage = "DOWNLOADING NEUROTRANSMITTER DATA";
                SetupProgress = Mathf.Max(SetupProgress, 0.15f);
            }
            else if (line.Contains("Downloading connectome-weights", StringComparison.OrdinalIgnoreCase))
            {
                _setupDownloadIndex = 2;
                SetupStage = "DOWNLOADING 1 GB CONNECTOME";
                SetupProgress = Mathf.Max(SetupProgress, 0.18f);
            }
            else if (line.Contains("Reading annotations", StringComparison.OrdinalIgnoreCase))
            {
                SetupStage = "READING 166K NEURONS";
                SetupProgress = Mathf.Max(SetupProgress, 0.72f);
            }
            else if (line.Contains("Streaming full connection graph", StringComparison.OrdinalIgnoreCase))
            {
                SetupStage = "BUILDING 25M-CONNECTION RUNTIME GRAPH";
                SetupProgress = Mathf.Max(SetupProgress, 0.76f);
            }
            else if (line.Contains("Build complete", StringComparison.OrdinalIgnoreCase))
            {
                SetupStage = "FINALIZING MALECNS CACHE";
                SetupProgress = Mathf.Max(SetupProgress, 0.98f);
            }
            else if (line.Contains("MaleCNS v1.0 is ready", StringComparison.OrdinalIgnoreCase))
            {
                SetupStage = "MALECNS v1.0 READY";
                SetupProgress = 1f;
            }

            Match scan = ScanRegex.Match(line);
            if (scan.Success)
            {
                string a = scan.Groups[1].Value.Replace(",", string.Empty);
                string b = scan.Groups[2].Value.Replace(",", string.Empty);
                if (double.TryParse(a, NumberStyles.Integer, Invariant, out double done) &&
                    double.TryParse(b, NumberStyles.Integer, Invariant, out double total) && total > 0)
                {
                    SetupProgress = Mathf.Max(SetupProgress, Mathf.Lerp(0.76f, 0.95f, (float)(done / total)));
                }
            }

            Match percentMatch = PercentRegex.Match(line);
            if (percentMatch.Success && _setupDownloadIndex >= 0 &&
                float.TryParse(percentMatch.Groups[1].Value, NumberStyles.Float, Invariant, out float filePercent))
            {
                float t = Mathf.Clamp01(filePercent / 100f);
                if (_setupDownloadIndex == 0)
                    SetupProgress = Mathf.Max(SetupProgress, Mathf.Lerp(0.14f, 0.15f, t));
                else if (_setupDownloadIndex == 1)
                    SetupProgress = Mathf.Max(SetupProgress, Mathf.Lerp(0.15f, 0.18f, t));
                else
                    SetupProgress = Mathf.Max(SetupProgress, Mathf.Lerp(0.18f, 0.72f, t));
            }
        }

        private void FinalizeSetup(int exitCode)
        {
            SetupInProgress = false;
            DisposeSetupProcess();

            if (exitCode == 0 && HasRuntimeData && File.Exists(GetPythonPath()))
            {
                SetupProgress = 1f;
                SetupStage = "MALECNS v1.0 READY";
                SetupDetail = "Setup complete. Starting the connectome runtime...";
                SetupError = string.Empty;
                StartRuntime();
                return;
            }

            FailSetup("Automatic setup did not complete. Python 3 is required; downloads can be resumed by pressing Play again.");
        }

        private void FailSetup(string message)
        {
            SetupInProgress = false;
            SetupError = message;
            SetupStage = "SETUP NEEDS ATTENTION";
            SetupDetail = message;
            Status = "MALECNS v1.0 / SETUP FAILED";
            IsReady = false;
            UnityEngine.Debug.LogWarning("[FlyMaze] " + message);
            DisposeSetupProcess();
        }

        private void DisposeSetupProcess()
        {
            if (_setupProcess == null)
                return;
            try { _setupProcess.Dispose(); } catch { }
            _setupProcess = null;
        }

        public void StartRuntime()
        {
            if (_process != null && !_process.HasExited)
                return;

            if (!HasRuntimeData || !File.Exists(GetPythonPath()))
            {
                BeginAutomaticSetup();
                return;
            }

            string python = GetPythonPath();
            string script = GetRuntimeScriptPath();
            string cache = GetCacheDirectory();
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

                        if (parts.Length >= 15)
                        {
                            int.TryParse(parts[13], NumberStyles.Integer, Invariant, out int learned);
                            TryFloat(parts[14], out float magnitude);
                            LearnedSynapseCount = Mathf.Max(0, learned);
                            PlasticityMagnitude = Mathf.Max(0f, magnitude);
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

                    if (!_process.WaitForExit(700))
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

        private void StopSetupProcess()
        {
            if (_setupProcess == null)
                return;
            try
            {
                if (!_setupProcess.HasExited)
                    _setupProcess.Kill();
            }
            catch { }
            DisposeSetupProcess();
            SetupInProgress = false;
        }

        private void OnDestroy()
        {
            _quitting = true;
            StopRuntime();
            StopSetupProcess();
        }

        private void OnApplicationQuit()
        {
            _quitting = true;
            StopRuntime();
            StopSetupProcess();
        }

        public static string GetProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string GetCacheDirectory() => Path.Combine(GetProjectRoot(), "Library", "MaleCNS");
        public static string GetWeightsPath() => Path.Combine(GetCacheDirectory(), "malecns_weights.npz");
        public static string GetMetaPath() => Path.Combine(GetCacheDirectory(), "malecns_meta.npz");
        public static string GetPlasticityPath() => Path.Combine(GetCacheDirectory(), "kc_mbon_plasticity_v1.npz");
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
