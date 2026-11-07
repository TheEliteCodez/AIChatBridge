/*
Path/Filename.cs: Services/ChromeSidecar.cs
Author: Gregory King
Date: 2026-02-17
Last Edit: 2026-02-22
License: Private use only by the author. No permission is granted to transfer, redistribute, sublicense, publish, modify, or use this file by any party other than the author. Provided "AS IS" without warranties or liability.
Desc: Source file for standalone ChromeSidecar runtime/application logic, ported out of CryptoDayTraderSuite.
Classes: SidecarStatus, ChromeSidecar, SidecarAiResponse, AiProvider
Functions: ShowWindow, IsWindowVisible, SetLaunchChromeHidden, SetManagedChromeVisible, IsManagedChromeVisible, SetStatus, MarkDisconnected, ConnectAsync, ConnectInternalAsync, GetAvailableServicesAsync, QueryAcrossAvailableServicesAsync, EvaluateJsAsync, RegisterPendingResponse, RemovePendingResponse, QueryAIAsync, BuildProviderFallbackOrder, GetNextRoundRobinPrimary, AdvanceRoundRobinCursor, ReadCandidateResponseAsync, IsProviderLikelyStuckAsync, BuildStuckDetectionScript, AttemptRefreshAndResendRecoveryAsync, IsAcceptableResponseCandidate, EvaluateJsWithRetryAsync, PollForModelResponseAsync, AttemptProviderRecoveryAsync, SendAsync, StartReceivePump, StopReceivePump, CancelPendingResponses, ReceivePumpAsync, ReceiveAsync, TryGetMessageId, IsAiTab, GetTabsAsync, EnsureDebugHostReadyAsync, IsDebugHostAvailableAsync, TryStartChromeDebugHost, GetManagedChromeWindowHandles, EnsureManagedChromeHidden, ResolveChromePath, EnsureProviderTabAsync, EnsureAnyAiTabAsync, TryCreateProviderFallbackTabAsync, GetProviderFallbackUrls, GetProviderStartUrl, CreateTabAsync, GetTabTitle, GetTabId, GetTabType, GetTabUrl, GetWebSocketDebuggerUrl, ActivateTabAsync, DetectProvider, ParsePreferredProvider, ProviderToServiceName, DetectModelName, BuildSourceLabel, BuildJsStringLiteral, IsError, IsNoSendStatus, IsPendingSendStatus, ResolvePendingSendStatusAsync, LooksLikeThinkingState, IsLikelyAssistantContent, BuildInjectScript, BuildReadScript, BuildGenericReadScript, BuildStartFreshChatScript, ContainsIgnoreCase, IsActiveTab, Log, Dispose
*/

using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using ChromeSidecarStandalone.Util;

namespace ChromeSidecarStandalone.Services
{
    public enum SidecarStatus
    {
        Disconnected,
        Connecting,
        Connected,
        Error
    }

    public class ChromeSidecar : IDisposable
    {
        private const int QueryTimeoutMs = 30000;
        public sealed class SidecarAiResponse
        {
            public string Raw { get; set; }
            public string Service { get; set; }
            public string Model { get; set; }
            public string SourceLabel { get; set; }
            public string FailureReason { get; set; }
            public bool Succeeded { get { return !string.IsNullOrWhiteSpace(Raw) && string.IsNullOrWhiteSpace(FailureReason); } }
        }

        internal sealed class ManagedChromeOwnership
        {
            public bool LaunchAttempted { get; set; }
            public int ProcessId { get; set; }
        }

        private enum AiProvider
        {
            Unknown,
            ChatGpt,
            Gemini,
            Claude
        }

        private const string DebugBase = "http://localhost:9222";
        private const string DebugUrl = DebugBase + "/json";
        private const string DebugPortArgument = "--remote-debugging-port=9222";
        private const string SidecarUserDataDirectoryName = "CryptoSidecar";
        private const string ChatGptStartUrl = "https://chatgpt.com/";
        private const string GeminiStartUrl = "https://gemini.google.com/";
        private const string ClaudeStartUrl = "https://claude.ai/new";
        // Prefer the providers that consistently return from the managed browser.
        // ChatGPT remains in the rotation, but a non-responsive ChatGPT tab must not
        // stall every runtime AI consumer before Gemini or Claude gets a chance.
        private static readonly AiProvider[] ProviderRoundRobin = new[] { AiProvider.Gemini, AiProvider.Claude, AiProvider.ChatGpt };
        private const int ConnectTimeoutMs = 12000;
        private const int HostProbeTimeoutMs = 5000;
        private const int HostLaunchWaitMs = 9000;
        private const int ManagedWindowHideAttempts = 20;
        private const int ManagedWindowStateRetryMs = 150;
        private const int SwHide = 0;
        private const int SwShow = 5;
        private const int SwRestore = 9;
        private const string ManagedChromeVisibleEventName = @"Local\ChromeSidecar.ManagedChromeVisible";
        private static readonly EventWaitHandle ManagedChromeVisibleEvent = CreateManagedChromeVisibleEvent();
        private static int _managedChromeVisibleFallback;
        private static readonly SemaphoreSlim BrowserQueryLock = new SemaphoreSlim(1, 1);
        private sealed class ProviderBackoffState
        {
            public ProviderBackoffState() { }
            public const decimal BurstCapacity = 3m;
            public int ConsecutiveRateLimits;
            public int TargetRequestsPerHour = 60;
            public int RatePolicyVersion;
            public int SuccessfulRequestsSinceAdjustment;
            public decimal AvailableTokens = BurstCapacity;
            public DateTime LastTokenRefillUtc = DateTime.UtcNow;
            public DateTime NextAttemptUtc;
            public DateTime BackoffUntilUtc;
        }

        public sealed class ProviderRateLimitSnapshot
        {
            public string provider { get; set; }
            public string state { get; set; }
            public int targetRequestsPerHour { get; set; }
            public int successfulRequestsUntilProbe { get; set; }
            public int consecutiveRateLimits { get; set; }
            public DateTime? nextAttemptUtc { get; set; }
            public DateTime? backoffUntilUtc { get; set; }
        }
        private static readonly object ProviderBackoffGate = new object();
        private static readonly Dictionary<AiProvider, ProviderBackoffState> ProviderBackoffs = new Dictionary<AiProvider, ProviderBackoffState>();

        private sealed class SharedProviderBudget : IDisposable
        {
            private readonly FileStream stream;
            private readonly AiProvider provider;
            public SharedProviderBudget(AiProvider provider)
            {
                this.provider = provider;
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChromeSidecar", "runtime", "provider-tabs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, ProviderToServiceName(provider) + "-budget.json");
                while (true)
                {
                    try { stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
                    catch (IOException) { Thread.Sleep(10); }
                }
                try
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                    {
                        var json = reader.ReadToEnd();
                        if (!string.IsNullOrWhiteSpace(json)) ProviderBackoffs[provider] = UtilCompat.JsonDeserialize<ProviderBackoffState>(json);
                    }
                    ProviderBackoffState state;
                    if (!ProviderBackoffs.TryGetValue(provider, out state) || state == null)
                        ProviderBackoffs[provider] = state = new ProviderBackoffState
                        {
                            RatePolicyVersion = 3,
                            TargetRequestsPerHour = 60
                        };
                    if (state.RatePolicyVersion < 2)
                    {
                        if ((provider == AiProvider.ChatGpt || provider == AiProvider.Gemini) && state.RatePolicyVersion == 1)
                            state.TargetRequestsPerHour = Math.Min(240, Math.Max(1, (int)Math.Ceiling(state.TargetRequestsPerHour / 0.85m)));
                        state.RatePolicyVersion = 2;
                    }
                    if (state.RatePolicyVersion < 3)
                    {
                        RecoverNormalProviderRate(state, DateTime.UtcNow);
                        state.RatePolicyVersion = 3;
                    }
                }
                catch { stream.Dispose(); throw; }
            }
            public void Dispose()
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(UtilCompat.JsonSerialize(ProviderBackoffs[provider]));
                    stream.Position = 0; stream.Write(bytes, 0, bytes.Length); stream.SetLength(bytes.Length); stream.Flush();
                }
                finally { stream.Dispose(); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private ClientWebSocket _ws;
        private volatile bool _tabNeedsRecovery;
        private int _consecutiveEvaluationTimeouts;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _queryLock;
        private readonly SemaphoreSlim _sendLock;
        private readonly SemaphoreSlim _connectLock;
        private readonly SemaphoreSlim _tabCreationLock;
        private readonly object _pendingLock = new object();
        private readonly Dictionary<int, TaskCompletionSource<string>> _pendingResponses;
        private int _msgId;
        private CancellationTokenSource _receivePumpCts;
        private Task _receivePumpTask;

        private AiProvider _provider = AiProvider.Unknown;
        private string _targetTitle;
        private string _targetUrl;
        private string _targetTabId;
        private string _lastAiResponse = string.Empty;
        private bool _chromeLaunchAttempted;
        private int _roundRobinCursor;
        private int _managedChromeProcessId;
        private bool _launchChromeHidden = true;

        public event Action<string> OnLog;
        public event Action<SidecarStatus> StatusChanged;

        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;
        public SidecarStatus Status { get; private set; } = SidecarStatus.Disconnected;

        public string CurrentServiceName => ProviderToServiceName(_provider);
        public string CurrentModelName => DetectModelName(_targetTitle, _provider);
        public string CurrentSourceLabel => BuildSourceLabel(CurrentServiceName, CurrentModelName);
        public string LastFailureReason { get; private set; }
        public bool LaunchChromeHidden => _launchChromeHidden;
        public bool KeepManagedChromeAlive { get; set; }
        public bool UseFreshChatForQueries { get; set; }

        public static IList<ProviderRateLimitSnapshot> GetProviderRateLimitSnapshots()
        {
            var now = DateTime.UtcNow;
            lock (ProviderBackoffGate)
            {
                return new[] { AiProvider.ChatGpt, AiProvider.Gemini, AiProvider.Claude }
                    .Select(provider =>
                    {
                        using (var sharedBudget = new SharedProviderBudget(provider))
                        {
                        ProviderBackoffState state;
                        if (!ProviderBackoffs.TryGetValue(provider, out state))
                            state = new ProviderBackoffState();
                        RefillProviderTokens(state, now);
                        var allowedUtc = state.BackoffUntilUtc > state.NextAttemptUtc
                            ? state.BackoffUntilUtc
                            : state.NextAttemptUtc;
                        return new ProviderRateLimitSnapshot
                        {
                            provider = ProviderToServiceName(provider),
                            state = state.BackoffUntilUtc > now ? "Backoff" : (state.NextAttemptUtc > now ? "Paced" : "Ready"),
                            targetRequestsPerHour = state.TargetRequestsPerHour,
                            successfulRequestsUntilProbe = Math.Max(0, 5 - state.SuccessfulRequestsSinceAdjustment),
                            consecutiveRateLimits = state.ConsecutiveRateLimits,
                            nextAttemptUtc = allowedUtc > now ? (DateTime?)allowedUtc : null,
                            backoffUntilUtc = state.BackoffUntilUtc > now ? (DateTime?)state.BackoffUntilUtc : null
                        };
                        }
                    })
                    .ToList();
            }
        }

        public void SetLaunchChromeHidden(bool hidden)
        {
            _launchChromeHidden = hidden;
            Log(hidden
                ? "Chrome launch mode set to hidden."
                : "Chrome launch mode set to visible.");
        }

        public bool SetManagedChromeVisible(bool visible)
        {
            SetManagedChromeVisibilityRequested(visible);
            _launchChromeHidden = !visible;
            if (visible)
            {
                var handles = GetManagedChromeWindowHandles(false);
                if (handles.Count == 0)
                {
                    Log("Managed Chrome has no visible top-level window; restarting it as a normal login window.");
                    StopManagedChrome(true);
                    TryStartChromeDebugHost(ChatGptStartUrl, true);
                    handles = GetManagedChromeWindowHandles(true);
                }
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    handles = GetManagedChromeWindowHandles(false);
                    foreach (var handle in handles)
                    {
                        ShowWindow(handle, SwRestore);
                        if (!IsWindowVisible(handle)) ShowWindow(handle, SwShow);
                        if (attempt == 0) SetForegroundWindow(handle);
                    }
                    if (attempt < 9) Thread.Sleep(200);
                }
            }
            else
            {
                EnsureManagedChromeHidden(true, true);
            }
            return visible ? IsManagedChromeVisible() : !IsManagedChromeVisible();
        }

        public bool IsManagedChromeVisible()
        {
            return GetManagedChromeWindowHandles(false).Any(handle => IsWindowVisible(handle));
        }

        public ChromeSidecar()
        {
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs);
            _queryLock = new SemaphoreSlim(1, 1);
            _sendLock = new SemaphoreSlim(1, 1);
            _connectLock = new SemaphoreSlim(1, 1);
            _tabCreationLock = new SemaphoreSlim(1, 1);
            _pendingResponses = new Dictionary<int, TaskCompletionSource<string>>();
        }

        private static EventWaitHandle CreateManagedChromeVisibleEvent()
        {
            try
            {
                return new EventWaitHandle(false, EventResetMode.ManualReset, ManagedChromeVisibleEventName);
            }
            catch
            {
                return null;
            }
        }

        private static void SetManagedChromeVisibilityRequested(bool visible)
        {
            Interlocked.Exchange(ref _managedChromeVisibleFallback, visible ? 1 : 0);
            try
            {
                if (visible) ManagedChromeVisibleEvent?.Set();
                else ManagedChromeVisibleEvent?.Reset();
            }
            catch
            {
            }
        }

        private static bool IsManagedChromeVisibilityRequested()
        {
            try
            {
                if (ManagedChromeVisibleEvent != null) return ManagedChromeVisibleEvent.WaitOne(0);
            }
            catch
            {
            }
            return Volatile.Read(ref _managedChromeVisibleFallback) == 1;
        }

        private void SetStatus(SidecarStatus status)
        {
            if (Status != status)
            {
                Status = status;
                StatusChanged?.Invoke(Status);
            }
        }

        private void MarkDisconnected(string reason)
        {
            if (Status != SidecarStatus.Disconnected)
            {
                if (string.IsNullOrWhiteSpace(reason))
                {
                    Log("Disconnected");
                }
                else
                {
                    Log("Disconnected: " + reason);
                }
            }

            SetStatus(SidecarStatus.Disconnected);
        }

        public async Task<bool> ConnectAsync()
        {
            await BrowserQueryLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ConnectInternalAsync(AiProvider.Unknown).ConfigureAwait(false);
            }
            finally
            {
                BrowserQueryLock.Release();
            }
        }

        public async Task<bool> ConnectAsync(string preferredService)
        {
            await BrowserQueryLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ConnectInternalAsync(ParsePreferredProvider(preferredService)).ConfigureAwait(false);
            }
            finally
            {
                BrowserQueryLock.Release();
            }
        }

        private async Task<bool> ConnectInternalAsync(AiProvider preferredProvider)
        {
            await _connectLock.WaitAsync().ConfigureAwait(false);
            SetStatus(SidecarStatus.Connecting);

            try
            {
                using (var overallTimeoutCts = new CancellationTokenSource(ConnectTimeoutMs + HostLaunchWaitMs + 4000))
                {
                    var token = overallTimeoutCts.Token;

                    StopReceivePump();
                    try { _ws?.Dispose(); } catch { }
                    _ws = null;

                    await EnsureDebugHostReadyAsync(preferredProvider, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    var tabs = await GetTabsAsync(token).ConfigureAwait(false);

                    if (preferredProvider != AiProvider.Unknown)
                    {
                        await EnsureProviderTabAsync(preferredProvider, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        tabs = await GetTabsAsync(token).ConfigureAwait(false);
                    }

                    var aiTabs = tabs.Where(IsAiTab).ToList();
                    if (aiTabs.Count == 0)
                    {
                        aiTabs = await EnsureAnyAiTabAsync(preferredProvider, token).ConfigureAwait(false);
                        if (aiTabs.Count == 0)
                        {
                            Log("No AI tab found and automatic tab creation failed.");
                            MarkDisconnected("No AI tab found after automatic creation attempt.");
                            return false;
                        }
                    }

                    dynamic target = null;

                    if (preferredProvider != AiProvider.Unknown)
                    {
                        var preferredTabs = aiTabs
                            .Where(t => DetectProvider(GetTabTitle(t), GetTabUrl(t)) == preferredProvider)
                            .ToList();
                        var suitablePreferredTabs = preferredTabs
                            .Where(t => IsSuitableProviderTab(t, preferredProvider))
                            .ToList();
                        target = suitablePreferredTabs.FirstOrDefault(IsActiveTab)
                            ?? suitablePreferredTabs.FirstOrDefault()
                            ?? preferredTabs.FirstOrDefault(IsActiveTab)
                            ?? preferredTabs.FirstOrDefault();
                    }

                    if (target == null)
                    {
                        target = aiTabs.FirstOrDefault(IsActiveTab) ?? aiTabs.FirstOrDefault();
                    }

                    if (target == null)
                    {
                        MarkDisconnected("No target AI tab could be selected.");
                        return false;
                    }

                    var wsUrl = GetWebSocketDebuggerUrl(target);
                    if (string.IsNullOrEmpty(wsUrl))
                    {
                        Log("Target tab does not have a WebSocket URL.");
                        MarkDisconnected("Target AI tab missing WebSocket debugger URL.");
                        return false;
                    }

                    _targetTitle = GetTabTitle(target);
                    _targetUrl = GetTabUrl(target);
                    _targetTabId = GetTabId(target);
                    _provider = DetectProvider(_targetTitle, _targetUrl);

                    if (!await ProbeTabAsync(wsUrl, token).ConfigureAwait(false))
                    {
                        Log("[TabRecovery] Unresponsive or crashed tab detected: " + _targetTabId);
                        var replacedTabId = _targetTabId;
                        await CloseTabAsync(_targetTabId, token).ConfigureAwait(false);
                        if (!await CreateTabAsync(GetProviderStartUrl(_provider), token).ConfigureAwait(false))
                            return false;
                        tabs = await GetTabsAsync(token).ConfigureAwait(false);
                        target = tabs.FirstOrDefault(t => IsAiTab(t) && GetTabId(t) != replacedTabId
                            && DetectProvider(GetTabTitle(t), GetTabUrl(t)) == _provider);
                        if (target == null) return false;
                        wsUrl = GetWebSocketDebuggerUrl(target);
                        _targetTabId = GetTabId(target);
                        _targetTitle = GetTabTitle(target);
                        _targetUrl = GetTabUrl(target);
                        Log("[TabRecovery] Replacement tab created: " + _targetTabId);
                    }
                    _tabNeedsRecovery = false;
                    _consecutiveEvaluationTimeouts = 0;

                    await ActivateTabAsync(target, token).ConfigureAwait(false);
                    await PruneExtraAiTabsAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    Log("Connecting to " + _targetTitle + " [" + _provider + "]...");

                    _ws = new ClientWebSocket();
                    await _ws.ConnectAsync(new Uri(wsUrl), token).ConfigureAwait(false);
                    StartReceivePump();
                    await KeepProviderPageActiveAsync().ConfigureAwait(false);

                    Log("Connected via CDP.");
                    SetStatus(SidecarStatus.Connected);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                Log("Connection failed: timed out.");
                SetStatus(SidecarStatus.Error);
                MarkDisconnected("Connection timeout.");
                return false;
            }
            catch (Exception ex)
            {
                Log("Connection failed: " + ex.Message);
                SetStatus(SidecarStatus.Error);
                MarkDisconnected("Connection failed.");
                return false;
            }
            finally
            {
                if (Status == SidecarStatus.Connecting)
                {
                    MarkDisconnected("Connection attempt did not complete.");
                }

                _connectLock.Release();
            }
        }

        public async Task<List<string>> GetAvailableServicesAsync()
        {
            var services = new List<string>();
            try
            {
                await EnsureProviderTabAsync(AiProvider.ChatGpt);
                await EnsureProviderTabAsync(AiProvider.Gemini);
                await EnsureProviderTabAsync(AiProvider.Claude);

                var tabs = await GetTabsAsync();
                foreach (var tab in tabs)
                {
                    if (!IsAiTab(tab)) continue;

                    var provider = DetectProvider(GetTabTitle(tab), GetTabUrl(tab));
                    var service = ProviderToServiceName(provider);
                    if (!string.IsNullOrWhiteSpace(service) && !services.Contains(service))
                    {
                        services.Add(service);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("GetAvailableServices failed: " + ex.Message);
            }

            return services;
        }

        public async Task<List<SidecarAiResponse>> QueryAcrossAvailableServicesAsync(string prompt)
        {
            var results = new List<SidecarAiResponse>();
            var startProvider = GetNextRoundRobinPrimary();
            var providers = BuildProviderFallbackOrder(startProvider, false);
            var maxProviders = ResolveMaxConsensusProviders();

            foreach (var provider in providers)
            {
                LastFailureReason = string.Empty;
                // Provider-policy reduction (opt-in): cap how many providers are consulted
                // for multi-provider consensus. Default 0 = all available (current behavior);
                // set CDTS_AI_MAX_CONSENSUS_PROVIDERS=1..3 to trade breadth for cost/latency.
                if (maxProviders > 0 && results.Count(x => x.Succeeded) >= maxProviders)
                {
                    Log("Consensus provider cap reached (" + maxProviders + "); stopping at " + ProviderToServiceName(provider) + ".");
                    break;
                }

                var service = ProviderToServiceName(provider);
                try
                {
                    var connected = await ConnectAsync(service);
                    if (!connected)
                    {
                        Log("Skipping " + service + ": unable to connect.");
                        results.Add(new SidecarAiResponse
                        {
                            Service = service,
                            Model = CurrentModelName,
                            SourceLabel = service,
                            FailureReason = string.IsNullOrWhiteSpace(LastFailureReason) ? "provider-connect-failed:" + service : LastFailureReason
                        });
                        continue;
                    }

                    // Give freshly-opened Gemini/Claude tabs time to fully load before attempting prompt injection
                    if (provider == AiProvider.Gemini || provider == AiProvider.Claude)
                        await WaitForProviderPageReadyAsync(provider, provider == AiProvider.Gemini ? 7000 : 6000);

                    var raw = await QueryAIAsync(prompt, provider);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        Log("No response captured from " + service + ".");
                        results.Add(new SidecarAiResponse
                        {
                            Service = service,
                            Model = CurrentModelName,
                            SourceLabel = CurrentSourceLabel,
                            FailureReason = string.IsNullOrWhiteSpace(LastFailureReason) ? "provider-response-timeout:" + service : LastFailureReason
                        });
                        continue;
                    }

                    if (raw.StartsWith("CDTS_RATE_LIMITED", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new SidecarAiResponse
                        {
                            Raw = raw,
                            Service = service,
                            Model = CurrentModelName,
                            SourceLabel = CurrentSourceLabel,
                            FailureReason = string.IsNullOrWhiteSpace(LastFailureReason) ? "rate-limited:" + service : LastFailureReason
                        });
                        continue;
                    }

                    results.Add(new SidecarAiResponse
                    {
                        Raw = raw,
                        Service = CurrentServiceName,
                        Model = CurrentModelName,
                        SourceLabel = CurrentSourceLabel,
                        FailureReason = string.Empty
                    });
                }
                catch (Exception ex)
                {
                    Log("Query failed for " + service + ": " + ex.Message);
                    results.Add(new SidecarAiResponse
                    {
                        Service = service,
                        Model = CurrentModelName,
                        SourceLabel = service,
                        FailureReason = "provider-error:" + service + ":" + ex.Message
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// Opt-in provider-breadth cap for multi-provider consensus queries.
        /// CDTS_AI_MAX_CONSENSUS_PROVIDERS=0 (default) consults all available providers;
        /// 1..3 reduces cost/latency by stopping after N successful responses.
        /// </summary>
        private static int ResolveMaxConsensusProviders()
        {
            var env = Environment.GetEnvironmentVariable("CDTS_AI_MAX_CONSENSUS_PROVIDERS");
            if (string.IsNullOrWhiteSpace(env)) return 0;
            int parsed;
            return int.TryParse(env, out parsed) && parsed >= 1 ? Math.Min(parsed, 3) : 0;
        }

        private async Task<bool> ProbeTabAsync(string websocketUrl, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var socket = new ClientWebSocket())
            {
                timeout.CancelAfter(4000);
                try
                {
                    await socket.ConnectAsync(new Uri(websocketUrl), timeout.Token).ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":\"1\",\"returnByValue\":true}}");
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                    while (socket.State == WebSocketState.Open)
                    {
                        var response = await ReceiveAsync(socket, timeout.Token).ConfigureAwait(false);
                        int id;
                        if (TryGetMessageId(response, out id) && id == 1)
                            return response.IndexOf("\"error\"", StringComparison.Ordinal) < 0;
                    }
                }
                catch (Exception) when (!token.IsCancellationRequested) { }
                token.ThrowIfCancellationRequested();
                return false;
            }
        }

        public async Task<string> EvaluateJsAsync(string script, int responseTimeoutMs = 12000)
        {
            if (_tabNeedsRecovery) return string.Empty;
            if (!IsConnected) return string.Empty;

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var id = RegisterPendingResponse(tcs);

            var command = new
            {
                id = id,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = script,
                    returnByValue = true,
                    awaitPromise = true
                }
            };

            var request = JsonConvert.SerializeObject(command);
            var sendOk = await SendAsync(request, 3000);
            if (!sendOk)
            {
                RemovePendingResponse(id);
                Log("EvaluateJsAsync send failed/timed out.");
                return "error: send-timeout";
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Math.Max(1000, responseTimeoutMs)));
            if (completed != tcs.Task)
            {
                RemovePendingResponse(id);

                Log("EvaluateJsAsync timeout waiting for matching CDP response.");
                if (Interlocked.Increment(ref _consecutiveEvaluationTimeouts) >= 2)
                {
                    _tabNeedsRecovery = true;
                    LastFailureReason = "provider-tab-unresponsive:" + ProviderToServiceName(_provider);
                }
                return string.Empty;
            }

            _consecutiveEvaluationTimeouts = 0;

            var responseJson = await tcs.Task;
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return string.Empty;
            }

            try
            {
                var dict = UtilCompat.JsonDeserialize<Dictionary<string, object>>(responseJson);
                if (dict != null && dict.ContainsKey("result"))
                {
                    var result = dict["result"] as Dictionary<string, object>;
                    if (result != null && result.ContainsKey("result"))
                    {
                        var inner = result["result"] as Dictionary<string, object>;
                        if (inner != null && inner.ContainsKey("value"))
                        {
                            return inner["value"] == null ? string.Empty : inner["value"].ToString();
                        }
                    }
                }

                if (dict != null && dict.ContainsKey("error"))
                {
                    return "error: " + dict["error"];
                }
            }
            catch { }

            return responseJson;
        }

        private async Task KeepProviderPageActiveAsync()
        {
            var lifecycleActive = await SendCdpCommandAsync(
                "Page.setWebLifecycleState",
                new Dictionary<string, object> { { "state", "active" } },
                3500).ConfigureAwait(false);
            var focusEmulated = await SendCdpCommandAsync(
                "Emulation.setFocusEmulationEnabled",
                new Dictionary<string, object> { { "enabled", true } },
                3500).ConfigureAwait(false);
            if (!lifecycleActive || !focusEmulated)
            {
                Log("Provider page lifecycle activation was incomplete.");
            }
        }

        private async Task<bool> SendCdpCommandAsync(string method, object parameters, int responseTimeoutMs)
        {
            if (!IsConnected || string.IsNullOrWhiteSpace(method)) return false;

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var id = RegisterPendingResponse(tcs);
            var command = new Dictionary<string, object>
            {
                { "id", id },
                { "method", method },
                { "params", parameters ?? new Dictionary<string, object>() }
            };
            if (!await SendAsync(JsonConvert.SerializeObject(command), 3000).ConfigureAwait(false))
            {
                RemovePendingResponse(id);
                return false;
            }

            var completed = await Task.WhenAny(
                tcs.Task,
                Task.Delay(Math.Max(1000, responseTimeoutMs))).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                RemovePendingResponse(id);
                return false;
            }

            var responseJson = await tcs.Task.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseJson)) return false;
            try
            {
                var response = UtilCompat.JsonDeserialize<Dictionary<string, object>>(responseJson);
                return response != null
                    && response.ContainsKey("result")
                    && !response.ContainsKey("error");
            }
            catch
            {
                return false;
            }
        }

        private int RegisterPendingResponse(TaskCompletionSource<string> pendingResponse)
        {
            if (pendingResponse == null)
            {
                throw new ArgumentNullException(nameof(pendingResponse));
            }

            lock (_pendingLock)
            {
                var id = Interlocked.Increment(ref _msgId);
                if (id <= 0)
                {
                    _msgId = 0;
                    id = Interlocked.Increment(ref _msgId);
                }

                _pendingResponses[id] = pendingResponse;
                return id;
            }
        }

        private void RemovePendingResponse(int id)
        {
            lock (_pendingLock)
            {
                _pendingResponses.Remove(id);
            }
        }

        /// <summary>
        /// Polls document.readyState via CDP until 'complete' or <paramref name="timeoutMs"/> expires.
        /// Prevents prompt injection into a partially-loaded Gemini/Claude tab.
        /// </summary>
        private async Task WaitForProviderPageReadyAsync(AiProvider provider, int timeoutMs = 12000)
        {
            // Phase 1: wait for document.readyState === 'complete'
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Min(timeoutMs, 5000));
            const int pollIntervalMs = 350;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var state = await EvaluateJsAsync("window.__cdts_navigation_pending ? 'loading' : document.readyState", 1500).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(state) &&
                        state.IndexOf("complete", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        break;
                    }
                }
                catch { /* page not yet interactive — keep polling */ }
                await Task.Delay(pollIntervalMs).ConfigureAwait(false);
            }

            // Phase 2: for AI SPAs also wait for the actual input element to appear,
            // since React/Angular components render after readyState === 'complete'.
            if (provider == AiProvider.Claude || provider == AiProvider.Gemini || provider == AiProvider.ChatGpt)
            {
                var inputProbe = provider == AiProvider.Claude
                    ? @"(function(){return !!(document.querySelector('div.ProseMirror[contenteditable=""true""]')||document.querySelector('div[contenteditable=""true""][translate=""no""]')||document.querySelector('div[contenteditable=""true""][data-testid*=""chat-input""]')||document.querySelector('div[contenteditable=""true""]'));})();"
                    : provider == AiProvider.Gemini
                        ? @"(function(){return !!(document.querySelector('rich-textarea div[contenteditable=""true""]')||document.querySelector('div.ql-editor[contenteditable=""true""]')||document.querySelector('div[contenteditable=""true""]'));})();"
                        : @"(function(){return !!(document.querySelector('#prompt-textarea')||document.querySelector('[data-testid=""composer-textarea""]')||document.querySelector('div.ProseMirror[contenteditable=""true""]')||document.querySelector('div[contenteditable=""true""][data-testid*=""composer""]')||document.querySelector('div[contenteditable=""true""][aria-label*=""Message""]')||document.querySelector('textarea')||document.querySelector('div[contenteditable=""true""]'));})();";
                inputProbe = "!window.__cdts_navigation_pending && " + inputProbe.TrimEnd(';');

                var openChatGptComposer = @"(function(){try{var has=!!(document.querySelector('#prompt-textarea')||document.querySelector('[data-testid=""composer-textarea""]')||document.querySelector('div.ProseMirror[contenteditable=""true""]')||document.querySelector('textarea')||document.querySelector('div[contenteditable=""true""]'));if(has)return 'already';if(location.href.indexOf('/apps')>=0||location.href.indexOf('/gpts')>=0||location.href.indexOf('/library')>=0||location.href.indexOf('/store')>=0){location.href='https://chatgpt.com/';return 'navigate-root';}var nodes=document.querySelectorAll('a,button,[role=""button""]');for(var i=0;i<nodes.length;i++){var n=nodes[i];var text=((n.innerText||n.textContent||n.getAttribute('aria-label')||'')+'').trim().toLowerCase();var href=((n.getAttribute('href')||'')+'').trim();if(text==='new chat'||text.indexOf('new chat')>=0||href==='/'){try{n.click();}catch(e){}setTimeout(function(){try{if(!document.querySelector('#prompt-textarea')) location.href='https://chatgpt.com/';}catch(e){}},250);return 'clicked';}}location.href='https://chatgpt.com/';return 'navigate-root';}catch(e){return 'error:'+e.message;}})();";

                var spaDeadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs - 5000));
                while (DateTime.UtcNow < spaDeadline)
                {
                    try
                    {
                        if (provider == AiProvider.ChatGpt)
                        {
                            var opened = await EvaluateJsAsync(openChatGptComposer, 1500).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(opened)
                                && (opened.IndexOf("clicked", StringComparison.OrdinalIgnoreCase) >= 0 || opened.IndexOf("navigate-root", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                Log("ChatGPT composer navigation attempted while waiting for input: " + opened);
                                await Task.Delay(opened.IndexOf("navigate-root", StringComparison.OrdinalIgnoreCase) >= 0 ? 6000 : 2500).ConfigureAwait(false);
                            }
                        }

                        var found = await EvaluateJsAsync(inputProbe, 1500).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(found) &&
                            found.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Log(ProviderToServiceName(provider) + " input element is ready.");
                            return;
                        }
                    }
                    catch { }
                    await Task.Delay(pollIntervalMs).ConfigureAwait(false);
                }

                Log("SPA input element not detected for " + ProviderToServiceName(provider) + "; proceeding anyway.");
            }
        }

        public async Task<string> QueryAIAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                Log("[Query] Skipped: prompt is empty");
                return string.Empty;
            }
            Log("[Query] Starting query with prompt length=" + prompt.Length);
            var result = await QueryAIWithTimeoutAsync(prompt, AiProvider.Unknown, false);
            if (string.IsNullOrWhiteSpace(result) || result.StartsWith("CDTS_RATE_LIMITED", StringComparison.OrdinalIgnoreCase))
            {
                Log("[Query] FAILED: No response received");
            }
            else
            {
                Log("[Query] SUCCESS: Response length=" + result.Length);
            }
            return result;
        }

        public async Task<string> QueryAIAsync(string prompt, string preferredService)
        {
            LastFailureReason = string.Empty;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                Log("[Query] Skipped: prompt is empty");
                LastFailureReason = "prompt-empty";
                return string.Empty;
            }

            var provider = ParsePreferredProvider(preferredService);
            if (provider == AiProvider.Unknown)
            {
                return await QueryAIAsync(prompt).ConfigureAwait(false);
            }

            Log("[Query] Starting query with prompt length=" + prompt.Length + " preferredProvider=" + ProviderToServiceName(provider));
            var result = await QueryAIWithTimeoutAsync(prompt, provider, true).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result) || result.StartsWith("CDTS_RATE_LIMITED", StringComparison.OrdinalIgnoreCase))
            {
                Log("[Query] FAILED: preferredProvider=" + ProviderToServiceName(provider) + " reason=" + LastFailureReason);
            }
            else
            {
                Log("[Query] SUCCESS: Response length=" + result.Length + " preferredProvider=" + ProviderToServiceName(provider));
            }
            return result;
        }

        private async Task<string> QueryAIAsync(string prompt, AiProvider preferredProvider)
        {
            TimeSpan wait;
            string pacingReason;
            int targetPerHour;
            if (preferredProvider != AiProvider.Unknown && !TryReserveProviderAttempt(preferredProvider, out wait, out pacingReason, out targetPerHour))
            {
                LastFailureReason = pacingReason + ":" + ProviderToServiceName(preferredProvider)
                    + ":retry-in-seconds=" + Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
                Log("[RateLimit] Skipping " + ProviderToServiceName(preferredProvider)
                    + " target=" + targetPerHour + "/hour reason=" + LastFailureReason + ".");
                return "CDTS_RATE_LIMITED:" + LastFailureReason;
            }

            var queryStarted = DateTime.UtcNow;
            string result = null;
            string queryError = null;
            try
            {
                result = await QueryAIAsync(prompt, preferredProvider, preferredProvider != AiProvider.Unknown).ConfigureAwait(false);
                var recoveryProvider = preferredProvider == AiProvider.Unknown ? _provider : preferredProvider;
                if (string.IsNullOrWhiteSpace(result) && _tabNeedsRecovery && recoveryProvider != AiProvider.Unknown)
                {
                    Log("[TabRecovery] Reconnecting and resubmitting the interrupted request once.");
                    await WaitForProviderCapacityAsync(ProviderToServiceName(recoveryProvider)).ConfigureAwait(false);
                    if (TryReserveProviderAttempt(recoveryProvider, out wait, out pacingReason, out targetPerHour))
                        result = await QueryAIAsync(prompt, recoveryProvider, true).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { queryError = ex.GetType().Name; throw; }
            finally
            {
                try
                {
                    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromeSidecar", "data", "news-intelligence", "ai-usage");
                    Directory.CreateDirectory(directory);
                    var row = new { provider = ProviderToServiceName(preferredProvider), startedUtc = queryStarted.ToString("o"),
                        completedUtc = DateTime.UtcNow.ToString("o"), elapsedSeconds = (DateTime.UtcNow - queryStarted).TotalSeconds,
                        inputCharacters = (prompt ?? "").Length, outputCharacters = (result ?? "").Length,
                        outcome = queryError ?? ((result ?? "").StartsWith("CDTS_RATE_LIMITED") ? "rate-limited" : string.IsNullOrWhiteSpace(result) ? "empty" : "response") };
                    File.AppendAllText(Path.Combine(directory, "provider-queries-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".jsonl"),
                        ChromeSidecarStandalone.Util.UtilCompat.JsonSerialize(row) + Environment.NewLine);
                }
                catch (Exception ex) { Log("[Usage] Unable to persist provider query metrics: " + ex.Message); }
            }
            if (preferredProvider != AiProvider.Unknown)
            {
                if ((result ?? string.Empty).StartsWith("CDTS_RATE_LIMITED", StringComparison.OrdinalIgnoreCase))
                    RecordProviderRateLimit(preferredProvider, result);
                else if (!string.IsNullOrWhiteSpace(result))
                    RecordProviderSuccess(preferredProvider);
            }
            return result;
        }

        private async Task<string> QueryAIWithTimeoutAsync(string prompt, AiProvider preferredProvider, bool strictProvider)
        {
            var operation = QueryAIAsync(prompt, preferredProvider, strictProvider);
            var completed = await Task.WhenAny(operation, Task.Delay(QueryTimeoutMs)).ConfigureAwait(false);
            if (completed != operation)
            {
                LastFailureReason = "provider-query-timeout:" + ProviderToServiceName(preferredProvider);
                Log("[Query] FAILED: provider query exceeded " + (QueryTimeoutMs / 1000) + " seconds.");
                return string.Empty;
            }

            return await operation.ConfigureAwait(false);
        }

        public async Task WaitForProviderCapacityAsync(string service)
        {
            var provider = ParsePreferredProvider(service);
            if (provider == AiProvider.Unknown) return;
            while (true)
            {
                TimeSpan wait;
                lock (ProviderBackoffGate)
                using (var sharedBudget = new SharedProviderBudget(provider))
                {
                    ProviderBackoffState state;
                    if (!ProviderBackoffs.TryGetValue(provider, out state) || state == null) return;
                    var now = DateTime.UtcNow;
                    RefillProviderTokens(state, now);
                    wait = state.BackoffUntilUtc > now ? state.BackoffUntilUtc - now : TimeSpan.Zero;
                    if (state.AvailableTokens < 1m)
                    {
                        var refill = TimeSpan.FromSeconds((double)((1m - state.AvailableTokens) * 3600m / Math.Max(1, state.TargetRequestsPerHour)));
                        if (refill > wait) wait = refill;
                    }
                }
                if (wait <= TimeSpan.Zero) return;
                Log("[RateLimit] Waiting for " + service + " capacity: " + Math.Ceiling(wait.TotalSeconds) + "s.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60d, Math.Max(0.1d, wait.TotalSeconds)))).ConfigureAwait(false);
            }
        }

        private static bool TryReserveProviderAttempt(AiProvider provider, out TimeSpan wait, out string reason, out int targetPerHour)
        {
            wait = TimeSpan.Zero;
            reason = "ready";
            targetPerHour = 0;
            if (provider == AiProvider.Unknown) return true;
            lock (ProviderBackoffGate)
                using (var sharedBudget = new SharedProviderBudget(provider))
            {
                ProviderBackoffState state;
                if (!ProviderBackoffs.TryGetValue(provider, out state) || state == null)
                {
                    state = new ProviderBackoffState();
                    ProviderBackoffs[provider] = state;
                }
                var now = DateTime.UtcNow;
                targetPerHour = state.TargetRequestsPerHour;
                RefillProviderTokens(state, now);
                if (state.BackoffUntilUtc > now)
                {
                    wait = state.BackoffUntilUtc - now;
                    reason = "provider-rate-limit-backoff";
                    return false;
                }
                if (state.AvailableTokens < 1m)
                {
                    var seconds = (double)((1m - state.AvailableTokens) * 3600m / Math.Max(1, state.TargetRequestsPerHour));
                    state.NextAttemptUtc = now.AddSeconds(Math.Max(1d, seconds));
                    wait = state.NextAttemptUtc - now;
                    reason = "provider-token-bucket";
                    return false;
                }
                state.AvailableTokens -= 1m;
                state.NextAttemptUtc = state.AvailableTokens < 1m
                    ? now.AddSeconds((double)((1m - state.AvailableTokens) * 3600m / Math.Max(1, state.TargetRequestsPerHour)))
                    : DateTime.MinValue;
                return true;
            }
        }

        private static void RefillProviderTokens(ProviderBackoffState state, DateTime now)
        {
            if (state == null) return;
            if (state.LastTokenRefillUtc == default(DateTime) || state.LastTokenRefillUtc > now)
                state.LastTokenRefillUtc = now;
            var elapsedHours = (decimal)Math.Max(0d, (now - state.LastTokenRefillUtc).TotalHours);
            if (elapsedHours > 0m)
            {
                state.AvailableTokens = Math.Min(ProviderBackoffState.BurstCapacity,
                    state.AvailableTokens + elapsedHours * Math.Max(1, state.TargetRequestsPerHour));
                state.LastTokenRefillUtc = now;
            }
            if (state.AvailableTokens >= 1m && state.BackoffUntilUtc <= now)
                state.NextAttemptUtc = DateTime.MinValue;
        }

        private void RecordProviderRateLimit(AiProvider provider, string reason)
        {
            if (provider == AiProvider.Unknown) return;
            int backoffSeconds;
            int count;
            int previousTarget;
            int newTarget;
            lock (ProviderBackoffGate)
                using (var sharedBudget = new SharedProviderBudget(provider))
            {
                ProviderBackoffState state;
                if (!ProviderBackoffs.TryGetValue(provider, out state) || state == null)
                {
                    state = new ProviderBackoffState();
                    ProviderBackoffs[provider] = state;
                }
                previousTarget = state.TargetRequestsPerHour;
                state.ConsecutiveRateLimits = Math.Min(10, state.ConsecutiveRateLimits + 1);
                state.SuccessfulRequestsSinceAdjustment = 0;
                state.TargetRequestsPerHour = Math.Max(1, state.TargetRequestsPerHour / 2);
                state.AvailableTokens = 0m;
                state.LastTokenRefillUtc = DateTime.UtcNow;
                newTarget = state.TargetRequestsPerHour;
                count = state.ConsecutiveRateLimits;
                backoffSeconds = Math.Min(3600, 60 * (1 << Math.Min(6, count - 1)));
                state.BackoffUntilUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
                state.NextAttemptUtc = state.BackoffUntilUtc;
            }
            Log("[RateLimit] Adaptive provider backoff provider=" + ProviderToServiceName(provider)
                + " target=" + previousTarget + "->" + newTarget + "/hour consecutive=" + count + " backoffSeconds=" + backoffSeconds
                + " reason=" + PreviewForTelemetry(reason, 100) + ".");
        }

        private static bool RecoverNormalProviderRate(ProviderBackoffState state, DateTime now)
        {
            if (state == null || state.TargetRequestsPerHour >= 60
                || state.ConsecutiveRateLimits != 0 || state.SuccessfulRequestsSinceAdjustment < 1
                || state.BackoffUntilUtc > now) return false;
            // Refill elapsed time at the old rate before restoring normal pacing.
            // A successful post-backoff request ends the quota episode; requiring
            // five successes at 1/hour otherwise strands recovery for many hours.
            RefillProviderTokens(state, now);
            state.TargetRequestsPerHour = 60;
            state.SuccessfulRequestsSinceAdjustment = 0;
            state.NextAttemptUtc = state.AvailableTokens < 1m
                ? now.AddSeconds((double)((1m - state.AvailableTokens) * 60m))
                : DateTime.MinValue;
            return true;
        }

        private void RecordProviderSuccess(AiProvider provider)
        {
            if (provider == AiProvider.Unknown) return;
            int previousTarget = 0;
            int newTarget = 0;
            lock (ProviderBackoffGate)
                using (var sharedBudget = new SharedProviderBudget(provider))
            {
                ProviderBackoffState state;
                if (!ProviderBackoffs.TryGetValue(provider, out state) || state == null) return;
                previousTarget = state.TargetRequestsPerHour;
                state.ConsecutiveRateLimits = 0;
                state.BackoffUntilUtc = DateTime.MinValue;
                state.SuccessfulRequestsSinceAdjustment++;
                var maximumRate = 240;
                if (!RecoverNormalProviderRate(state, DateTime.UtcNow)
                    && state.SuccessfulRequestsSinceAdjustment >= 5 && state.TargetRequestsPerHour < maximumRate)
                {
                    state.TargetRequestsPerHour = Math.Min(maximumRate, previousTarget + Math.Max(1, previousTarget / 10));
                    state.SuccessfulRequestsSinceAdjustment = 0;
                }
                newTarget = state.TargetRequestsPerHour;
            }
            if (newTarget > previousTarget)
                Log("[RateLimit] Adaptive provider target increased provider=" + ProviderToServiceName(provider)
                    + " target=" + previousTarget + "->" + newTarget + "/hour after successful responses.");
        }

        private async Task<string> QueryAIAsync(string prompt, AiProvider preferredProvider, bool strictProvider)
        {
            var startedUtc = DateTime.UtcNow;
            await BrowserQueryLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var leaseProvider = preferredProvider == AiProvider.Unknown ? GetNextRoundRobinPrimary() : preferredProvider;
                using (var providerLease = await AcquireProviderTabLeaseAsync(leaseProvider).ConfigureAwait(false))
                {
                var result = await QueryAIAsyncCore(prompt, leaseProvider, strictProvider, startedUtc).ConfigureAwait(false);
                EmitResponseTelemetry(startedUtc, preferredProvider, result);
                return result;
                }
            }
            finally
            {
                BrowserQueryLock.Release();
            }
        }

        private async Task<FileStream> AcquireProviderTabLeaseAsync(AiProvider provider)
        {
            // Queue workers are separate processes sharing the same authenticated tab.
            // File sharing coordinates only callers targeting the same provider.
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChromeSidecar", "runtime", "provider-tabs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ProviderToServiceName(provider) + ".lock");
            var waiting = false;
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    if (!waiting) { Log("[Query] Waiting for shared provider tab: " + provider); waiting = true; }
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Structured response telemetry for every sidecar query: provider, latency,
        /// response length, and a head/tail preview. Logs even on empty responses so
        /// degenerate/truncated output is observable across all AI dispatch points.
        /// </summary>
        private void EmitResponseTelemetry(DateTime startedUtc, AiProvider provider, string raw)
        {
            var elapsedMs = (DateTime.UtcNow - startedUtc).TotalMilliseconds;
            var len = raw == null ? 0 : raw.Length;
            var head = PreviewForTelemetry(raw, 120);
            var tail = raw != null && raw.Length > 120
                ? raw.Substring(raw.Length - 80).Replace("\r", " ").Replace("\n", " ").Trim()
                : string.Empty;
            Log("[Telemetry] query provider=" + ProviderToServiceName(provider)
                + " elapsedMs=" + elapsedMs.ToString("0")
                + " bytes=" + len
                + (string.IsNullOrWhiteSpace(raw) ? " empty=1" : string.Empty)
                + " head=" + head
                + (string.IsNullOrWhiteSpace(tail) ? string.Empty : " tail=" + tail));
        }

        private static string PreviewForTelemetry(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= maxLength ? clean : clean.Substring(0, maxLength) + "...";
        }

        private async Task<string> QueryAIAsyncCore(string prompt, AiProvider preferredProvider, bool strictProvider, DateTime startedUtc)
        {
            if (!await _queryLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            {
                LastFailureReason = "sidecar-instance-busy-timeout";
                Log("[Query] FAILED: sidecar instance query lock remained busy for 5 seconds.");
                return string.Empty;
            }
            try
            {
                var primaryProvider = preferredProvider == AiProvider.Unknown ? GetNextRoundRobinPrimary() : preferredProvider;
                if (primaryProvider == AiProvider.Unknown)
                {
                    primaryProvider = AiProvider.ChatGpt;
                }
                Log("[Query] Primary provider: " + primaryProvider);

                var preferredService = ProviderToServiceName(primaryProvider);
                var reconnected = await ConnectInternalAsync(primaryProvider).ConfigureAwait(false);
                if (!reconnected || !IsConnected)
                {
                    Log("Failed to connect provider before query: " + preferredService);
                    return string.Empty;
                }
                await Task.Delay(primaryProvider == AiProvider.Gemini || primaryProvider == AiProvider.Claude ? 700 : 350);

                if (await IsProviderAuthenticationRequiredAsync(primaryProvider).ConfigureAwait(false)
                    && !await TryReauthenticateProviderAsync(primaryProvider).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(LastFailureReason))
                        LastFailureReason = "provider-auth-required:" + ProviderToServiceName(primaryProvider);
                    Log("[Query] FAILED: provider authentication is required for " + ProviderToServiceName(primaryProvider) + ".");
                    return string.Empty;
                }

                var escapedPrompt = BuildJsStringLiteral(prompt ?? string.Empty);

                if (primaryProvider == AiProvider.Gemini || primaryProvider == AiProvider.Claude || primaryProvider == AiProvider.ChatGpt)
                {
                    await WaitForProviderPageReadyAsync(primaryProvider, primaryProvider == AiProvider.ChatGpt ? 18000 : 7000);
                }

                if (strictProvider
                    && UseFreshChatForQueries
                    && (primaryProvider == AiProvider.ChatGpt
                        || primaryProvider == AiProvider.Gemini
                        || primaryProvider == AiProvider.Claude))
                {
                    await StartFreshProviderChatAsync(primaryProvider).ConfigureAwait(false);
                    if (await IsProviderAuthenticationRequiredAsync(primaryProvider).ConfigureAwait(false)
                        && !await TryReauthenticateProviderAsync(primaryProvider).ConfigureAwait(false))
                    {
                        if (string.IsNullOrWhiteSpace(LastFailureReason))
                            LastFailureReason = "provider-auth-required:" + ProviderToServiceName(primaryProvider);
                        Log("[Query] FAILED: provider authentication is required after opening a fresh "
                            + ProviderToServiceName(primaryProvider) + " chat.");
                        return string.Empty;
                    }
                }

                // Pre-injection rate-limit page detection: if the provider page shows a
                // rate-limit / quota / throttle banner, bail immediately with a rate-limit
                // marker so callers can rotate to the next provider without polling.
                var rateLimitCheck = await DetectPageRateLimitAsync(primaryProvider).ConfigureAwait(false);
                if (rateLimitCheck.Item1)
                {
                    LastFailureReason = "rate-limited:" + ProviderToServiceName(primaryProvider)
                        + ":" + (rateLimitCheck.Item2 ?? "detected");
                    Log("[Query] FAILED: rate-limited page detected for " + ProviderToServiceName(primaryProvider)
                        + " reason=" + rateLimitCheck.Item2);
                    return "CDTS_RATE_LIMITED:" + ProviderToServiceName(primaryProvider)
                        + ":" + (rateLimitCheck.Item2 ?? "detected");
                }

                var baselineResponse = (await EvaluateJsAsync(BuildReadScript(primaryProvider), 3500) ?? string.Empty).Trim();

                var injectResult = string.Empty;
                for (var pass = 0; pass < 2; pass++)
                {
                    injectResult = string.Empty;
                    foreach (var provider in BuildProviderFallbackOrder(primaryProvider, strictProvider))
                    {
                        var timeoutMs = provider == AiProvider.Gemini || provider == AiProvider.Claude ? 12000 : 8000;
                        injectResult = await EvaluateJsWithRetryAsync(BuildInjectScript(provider, escapedPrompt), timeoutMs, 2, 400);
                        injectResult = await ResolvePendingSendStatusAsync(provider, injectResult);
                        if (!IsError(injectResult) && !IsNoSendStatus(injectResult) && !string.IsNullOrWhiteSpace(injectResult))
                        {
                            _provider = provider;
                            AdvanceRoundRobinCursor(provider);
                            primaryProvider = provider;
                            break;
                        }
                    }

                    if (!IsError(injectResult) && !IsNoSendStatus(injectResult) && !string.IsNullOrWhiteSpace(injectResult))
                    {
                        break;
                    }

                    if (pass == 0)
                    {
                        Log("Prompt injection first pass failed; reconnecting and retrying...");
                        var reconnectService = ProviderToServiceName(primaryProvider);
                        await ConnectInternalAsync(ParsePreferredProvider(reconnectService)).ConfigureAwait(false);
                        await Task.Delay(primaryProvider == AiProvider.Gemini || primaryProvider == AiProvider.Claude ? 900 : 400);
                    }
                }

                if (IsNoSendStatus(injectResult))
                {
                    LastFailureReason = "provider-prompt-submit-failed:" + ProviderToServiceName(primaryProvider) + ":no-send";
                    Log("Prompt injection did not submit (no-send) for provider " + ProviderToServiceName(primaryProvider) + ".");
                    return string.Empty;
                }

                if (IsError(injectResult) || string.IsNullOrWhiteSpace(injectResult))
                {
                    LastFailureReason = "provider-prompt-submit-failed:" + ProviderToServiceName(primaryProvider);
                    Log("Prompt injection failed: " + injectResult);
                    return string.Empty;
                }

                Log("Prompt injection result: " + (string.IsNullOrWhiteSpace(injectResult) ? "(empty)" : injectResult));
                Log("Prompt injected. Waiting for model response...");

                var direct = await PollForModelResponseAsync(primaryProvider, baselineResponse, strictProvider);
                if (string.IsNullOrWhiteSpace(direct)
                    && ((LastFailureReason ?? string.Empty).StartsWith("provider-auth-restored:", StringComparison.Ordinal)
                        || (LastFailureReason ?? string.Empty).StartsWith("provider-ui-error-failed-to-fetch:", StringComparison.Ordinal)))
                {
                    // Resubmit the actual prompt once after restored authentication
                    // or a provider transport failure, rather than polling an error page.
                    LastFailureReason = string.Empty;
                    await StartFreshProviderChatAsync(primaryProvider).ConfigureAwait(false);
                    baselineResponse = (await EvaluateJsAsync(BuildReadScript(primaryProvider), 3500) ?? string.Empty).Trim();
                    injectResult = await EvaluateJsWithRetryAsync(BuildInjectScript(primaryProvider, escapedPrompt), 12000, 2, 400);
                    injectResult = await ResolvePendingSendStatusAsync(primaryProvider, injectResult);
                    if (!IsError(injectResult) && !IsNoSendStatus(injectResult) && !string.IsNullOrWhiteSpace(injectResult))
                        direct = await PollForModelResponseAsync(primaryProvider, baselineResponse, strictProvider);
                    else
                        LastFailureReason = "provider-prompt-submit-failed:" + ProviderToServiceName(primaryProvider) + ":after-reauthentication";
                }
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    _lastAiResponse = direct;
                    Log("Model response captured.");
                    return direct;
                }

                if ((LastFailureReason ?? string.Empty).StartsWith("provider-input-too-long:", StringComparison.Ordinal)
                    || (LastFailureReason ?? string.Empty).StartsWith("provider-auth-", StringComparison.Ordinal))
                    return string.Empty;

                if (primaryProvider == AiProvider.Gemini || primaryProvider == AiProvider.Claude)
                {
                    var stuck = await IsProviderLikelyStuckAsync(primaryProvider);
                    if (stuck)
                    {
                        Log("Detected stuck provider state; attempting refresh-and-resend recovery...");
                        var refreshed = await AttemptRefreshAndResendRecoveryAsync(primaryProvider, escapedPrompt, strictProvider);
                        if (!string.IsNullOrWhiteSpace(refreshed))
                        {
                            _lastAiResponse = refreshed;
                            Log("Model response captured after refresh-and-resend recovery.");
                            return refreshed;
                        }
                    }

                    Log("Provider response stalled; attempting recovery (new chat / refresh / fresh tab)...");
                    var recovered = await AttemptProviderRecoveryAsync(primaryProvider, escapedPrompt, strictProvider);
                    if (!string.IsNullOrWhiteSpace(recovered))
                    {
                        _lastAiResponse = recovered;
                        Log("Model response captured after provider recovery.");
                        return recovered;
                    }
                }

                if (string.IsNullOrWhiteSpace(LastFailureReason))
                    LastFailureReason = "provider-response-timeout:" + ProviderToServiceName(primaryProvider);
                Log("Response polling timed out.");
                return string.Empty;
            }
            finally
            {
                _queryLock.Release();
            }
        }

        private List<AiProvider> BuildProviderFallbackOrder(AiProvider primaryProvider, bool strictProvider)
        {
            if (strictProvider)
            {
                return new List<AiProvider> { primaryProvider == AiProvider.Unknown ? AiProvider.ChatGpt : primaryProvider };
            }

            var start = primaryProvider == AiProvider.Unknown ? AiProvider.ChatGpt : primaryProvider;
            var startIndex = Array.IndexOf(ProviderRoundRobin, start);
            if (startIndex < 0) startIndex = 0;

            var ordered = new List<AiProvider>(ProviderRoundRobin.Length);
            for (var i = 0; i < ProviderRoundRobin.Length; i++)
            {
                ordered.Add(ProviderRoundRobin[(startIndex + i) % ProviderRoundRobin.Length]);
            }

            return ordered;
        }

        private AiProvider GetNextRoundRobinPrimary()
        {
            if (_roundRobinCursor < 0 || _roundRobinCursor >= ProviderRoundRobin.Length)
            {
                _roundRobinCursor = 0;
            }

            var provider = ProviderRoundRobin[_roundRobinCursor];
            _roundRobinCursor = (_roundRobinCursor + 1) % ProviderRoundRobin.Length;
            return provider;
        }

        private void AdvanceRoundRobinCursor(AiProvider provider)
        {
            var index = Array.IndexOf(ProviderRoundRobin, provider);
            if (index < 0)
            {
                return;
            }

            _roundRobinCursor = (index + 1) % ProviderRoundRobin.Length;
        }

        private async Task<string> ReadCandidateResponseAsync(AiProvider primaryProvider, string baselineResponse, bool allowBaselineMatch, bool strictProvider)
        {
            foreach (var provider in BuildProviderFallbackOrder(primaryProvider, strictProvider))
            {
                var candidate = (await EvaluateJsAsync(BuildReadScript(provider), 2500) ?? string.Empty).Trim();
                if (!IsAcceptableResponseCandidate(candidate, baselineResponse, allowBaselineMatch))
                {
                    continue;
                }

                if (provider != _provider)
                {
                    _provider = provider;
                }

                return candidate;
            }

            if (strictProvider || primaryProvider == AiProvider.Gemini)
            {
                return string.Empty;
            }

            var generic = (await EvaluateJsAsync(BuildGenericReadScript(), 2500) ?? string.Empty).Trim();
            if (IsAcceptableResponseCandidate(generic, baselineResponse, allowBaselineMatch))
            {
                return generic;
            }

            return string.Empty;
        }

        private async Task<bool> IsProviderLikelyStuckAsync(AiProvider provider)
        {
            if (provider != AiProvider.Claude && provider != AiProvider.Gemini && provider != AiProvider.ChatGpt)
            {
                return false;
            }

            var script = BuildStuckDetectionScript(provider);
            var result = (await EvaluateJsAsync(script, 3500) ?? string.Empty).Trim().ToLowerInvariant();
            return string.Equals(result, "stuck", StringComparison.OrdinalIgnoreCase);
        }

        private string BuildStuckDetectionScript(AiProvider provider)
        {
            if (provider == AiProvider.ChatGpt)
            {
                return @"(function(){
                    var nodes=document.querySelectorAll('[data-chatgpt-copy-reference][data-dil-source-message-id],[data-message-author-role=""assistant""]');
                    if(!nodes.length)return 'ok';
                    var text=(nodes[nodes.length-1].innerText||'').trim();
                    var partial=text.indexOf('CDTS_EVENT_JSON_START')>=0 && text.indexOf('CDTS_EVENT_JSON_END')<0;
                    var complete=document.querySelector('button[aria-label=""Regenerate response""]');
                    var busy=document.querySelector('button[aria-label*=""Stop""],button[data-testid=""stop-button""]');
                    return partial && complete && !busy ? 'stuck' : 'ok';
                })();";
            }
            if (provider == AiProvider.Claude)
            {
                return @"(function(){
                    try{
                        var getComposer=function(){
                            return document.querySelector('div[contenteditable=""true""][data-testid*=""chat-input""]')
                                || document.querySelector('div[contenteditable=""true""][aria-label*=""Talk to Claude""]')
                                || document.querySelector('div[contenteditable=""true""][aria-label*=""Message Claude""]')
                                || document.querySelector('div[contenteditable=""true""][role=""textbox""]')
                                || document.querySelector('textarea');
                        };
                        var composer=getComposer();
                        var composerText='';
                        if(composer){composerText=((composer.value||composer.innerText||composer.textContent||'')+'').trim();}

                        var spinner=document.querySelector('[class*=""spin""]')||document.querySelector('[class*=""loading""]')||document.querySelector('svg[class*=""animate-spin""]');
                        var stopBtn=(function(){
                            var nodes=document.querySelectorAll('button,[role=""button""]');
                            for(var i=0;i<nodes.length;i++){
                                var t=((nodes[i].innerText||nodes[i].textContent||'')+'').toLowerCase();
                                var a=((nodes[i].getAttribute('aria-label')||'')+'').toLowerCase();
                                if(t.indexOf('stop')>=0||t.indexOf('cancel')>=0||a.indexOf('stop')>=0||a.indexOf('cancel')>=0) return true;
                            }
                            return false;
                        })();

                        var assistant=document.querySelector('[data-testid*=""assistant""],[data-message-author-role=""assistant""],[data-markdown-text-style=""assistant-message""]');
                        var busy=!!spinner || !!stopBtn;
                        var hasDraft=composerText.length>0;
                        var hasAssistant=!!assistant;
                        if(busy && hasDraft && !hasAssistant){ return 'stuck'; }
                        if(busy && hasDraft){ return 'stuck'; }
                        return 'ok';
                    }catch(e){return 'ok';}
                })();";
            }

            return @"(function(){
                try{
                    var composer=document.querySelector('div[contenteditable=""true""][aria-label*=""prompt""]')||document.querySelector('div[contenteditable=""true""]')||document.querySelector('textarea');
                    var composerText='';
                    if(composer){composerText=((composer.value||composer.innerText||composer.textContent||'')+'').trim();}
                    var visible=function(node){var r=node.getBoundingClientRect();return r.width>0&&r.height>0&&getComputedStyle(node).visibility!=='hidden';};
                    var spinners=document.querySelectorAll('mat-progress-spinner,md-progress-circular,[role=""progressbar""],.loading-content-spinner-container');
                    var busy=Array.prototype.some.call(spinners,visible);
                    if(busy && composerText.length>0){ return 'stuck'; }
                    return 'ok';
                }catch(e){return 'ok';}
            })();";
        }

        private async Task<string> AttemptRefreshAndResendRecoveryAsync(AiProvider provider, string escapedPrompt, bool strictProvider)
        {
            var service = ProviderToServiceName(provider);
            try
            {
                await EvaluateJsAsync("(function(){try{location.reload();return 'ok';}catch(e){return 'error:'+e.message;}})();", 3000);
            }
            catch
            {
            }

            await Task.Delay(provider == AiProvider.Gemini ? 2600 : 2200);
            await ConnectInternalAsync(provider).ConfigureAwait(false);
            await Task.Delay(provider == AiProvider.Gemini ? 1200 : 1000);

            var baseline = (await EvaluateJsAsync(BuildReadScript(provider), 3500) ?? string.Empty).Trim();
            var inject = await EvaluateJsWithRetryAsync(BuildInjectScript(provider, escapedPrompt), 12000, 2, 400);
            inject = await ResolvePendingSendStatusAsync(provider, inject);

            if (IsNoSendStatus(inject) || IsError(inject) || string.IsNullOrWhiteSpace(inject))
            {
                return string.Empty;
            }

            return await PollForModelResponseAsync(provider, baseline, strictProvider);
        }

        private async Task StartFreshProviderChatAsync(AiProvider provider)
        {
            try
            {
                if (provider == AiProvider.ChatGpt)
                {
                    // Prefer resetting the current managed ChatGPT tab instead of creating a new tab
                    // each request, which can accumulate chrome.exe memory/process usage over time.
                    await ConnectInternalAsync(provider).ConfigureAwait(false);
                }

                var result = await EvaluateJsAsync(BuildStartFreshChatScript(provider), 5000).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    Log("Fresh chat request for " + ProviderToServiceName(provider) + ": " + result);
                }

                await Task.Delay(provider == AiProvider.ChatGpt ? 6500 : 1400).ConfigureAwait(false);
                await WaitForProviderPageReadyAsync(provider, provider == AiProvider.ChatGpt ? 18000 : 7000).ConfigureAwait(false);
                await KeepProviderPageActiveAsync().ConfigureAwait(false);
                await PruneExtraAiTabsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("Fresh chat request failed for " + ProviderToServiceName(provider) + ": " + ex.Message);
            }
        }

        private bool IsAcceptableResponseCandidate(string response, string baselineResponse, bool allowBaselineMatch)
        {
            if (string.IsNullOrWhiteSpace(response)) return false;
            if (IsError(response))
            {
                Log("Read script returned error: " + response);
                return false;
            }
            if (LooksLikeThinkingState(response)) return false;
            if (LooksLikeIncompleteStructuredMarkerResponse(response)) return false;
            if (!IsLikelyAssistantContent(response)) return false;
            if (!allowBaselineMatch
                && !string.IsNullOrWhiteSpace(baselineResponse)
                && string.Equals(response, baselineResponse, StringComparison.Ordinal)) return false;

            return true;
        }

        private bool LooksLikeIncompleteStructuredMarkerResponse(string response)
        {
            var value = response ?? string.Empty;
            if (value.IndexOf("CDTS_BATCH_START", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("CDTS_BATCH_END", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return true;
            }

            if (value.IndexOf("CDTS_EVENT_JSON_START", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("CDTS_EVENT_JSON_END", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return true;
            }

            if (value.IndexOf("CDTS_JSON_START", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("CDTS_JSON_END", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return true;
            }

            return false;
        }

        private async Task<string> EvaluateJsWithRetryAsync(string script, int timeoutMs, int attempts, int retryDelayMs)
        {
            var tries = Math.Max(1, attempts);
            for (var attempt = 0; attempt < tries; attempt++)
            {
                var result = await EvaluateJsAsync(script, timeoutMs);
                if (!string.IsNullOrWhiteSpace(result) && !IsError(result))
                {
                    return result;
                }

                if (attempt + 1 < tries)
                {
                    await Task.Delay(Math.Max(100, retryDelayMs));
                }
            }

            return string.Empty;
        }

        private async Task<bool> IsProviderAuthenticationRequiredAsync(AiProvider provider)
        {
            string script;
            if (provider == AiProvider.ChatGpt)
            {
                script = @"(function(){try{if(location.hostname==='auth.openai.com'||location.hostname==='accounts.google.com')return true;var nodes=document.querySelectorAll('button,a,[role=""button""],[data-testid=""modal-no-auth-login""]');for(var i=0;i<nodes.length;i++){var n=nodes[i];if(n.closest('[data-message-author-role]'))continue;var r=n.getBoundingClientRect();if(!r.width||!r.height)continue;var label=(n.innerText||n.textContent||'').trim().toLowerCase();if(label==='log in'||label==='sign up'||label==='sign up for free'||n.getAttribute('data-testid')==='modal-no-auth-login')return true;}return false;}catch(e){return false;}})();";
            }
            else if (provider == AiProvider.Gemini)
            {
                script = @"(function(){try{if(location.hostname==='accounts.google.com')return true;var nodes=document.querySelectorAll('a,button,[role=""button""]');for(var i=0;i<nodes.length;i++){var n=nodes[i];if(n.closest('user-query,model-response'))continue;var r=n.getBoundingClientRect();if(!r.width||!r.height)continue;var text=(n.innerText||n.textContent||'').trim().toLowerCase();if(text==='sign in'||text==='log in'||(n.getAttribute('href')||'').indexOf('accounts.google.com/ServiceLogin')>=0)return true;}return false;}catch(e){return false;}})();";
            }
            else if (provider == AiProvider.Claude)
            {
                script = @"(function(){try{var u=(location.href||'').toLowerCase();return u.indexOf('/login')>=0||u.indexOf('/oauth')>=0||!!document.querySelector('a[href*=""/login""],button[data-testid*=""login""]');}catch(e){return false;}})();";
            }
            else
            {
                return false;
            }

            var value = await EvaluateJsAsync(script, 3500).ConfigureAwait(false);
            return string.Equals((value ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> TryReauthenticateProviderAsync(AiProvider provider)
        {
            if (provider != AiProvider.ChatGpt && provider != AiProvider.Gemini) return false;
            var email = Environment.GetEnvironmentVariable("CDTS_BROWSER_LOGIN_EMAIL");
            if (string.IsNullOrWhiteSpace(email)) email = "destroythedrones@gmail.com";
            email = email.Trim();
            var originalTab = _targetTabId;
            var existing = new HashSet<string>((await GetTabsAsync().ConfigureAwait(false)).Select(t => (string)GetTabId(t)));
            var script = BuildReauthenticationScript(provider, email);
            var deadline = DateTime.UtcNow.AddSeconds(90);
            Log("[Auth] Restoring " + ProviderToServiceName(provider) + " using Google account " + email + ".");
            string previous = null;
            while (DateTime.UtcNow < deadline)
            {
                var tabs = await GetTabsAsync().ConfigureAwait(false);
                foreach (var tab in tabs)
                {
                    if (!string.Equals(GetTabType(tab), "page", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = GetTabId(tab);
                    Uri uri;
                    if (!Uri.TryCreate(GetTabUrl(tab), UriKind.Absolute, out uri)) continue;
                    var authHost = uri.Host == "accounts.google.com" || uri.Host == "auth.openai.com";
                    if (id != originalTab && (existing.Contains(id) || !authHost)) continue;
                    var step = await EvaluateAuthenticationTabAsync(GetWebSocketDebuggerUrl(tab), script).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(step)) continue; // Navigation can replace the execution context.
                    if (step != previous) { Log("[Auth] " + step); previous = step; }
                    if (step == "authenticated")
                    {
                        await ConnectInternalAsync(provider).ConfigureAwait(false);
                        await WaitForProviderPageReadyAsync(provider, 18000).ConfigureAwait(false);
                        if (await IsProviderAuthenticationRequiredAsync(provider).ConfigureAwait(false))
                        {
                            await EvaluateJsAsync("window.__cdts_navigation_pending=true;location.reload();'refresh-authenticated-session';", 3000).ConfigureAwait(false);
                            await WaitForProviderPageReadyAsync(provider, 18000).ConfigureAwait(false);
                        }
                        if (!await IsProviderAuthenticationRequiredAsync(provider).ConfigureAwait(false))
                        {
                            LastFailureReason = string.Empty;
                            Log("[Auth] Verified restored session for " + email + ".");
                            return true;
                        }
                    }
                    if (step.StartsWith("challenge:", StringComparison.Ordinal) || step.StartsWith("wrong-account:", StringComparison.Ordinal))
                    {
                        LastFailureReason = "provider-auth-" + step + ":" + ProviderToServiceName(provider);
                        return false;
                    }
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }
            LastFailureReason = "provider-auth-recovery-timeout:" + ProviderToServiceName(provider);
            return false;
        }

        private string BuildReauthenticationScript(AiProvider provider, string email)
        {
            return "(async function(){var email=" + BuildJsStringLiteral(email.ToLowerInvariant()) + ";var host=location.hostname;"
                + @"var visible=function(n){var r=n.getBoundingClientRect();return !!(r.width&&r.height);};
                    var label=function(n){return (n.innerText||n.textContent||n.getAttribute('aria-label')||'').trim().toLowerCase();};
                    var controls=Array.from(document.querySelectorAll('a,button,[role=""button""]')).filter(visible);
                    if(host==='chatgpt.com'){
                        try{var r=await fetch('/api/auth/session');var s=await r.json();if(s.user&&s.user.email){
                            return s.user.email.toLowerCase()===email?'authenticated':'wrong-account:'+s.user.email;
                        }}catch(e){}
                    }
                    if(host==='gemini.google.com'){
                        var profile=document.querySelector('meta[name=""og-profile-acct""]');
                        if(profile&&profile.content&&!controls.some(n=>/^(sign in|log in)$/.test(label(n))))
                            return profile.content.toLowerCase()===email?'authenticated':'wrong-account:'+profile.content;
                    }
                    var authHost=host==='accounts.google.com'||host==='auth.openai.com';
                    if(authHost){
                        if(Array.from(document.querySelectorAll('input[type=""password""]')).some(visible))return 'challenge:password';
                        if(Array.from(document.querySelectorAll('input[autocomplete=""one-time-code""]')).some(visible)
                            || /verify it.s you|2.step verification|check your phone|enter (the |a )?verification code/i.test(document.body.innerText))return 'challenge:verification';
                    }
                    if(Date.now()-(window.__cdts_auth_action_at||0)<4000)return 'waiting-navigation';
                    var click=function(n,state){window.__cdts_auth_action_at=Date.now();n.click();return state;};
                    if(host==='accounts.google.com'){
                        var account=Array.from(document.querySelectorAll('[data-identifier],[data-email]')).find(n=>visible(n)
                            &&(n.getAttribute('data-identifier')||n.getAttribute('data-email')||'').toLowerCase()===email);
                        if(account)return click(account,'selected-google-account');
                        var input=Array.from(document.querySelectorAll('input[type=""email""]')).find(visible);
                        if(input){var setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;setter.call(input,email);
                            input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));
                            var next=controls.find(n=>label(n)==='next');if(next)return click(next,'submitted-google-email');}
                        var consent=controls.find(n=>label(n)==='continue');if(consent)return click(consent,'continued-google-sign-in');
                    }
                    var google=controls.find(n=>/^(continue with google|sign in with google)$/.test(label(n)));
                    if(google)return click(google,'opened-google-sign-in');
                    var login=controls.find(n=>/^(log in|sign in)$/.test(label(n)));
                    if(login)return click(login,'opened-provider-login');
                    return 'waiting-login-page';
                })();";
        }

        private async Task<string> EvaluateAuthenticationTabAsync(string websocketUrl, string script)
        {
            if (string.IsNullOrWhiteSpace(websocketUrl)) return string.Empty;
            try
            {
                using (var socket = new ClientWebSocket())
                using (var timeout = new CancellationTokenSource(6500))
                {
                    await socket.ConnectAsync(new Uri(websocketUrl), timeout.Token).ConfigureAwait(false);
                    var command = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { id = 1, method = "Runtime.evaluate",
                        @params = new { expression = script, returnByValue = true, awaitPromise = true } }));
                    await socket.SendAsync(new ArraySegment<byte>(command), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                    var buffer = new byte[8192];
                    while (socket.State == WebSocketState.Open)
                    {
                        using (var message = new MemoryStream())
                        {
                            WebSocketReceiveResult received;
                            do
                            {
                                received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                                if (received.MessageType == WebSocketMessageType.Close) return string.Empty;
                                message.Write(buffer, 0, received.Count);
                            } while (!received.EndOfMessage);
                            var reply = UtilCompat.JsonDeserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(message.ToArray()));
                            if (reply == null || !reply.ContainsKey("id") || Convert.ToInt32(reply["id"]) != 1) continue;
                            object result;
                            if (!reply.TryGetValue("result", out result)) return string.Empty;
                            var outer = result as Dictionary<string, object>;
                            if (outer == null || !outer.TryGetValue("result", out result)) return string.Empty;
                            var inner = result as Dictionary<string, object>;
                            return inner != null && inner.TryGetValue("value", out result) ? Convert.ToString(result) : string.Empty;
                        }
                    }
                }
            }
            catch { /* Navigation closes popup targets and invalidates execution contexts. */ }
            return string.Empty;
        }

        private async Task<string> DetectProviderResponseFailureAsync(AiProvider provider)
        {
            // Inspect provider notifications, excluding user and assistant content.
            var script = @"(function(){var nodes=document.querySelectorAll('[role=""alert""],[data-testid*=""error""],.error-message,.error-container,.response-error,mat-snack-bar-container');
                for(var i=0;i<nodes.length;i++){
                    if(nodes[i].closest('[data-message-author-role],user-query,[data-markdown-text-style]'))continue;
                    var rect=nodes[i].getBoundingClientRect();if(!rect.width||!rect.height)continue;
                    var text=(nodes[i].innerText||nodes[i].textContent||'').toLowerCase();
                    if(text.indexOf('messages you submitted were too long')>=0 || text.indexOf('message is too long')>=0
                        || text.indexOf('conversation is too long')>=0)return 'input-too-long';
                    var failed=text.match(/something went wrong\s*\((\d+)\)/);
                    if(failed)return 'ui-error-'+failed[1];
                }
                var retries=document.querySelectorAll('button');
                for(var j=0;j<retries.length;j++){
                    var button=retries[j];if((button.innerText||'').trim()!=='Retry')continue;
                    var rect=button.getBoundingClientRect();if(!rect.width||!rect.height)continue;
                    var container=button.parentElement;
                    for(var depth=0;container&&depth<4;depth++,container=container.parentElement){
                        var label=(container.innerText||'').trim().toLowerCase();
                        if(label.length<300&&/(^|\n)failed to fetch(\n|$)/.test(label))return 'ui-error-failed-to-fetch';
                    }
                }return '';})();";
            var error = (await EvaluateJsAsync(script, 2000).ConfigureAwait(false) ?? string.Empty).Trim();
            return error == "input-too-long"
                ? "provider-input-too-long:" + ProviderToServiceName(provider)
                : error.StartsWith("ui-error-", StringComparison.Ordinal)
                    ? "provider-" + error + ":" + ProviderToServiceName(provider) : string.Empty;
        }

        private async Task<string> PollForModelResponseAsync(AiProvider provider, string baselineResponse, bool strictProvider)
        {
            if (_tabNeedsRecovery) return string.Empty;
            string bestCandidate = string.Empty;
            int stableCount = 0;

            var maxAttempts = provider == AiProvider.Claude ? 10
                : provider == AiProvider.Gemini ? 15 : 20;
            var pollDelayMs = provider == AiProvider.Gemini || provider == AiProvider.Claude ? 1000 : 750;

            for (var i = 0; i < maxAttempts; i++)
            {
                await Task.Delay(pollDelayMs);
                if (_tabNeedsRecovery || !IsConnected)
                {
                    _tabNeedsRecovery = true;
                    LastFailureReason = "provider-tab-unresponsive:" + ProviderToServiceName(provider);
                    return string.Empty;
                }
                Log("Polling model response attempt " + (i + 1));
                var fastResponse = await ReadCandidateResponseAsync(provider, baselineResponse, false, strictProvider);
                if (!string.IsNullOrWhiteSpace(fastResponse))
                {
                    await Task.Delay(250);
                    var fastConfirm = await ReadCandidateResponseAsync(provider, baselineResponse, false, strictProvider);
                    if (!string.IsNullOrWhiteSpace(fastConfirm)
                        && string.Equals(fastConfirm, fastResponse, StringComparison.Ordinal))
                    {
                        return fastConfirm;
                    }
                }

                var quota = await DetectPageRateLimitAsync(provider).ConfigureAwait(false);
                if (quota.Item1)
                    return "CDTS_RATE_LIMITED:" + ProviderToServiceName(provider) + ":" + quota.Item2;
                if (await IsProviderAuthenticationRequiredAsync(provider).ConfigureAwait(false))
                {
                    var restored = await TryReauthenticateProviderAsync(provider).ConfigureAwait(false);
                    if (restored)
                        LastFailureReason = "provider-auth-restored:" + ProviderToServiceName(provider);
                    else if (string.IsNullOrWhiteSpace(LastFailureReason))
                        LastFailureReason = "provider-auth-required:" + ProviderToServiceName(provider);
                    Log(restored ? "[Auth] Session restored; the interrupted prompt will be resubmitted."
                        : "[Query] FAILED: " + LastFailureReason);
                    return string.Empty;
                }
                var responseFailure = await DetectProviderResponseFailureAsync(provider).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(responseFailure))
                {
                    LastFailureReason = responseFailure;
                    Log("[Query] FAILED: " + responseFailure);
                    // Gemini's 1095 rejection was observed with RESOURCE_EXHAUSTED
                    // in StreamGenerate even though the transport returned HTTP 200.
                    // Return the existing pacing marker before chat/tab recovery.
                    if (provider == AiProvider.Gemini
                        && responseFailure == "provider-ui-error-1095:Gemini")
                        return "CDTS_RATE_LIMITED:" + responseFailure;
                    return string.Empty;
                }

                var response = await ReadCandidateResponseAsync(provider, baselineResponse, false, strictProvider);
                if (string.IsNullOrWhiteSpace(response)) continue;

                // Bail out early if the response looks like a rate-limit / quota page
                // rather than an AI model response — saves polling time.
                if (LooksLikeRateLimitResponse(response))
                {
                    Log("[RateLimit] Polling detected rate-limit response for " + ProviderToServiceName(provider)
                        + " after " + (i + 1) + " polls; bailing.");
                    return "CDTS_RATE_LIMITED_RESPONSE:" + ProviderToServiceName(provider);
                }

                if (string.Equals(response, bestCandidate, StringComparison.Ordinal))
                {
                    stableCount++;
                }
                else
                {
                    bestCandidate = response;
                    stableCount = 1;
                }

                if (stableCount >= 2)
                {
                    await Task.Delay(500);
                    var confirm = await ReadCandidateResponseAsync(provider, baselineResponse, false, strictProvider);
                    if (!string.IsNullOrWhiteSpace(confirm)
                        && string.Equals(confirm, response, StringComparison.Ordinal))
                    {
                        return confirm;
                    }
                }
            }

            if (!strictProvider && provider != AiProvider.Claude && provider != AiProvider.Gemini)
            {
                var generic = (await EvaluateJsAsync(BuildGenericReadScript(), 3000) ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(generic)
                    && IsLikelyAssistantContent(generic)
                    && !LooksLikeThinkingState(generic))
                {
                    Log("Model response captured via generic fallback selector.");
                    return generic;
                }
            }

            var finalCandidate = (await EvaluateJsAsync(BuildReadScript(provider), 3000) ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(finalCandidate)
                && !string.Equals(finalCandidate, baselineResponse, StringComparison.Ordinal)
                && !LooksLikeThinkingState(finalCandidate)
                && !LooksLikeIncompleteStructuredMarkerResponse(finalCandidate)
                && IsLikelyAssistantContent(finalCandidate))
            {
                Log("Model response captured by final provider DOM read.");
                return finalCandidate;
            }

            return string.Empty;
        }

        private async Task<string> AttemptProviderRecoveryAsync(AiProvider provider, string escapedPrompt, bool strictProvider)
        {
            if (provider != AiProvider.Gemini && provider != AiProvider.Claude)
            {
                return string.Empty;
            }

            var service = ProviderToServiceName(provider);

            try
            {
                await EvaluateJsAsync(BuildStartFreshChatScript(provider), 5000);
            }
            catch
            {
            }

            await Task.Delay(provider == AiProvider.Gemini ? 1200 : 900);

            var baseline = (await EvaluateJsAsync(BuildReadScript(provider), 3500) ?? string.Empty).Trim();
            var inject = await EvaluateJsWithRetryAsync(BuildInjectScript(provider, escapedPrompt), 12000, 2, 400);
            inject = await ResolvePendingSendStatusAsync(provider, inject);
            if (!IsError(inject) && !IsNoSendStatus(inject) && !string.IsNullOrWhiteSpace(inject))
            {
                var recovered = await PollForModelResponseAsync(provider, baseline, strictProvider);
                if (!string.IsNullOrWhiteSpace(recovered))
                {
                    return recovered;
                }
            }

            Log("Soft recovery did not produce a response; opening fresh provider tab...");

            try
            {
                var startUrl = GetProviderStartUrl(provider);
                if (!string.IsNullOrWhiteSpace(startUrl))
                {
                    await CreateTabAsync(startUrl);
                }
            }
            catch
            {
            }

            await Task.Delay(provider == AiProvider.Gemini ? 1800 : 1300);
            await ConnectInternalAsync(provider).ConfigureAwait(false);
            await Task.Delay(provider == AiProvider.Gemini ? 1200 : 900);
            await PruneExtraAiTabsAsync().ConfigureAwait(false);

            baseline = (await EvaluateJsAsync(BuildReadScript(provider), 3500) ?? string.Empty).Trim();
            inject = await EvaluateJsWithRetryAsync(BuildInjectScript(provider, escapedPrompt), 12000, 2, 400);
            inject = await ResolvePendingSendStatusAsync(provider, inject);
            if (IsNoSendStatus(inject))
            {
                Log("Fresh-tab recovery prompt was injected but not submitted (no-send).");
                return string.Empty;
            }

            if (IsError(inject) || string.IsNullOrWhiteSpace(inject))
            {
                Log("Fresh-tab recovery prompt injection failed: " + inject);
                return string.Empty;
            }

            return await PollForModelResponseAsync(provider, baseline, strictProvider);
        }

        private async Task<bool> CloseTabAsync(string tabId, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(tabId))
            {
                return false;
            }

            try
            {
                using (var resp = await _http.GetAsync(DebugBase + "/json/close/" + Uri.EscapeDataString(tabId), token).ConfigureAwait(false))
                {
                    return resp.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task PruneExtraAiTabsAsync(CancellationToken token = default(CancellationToken))
        {
            try
            {
                var tabs = await GetTabsAsync(token).ConfigureAwait(false);
                var aiTabs = tabs.Where(IsAiTab).ToList();
                if (aiTabs.Count <= 4)
                {
                    return;
                }

                var keepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(_targetTabId))
                {
                    keepIds.Add(_targetTabId);
                }

                var active = aiTabs.FirstOrDefault(IsActiveTab);
                if (active != null)
                {
                    var activeId = GetTabId(active);
                    if (!string.IsNullOrWhiteSpace(activeId))
                    {
                        keepIds.Add(activeId);
                    }
                }

                foreach (var provider in ProviderRoundRobin)
                {
                    var providerTab = aiTabs.FirstOrDefault(t => DetectProvider(GetTabTitle(t), GetTabUrl(t)) == provider);
                    var providerId = GetTabId(providerTab);
                    if (!string.IsNullOrWhiteSpace(providerId))
                    {
                        keepIds.Add(providerId);
                    }
                }

                foreach (var tab in aiTabs)
                {
                    var tabId = GetTabId(tab);
                    if (string.IsNullOrWhiteSpace(tabId) || keepIds.Contains(tabId))
                    {
                        continue;
                    }

                    if (await CloseTabAsync(tabId, token).ConfigureAwait(false))
                    {
                        Log("Closed extra AI tab: " + tabId);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("AI tab pruning failed: " + ex.Message);
            }
        }

        private async Task<bool> SendAsync(string msg, int timeoutMs)
        {
            await _sendLock.WaitAsync();
            var bytes = Encoding.UTF8.GetBytes(msg);
            try
            {
                using (var sendCts = new CancellationTokenSource(Math.Max(500, timeoutMs)))
                {
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sendCts.Token);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                Log("CDP send timed out.");
                if (!IsConnected)
                {
                    MarkDisconnected("CDP send timeout after socket close.");
                }

                return false;
            }
            catch
            {
                if (!IsConnected)
                {
                    MarkDisconnected("CDP send failed due to closed socket.");
                }

                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void StartReceivePump()
        {
            StopReceivePump();
            _receivePumpCts = new CancellationTokenSource();
            var token = _receivePumpCts.Token;
            var socket = _ws;
            _receivePumpTask = Task.Run(() => ReceivePumpAsync(socket, token));
        }

        private void StopReceivePump()
        {
            try
            {
                if (_receivePumpCts != null)
                {
                    _receivePumpCts.Cancel();
                }
            }
            catch
            {
            }

            try
            {
                if (_receivePumpTask != null)
                {
                    _receivePumpTask.Wait(300);
                }
            }
            catch
            {
            }

            try { _receivePumpCts?.Dispose(); } catch { }
            _receivePumpCts = null;
            _receivePumpTask = null;

            CancelPendingResponses();
        }

        private void CancelPendingResponses()
        {
            List<TaskCompletionSource<string>> pending;
            lock (_pendingLock)
            {
                pending = _pendingResponses.Values.ToList();
                _pendingResponses.Clear();
            }

            foreach (var p in pending)
            {
                try { p.TrySetResult(string.Empty); } catch { }
            }
        }

        private async Task ReceivePumpAsync(ClientWebSocket socket, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var responseJson = await ReceiveAsync(socket, token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(responseJson)) break;

                    if (responseJson.Contains("\"Inspector.targetCrashed\"") || responseJson.Contains("\"Target.targetCrashed\""))
                    {
                        _tabNeedsRecovery = true;
                        LastFailureReason = "provider-tab-crashed:" + ProviderToServiceName(_provider);
                        Log("[TabRecovery] Provider renderer crashed: " + _targetTabId);
                        break;
                    }

                    int responseId;
                    if (!TryGetMessageId(responseJson, out responseId)) continue;

                    TaskCompletionSource<string> pending = null;
                    lock (_pendingLock)
                    {
                        if (!ReferenceEquals(socket, _ws) || token.IsCancellationRequested) return;
                        if (_pendingResponses.TryGetValue(responseId, out pending))
                            _pendingResponses.Remove(responseId);
                    }
                    if (pending != null) pending.TrySetResult(responseJson);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Stopping this connection must not change a replacement connection.
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && ReferenceEquals(socket, _ws))
                    Log("CDP receive failed: " + ex.Message);
            }
            finally
            {
                if (!token.IsCancellationRequested && ReferenceEquals(socket, _ws))
                {
                    MarkDisconnected("CDP receive connection closed.");
                    CancelPendingResponses();
                }
            }
        }

        private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[8192];
            // Decode after assembling the message: a UTF-8 character can span frames.
            using (var message = new MemoryStream())
            {
                while (true)
                {
                    // Request callers own their response deadlines. Cancelling an idle
                    // ReceiveAsync aborts ClientWebSocket, even on a healthy provider tab.
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return string.Empty;
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                        return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                }
            }
        }

        private bool TryGetMessageId(string json, out int id)
        {
            id = 0;
            try
            {
                var dict = UtilCompat.JsonDeserialize<Dictionary<string, object>>(json);
                if (dict == null || !dict.ContainsKey("id") || dict["id"] == null) return false;

                var val = dict["id"];
                if (val is int) { id = (int)val; return true; }
                if (val is long) { id = (int)(long)val; return true; }
                if (val is double) { id = (int)(double)val; return true; }

                int parsed;
                if (int.TryParse(val.ToString(), out parsed))
                {
                    id = parsed;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private bool IsAiTab(dynamic tab)
        {
            var type = GetTabType(tab);
            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var title = GetTabTitle(tab);
            var url = GetTabUrl(tab);
            return ContainsIgnoreCase(title, "ChatGPT")
                || ContainsIgnoreCase(title, "Gemini")
                || ContainsIgnoreCase(title, "Claude")
                || ContainsIgnoreCase(url, "chatgpt.com")
                || ContainsIgnoreCase(url, "gemini.google.com")
                || ContainsIgnoreCase(url, "claude.ai");
        }

        private bool IsSuitableProviderTab(dynamic tab, AiProvider provider)
        {
            var url = (GetTabUrl(tab) ?? string.Empty).ToLowerInvariant();
            if (provider == AiProvider.ChatGpt)
            {
                if (!url.Contains("chatgpt.com") && !url.Contains("chat.openai.com")) return false;
                if (url.Contains("/apps") || url.Contains("/gpts") || url.Contains("/library") || url.Contains("/store")) return false;
                return true;
            }

            if (provider == AiProvider.Gemini) return url.Contains("gemini.google.com");
            if (provider == AiProvider.Claude) return url.Contains("claude.ai");
            return IsAiTab(tab);
        }

        private async Task<List<dynamic>> GetTabsAsync(CancellationToken token = default(CancellationToken))
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, DebugUrl))
                using (var resp = await _http.SendAsync(req, token).ConfigureAwait(false))
                {
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return UtilCompat.JsonDeserialize<List<dynamic>>(json) ?? new List<dynamic>();
                }
            }
            catch
            {
                return new List<dynamic>();
            }
        }

        internal async Task EnsureBrowserHostAsync(string preferredService)
        {
            await BrowserQueryLock.WaitAsync().ConfigureAwait(false);
            try { await EnsureDebugHostReadyAsync(ParsePreferredProvider(preferredService)).ConfigureAwait(false); }
            finally { BrowserQueryLock.Release(); }
        }

        private async Task EnsureDebugHostReadyAsync(AiProvider preferredProvider, CancellationToken token = default(CancellationToken))
        {
            if (await IsDebugHostAvailableAsync(token))
            {
                EnsureManagedChromeHidden(true);
                return;
            }

            if (_chromeLaunchAttempted)
            {
                return;
            }

            _chromeLaunchAttempted = true;
            var startUrl = GetProviderStartUrl(preferredProvider == AiProvider.Unknown ? AiProvider.ChatGpt : preferredProvider);
            var launched = TryStartChromeDebugHost(startUrl, false);
            if (!launched)
            {
                Log("Chrome debug host unavailable and automatic Chrome launch failed.");
                return;
            }

            Log("Started Chrome in remote-debug mode; waiting for CDP host...");
            var begin = DateTime.UtcNow;
            while ((DateTime.UtcNow - begin).TotalMilliseconds < HostLaunchWaitMs)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(500, token).ConfigureAwait(false);
                if (await IsDebugHostAvailableAsync(token))
                {
                    Log("CDP host is now available.");
                    EnsureManagedChromeHidden(true);
                    return;
                }
            }

            Log("CDP host did not become available after automatic Chrome launch.");
        }

        private async Task<bool> IsDebugHostAvailableAsync(CancellationToken token = default(CancellationToken))
        {
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var request = new HttpRequestMessage(HttpMethod.Get, DebugBase + "/json/version"))
                {
                    cts.CancelAfter(HostProbeTimeoutMs);
                    using (var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false))
                    {
                        return response.IsSuccessStatusCode;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private bool TryStartChromeDebugHost(string startUrl, bool visibleWindow = false)
        {
            try
            {
                var chromePath = ResolveChromePath();
                if (string.IsNullOrWhiteSpace(chromePath))
                {
                    return false;
                }

                var userDataDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CryptoSidecar");

                var args = new StringBuilder();
                args.Append(DebugPortArgument).Append(" ");
                args.Append("--user-data-dir=\"").Append(userDataDir).Append("\"");
                args.Append(" --disable-session-crashed-bubble");
                args.Append(" --no-first-run");
                args.Append(" --no-default-browser-check");
                args.Append(" --disable-background-timer-throttling");
                args.Append(" --disable-backgrounding-occluded-windows");
                args.Append(" --disable-renderer-backgrounding");
                if (visibleWindow) args.Append(" --new-window");
                else args.Append(" --start-minimized");
                if (!string.IsNullOrWhiteSpace(startUrl))
                {
                    args.Append(" \"").Append(startUrl).Append("\"");
                }

                var psi = new ProcessStartInfo
                {
                    FileName = chromePath,
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    WindowStyle = visibleWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
                };

                var process = Process.Start(psi);
                _managedChromeProcessId = process != null ? process.Id : 0;
                if (!visibleWindow) EnsureManagedChromeHidden(true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private List<IntPtr> GetManagedChromeWindowHandles(bool wait = false)
        {
            try
            {
                int maxAttempts = wait ? ManagedWindowHideAttempts : 1;
                for (var i = 0; i < maxAttempts; i++)
                {
                    var handles = new List<IntPtr>();
                    var processes = Process.GetProcessesByName("chrome");
                    foreach (var process in processes)
                    {
                        using (process)
                        {
                            try
                            {
                                if (process.MainWindowHandle != IntPtr.Zero
                                    && IsManagedChromeCommandLine(TryGetProcessCommandLine(process.Id)))
                                {
                                    handles.Add(process.MainWindowHandle);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    if (handles.Count > 0 || !wait)
                    {
                        return handles;
                    }

                    if (wait && i < maxAttempts - 1)
                    {
                        Thread.Sleep(ManagedWindowStateRetryMs);
                    }
                }
            }
            catch
            {
            }

            return new List<IntPtr>();
        }

        private bool EnsureManagedChromeHidden(bool wait = true, bool force = false)
        {
            if (!force && IsManagedChromeVisibilityRequested()) return false;
            if (!_launchChromeHidden) return false;

            var found = false;
            var maxAttempts = wait ? ManagedWindowHideAttempts : 1;

            for (var i = 0; i < maxAttempts; i++)
            {
                var handles = GetManagedChromeWindowHandles(false);
                foreach (var handle in handles)
                {
                    found = true;
                    ShowWindow(handle, SwHide);
                }

                if (found && !GetManagedChromeWindowHandles(false).Any(handle => IsWindowVisible(handle)))
                {
                    return true;
                }

                if (i < maxAttempts - 1)
                {
                    Thread.Sleep(ManagedWindowStateRetryMs);
                }
            }

            return found;
        }

        private void StopManagedChrome(bool force = false)
        {
            if (!force && IsManagedChromeVisibilityRequested())
            {
                Log("Managed Chrome remains open because visible mode is explicitly requested.");
                return;
            }
            if (!force && !_chromeLaunchAttempted && _managedChromeProcessId <= 0)
            {
                return;
            }

            var processIds = new List<int>();
            if (_managedChromeProcessId > 0)
            {
                processIds.Add(_managedChromeProcessId);
            }

            try
            {
                foreach (var process in Process.GetProcessesByName("chrome"))
                {
                    using (process)
                    {
                        if (!processIds.Contains(process.Id) && IsManagedChromeCommandLine(TryGetProcessCommandLine(process.Id)))
                        {
                            processIds.Add(process.Id);
                        }
                    }
                }
            }
            catch
            {
            }

            foreach (var processId in processIds)
            {
                try
                {
                    using (var process = Process.GetProcessById(processId))
                    {
                        if (process.HasExited || !IsManagedChromeProcess(process))
                        {
                            continue;
                        }

                        try
                        {
                            if (process.MainWindowHandle != IntPtr.Zero)
                            {
                                process.CloseMainWindow();
                                if (process.WaitForExit(2000))
                                {
                                    continue;
                                }
                            }
                        }
                        catch
                        {
                        }

                        process.Kill();
                        process.WaitForExit(2000);
                    }
                }
                catch
                {
                }
            }

            _managedChromeProcessId = 0;
        }

        public async Task StartChromeAsync(string preferredService = null)
        {
            await EnsureBrowserHostAsync(preferredService).ConfigureAwait(false);
        }

        public void StopChrome()
        {
            StopManagedChrome(true);
        }

        public async Task<bool> StartNewChatAsync(string preferredService = null)
        {
            var provider = ParsePreferredProvider(preferredService);
            if (provider == AiProvider.Unknown)
                provider = AiProvider.ChatGpt;

            if (!await ConnectAsync(ProviderToServiceName(provider)).ConfigureAwait(false))
                return false;

            await StartFreshProviderChatAsync(provider).ConfigureAwait(false);
            return IsConnected;
        }

        public async Task<int> CloseAllTabsAsync()
        {
            if (!await IsDebugHostAvailableAsync().ConfigureAwait(false))
                return 0;

            var tabs = await GetTabsAsync().ConfigureAwait(false);
            var closed = 0;
            foreach (var tab in tabs)
            {
                var tabId = GetTabId(tab);
                if (!string.IsNullOrWhiteSpace(tabId)
                    && await CloseTabAsync(tabId).ConfigureAwait(false))
                    closed++;
            }

            return closed;
        }

        internal ManagedChromeOwnership DetachManagedChromeOwnership()
        {
            var ownership = new ManagedChromeOwnership
            {
                LaunchAttempted = _chromeLaunchAttempted,
                ProcessId = _managedChromeProcessId
            };
            _chromeLaunchAttempted = false;
            _managedChromeProcessId = 0;
            return ownership;
        }

        internal void AdoptManagedChromeOwnership(ManagedChromeOwnership ownership)
        {
            if (ownership == null) return;
            _chromeLaunchAttempted = ownership.LaunchAttempted;
            _managedChromeProcessId = ownership.ProcessId;
        }

        private bool IsManagedChromeCommandLine(string commandLine)
        {
            return ContainsIgnoreCase(commandLine, DebugPortArgument)
                && ContainsIgnoreCase(commandLine, SidecarUserDataDirectoryName);
        }

        private bool IsManagedChromeProcess(Process process)
        {
            if (process == null)
            {
                return false;
            }

            try
            {
                return ContainsIgnoreCase(process.ProcessName, "chrome")
                    && IsManagedChromeCommandLine(TryGetProcessCommandLine(process.Id));
            }
            catch
            {
                return false;
            }
        }

        private string TryGetProcessCommandLine(int processId)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + processId))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        return obj["CommandLine"] as string ?? string.Empty;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private string ResolveChromePath()
        {
            var candidates = new[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\Application\chrome.exe")
            };

            for (var i = 0; i < candidates.Length; i++)
            {
                var path = candidates[i];
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    return path;
                }
            }

            return string.Empty;
        }

        private async Task<bool> EnsureProviderTabAsync(AiProvider provider, CancellationToken token = default(CancellationToken))
        {
            if (provider == AiProvider.Unknown) return false;

            token.ThrowIfCancellationRequested();
            await _tabCreationLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var tabs = await GetTabsAsync(token).ConfigureAwait(false);
                if (tabs.Any(t => DetectProvider(GetTabTitle(t), GetTabUrl(t)) == provider
                    && IsSuitableProviderTab(t, provider)))
                    return true;

                var created = await CreateTabAsync(GetProviderStartUrl(provider), token).ConfigureAwait(false);
                if (!created) return false;

                Log("Opened background tab for " + ProviderToServiceName(provider) + ".");
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    tabs = await GetTabsAsync(token).ConfigureAwait(false);
                    if (tabs.Any(t => DetectProvider(GetTabTitle(t), GetTabUrl(t)) == provider
                        && IsSuitableProviderTab(t, provider)))
                        break;
                }

                return true;
            }
            finally
            {
                _tabCreationLock.Release();
            }
        }

        private async Task<List<dynamic>> EnsureAnyAiTabAsync(AiProvider preferredProvider, CancellationToken token = default(CancellationToken))
        {
            var providers = new List<AiProvider>();
            if (preferredProvider != AiProvider.Unknown)
            {
                providers.Add(preferredProvider);
            }
            else
            {
                providers.Add(AiProvider.ChatGpt);
                providers.Add(AiProvider.Gemini);
                providers.Add(AiProvider.Claude);
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                foreach (var provider in providers)
                {
                    await EnsureProviderTabAsync(provider, token).ConfigureAwait(false);
                }

                await Task.Delay(300 + (attempt * 250), token).ConfigureAwait(false);

                var tabs = await GetTabsAsync(token).ConfigureAwait(false);
                var aiTabs = tabs.Where(IsAiTab).ToList();
                if (aiTabs.Count > 0)
                {
                    return aiTabs;
                }

                if (attempt >= 1)
                {
                    foreach (var provider in providers)
                    {
                        await TryCreateProviderFallbackTabAsync(provider, token).ConfigureAwait(false);
                    }
                }
            }

            return new List<dynamic>();
        }

        private async Task TryCreateProviderFallbackTabAsync(AiProvider provider, CancellationToken token = default(CancellationToken))
        {
            await _tabCreationLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var existingTabs = await GetTabsAsync(token).ConfigureAwait(false);
                if (existingTabs.Any(t => DetectProvider(GetTabTitle(t), GetTabUrl(t)) == provider
                    && IsSuitableProviderTab(t, provider)))
                    return;

                foreach (var url in GetProviderFallbackUrls(provider))
                {
                    token.ThrowIfCancellationRequested();
                    var created = await CreateTabAsync(url, token).ConfigureAwait(false);
                    if (created)
                    {
                        Log("Opened fallback tab for " + ProviderToServiceName(provider) + " at " + url + ".");
                        await Task.Delay(750, token).ConfigureAwait(false);
                        return;
                    }
                }
            }
            finally
            {
                _tabCreationLock.Release();
            }
        }

        private List<string> GetProviderFallbackUrls(AiProvider provider)
        {
            if (provider == AiProvider.ChatGpt)
            {
                return new List<string>
                {
                    ChatGptStartUrl,
                    "https://chat.openai.com/"
                };
            }

            if (provider == AiProvider.Gemini)
            {
                return new List<string>
                {
                    GeminiStartUrl,
                    "https://gemini.google.com/app"
                };
            }

            if (provider == AiProvider.Claude)
            {
                return new List<string>
                {
                    ClaudeStartUrl,
                    "https://claude.ai/"
                };
            }

            return new List<string>
            {
                ChatGptStartUrl,
                GeminiStartUrl,
                ClaudeStartUrl
            };
        }

        private string GetProviderStartUrl(AiProvider provider)
        {
            switch (provider)
            {
                case AiProvider.ChatGpt:
                    return "https://chatgpt.com/?temporary-chat=true";
                case AiProvider.Gemini:
                    return GeminiStartUrl;
                case AiProvider.Claude:
                    return ClaudeStartUrl;
                default:
                    return ChatGptStartUrl;
            }
        }

        private async Task<bool> CreateTabAsync(string url, CancellationToken token = default(CancellationToken))
        {
            try
            {
                var encoded = Uri.EscapeDataString(url ?? string.Empty);
                var endpoint = DebugBase + "/json/new?" + encoded;

                try
                {
                    using (var put = new HttpRequestMessage(HttpMethod.Put, endpoint))
                    using (var putResp = await _http.SendAsync(put, token).ConfigureAwait(false))
                    {
                        if (putResp.IsSuccessStatusCode)
                        {
                            EnsureManagedChromeHidden(true);
                            return true;
                        }
                    }
                }
                catch
                {
                }

                using (var getResp = await _http.GetAsync(endpoint, token).ConfigureAwait(false))
                {
                    var created = getResp.IsSuccessStatusCode;
                    if (created)
                    {
                        EnsureManagedChromeHidden(true);
                    }
                    return created;
                }
            }
            catch
            {
                return false;
            }
        }

        private string GetTabTitle(dynamic tab)
        {
            try { return Convert.ToString(tab["title"]) ?? string.Empty; } catch { return string.Empty; }
        }

        private string GetTabId(dynamic tab)
        {
            try { return Convert.ToString(tab["id"]) ?? string.Empty; } catch { return string.Empty; }
        }

        private string GetTabType(dynamic tab)
        {
            try { return Convert.ToString(tab["type"]) ?? string.Empty; } catch { return string.Empty; }
        }

        private string GetTabUrl(dynamic tab)
        {
            try { return Convert.ToString(tab["url"]) ?? string.Empty; } catch { return string.Empty; }
        }

        private string GetWebSocketDebuggerUrl(dynamic tab)
        {
            try { return Convert.ToString(tab["webSocketDebuggerUrl"]) ?? string.Empty; } catch { return string.Empty; }
        }

        private async Task<bool> ActivateTabAsync(dynamic tab, CancellationToken token = default(CancellationToken))
        {
            var id = GetTabId(tab);
            if (string.IsNullOrWhiteSpace(id)) return false;

            try
            {
                using (var resp = await _http.GetAsync(DebugBase + "/json/activate/" + Uri.EscapeDataString(id), token).ConfigureAwait(false))
                {
                    var activated = resp.IsSuccessStatusCode;
                    if (activated)
                    {
                        EnsureManagedChromeHidden(true);
                    }
                    return activated;
                }
            }
            catch
            {
                return false;
            }
        }

        private AiProvider DetectProvider(string title, string url)
        {
            var t = (title ?? string.Empty).ToLowerInvariant();
            var u = (url ?? string.Empty).ToLowerInvariant();

            if (u.Contains("chatgpt.com") || t.Contains("chatgpt")) return AiProvider.ChatGpt;
            if (u.Contains("gemini.google.com") || t.Contains("gemini")) return AiProvider.Gemini;
            if (u.Contains("claude.ai") || t.Contains("claude")) return AiProvider.Claude;
            return AiProvider.Unknown;
        }

        private AiProvider ParsePreferredProvider(string preferredService)
        {
            var s = (preferredService ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Contains("chatgpt") || s.Contains("openai")) return AiProvider.ChatGpt;
            if (s.Contains("gemini") || s.Contains("google")) return AiProvider.Gemini;
            if (s.Contains("claude") || s.Contains("anthropic")) return AiProvider.Claude;
            return AiProvider.Unknown;
        }

        private static string ProviderToServiceName(AiProvider provider)
        {
            switch (provider)
            {
                case AiProvider.ChatGpt: return "ChatGPT";
                case AiProvider.Gemini: return "Gemini";
                case AiProvider.Claude: return "Claude";
                default: return "Unknown";
            }
        }

        private string DetectModelName(string title, AiProvider provider)
        {
            var t = (title ?? string.Empty).Trim();
            var lower = t.ToLowerInvariant();

            if (lower.Contains("gpt-5")) return "GPT-5";
            if (lower.Contains("gpt-4.1")) return "GPT-4.1";
            if (lower.Contains("gpt-4")) return "GPT-4";
            if (lower.Contains("o3")) return "o3";
            if (lower.Contains("o1")) return "o1";
            if (lower.Contains("gemini 2.5 pro")) return "Gemini 2.5 Pro";
            if (lower.Contains("gemini 2.5 flash")) return "Gemini 2.5 Flash";
            if (lower.Contains("gemini 2.0")) return "Gemini 2.0";
            if (lower.Contains("claude 3.5 sonnet")) return "Claude 3.5 Sonnet";
            if (lower.Contains("claude 3.7 sonnet")) return "Claude 3.7 Sonnet";
            if (lower.Contains("claude sonnet 4")) return "Claude Sonnet 4";
            if (lower.Contains("claude opus")) return "Claude Opus";

            switch (provider)
            {
                case AiProvider.ChatGpt: return "Auto";
                case AiProvider.Gemini: return "Auto";
                case AiProvider.Claude: return "Auto";
                default: return "Unknown";
            }
        }

        private string BuildSourceLabel(string service, string model)
        {
            var s = string.IsNullOrWhiteSpace(service) ? "Unknown" : service.Trim();
            var m = string.IsNullOrWhiteSpace(model) ? "Auto" : model.Trim();
            return s + " (" + m + ")";
        }

        private string BuildJsStringLiteral(string input)
        {
            try
            {
                return JsonConvert.SerializeObject(input ?? string.Empty);
            }
            catch
            {
                var value = input ?? string.Empty;
                return "\""
                    + value
                        .Replace("\\", "\\\\")
                        .Replace("\"", "\\\"")
                        .Replace("\r", "\\r")
                        .Replace("\n", "\\n")
                        .Replace("\t", "\\t")
                    + "\"";
            }
        }

        private bool IsError(string value)
        {
            return !string.IsNullOrEmpty(value) && value.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsNoSendStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            return v.IndexOf("no-send", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsPendingSendStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            return v.IndexOf("pending", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<string> ResolvePendingSendStatusAsync(AiProvider provider, string injectResult)
        {
            if (provider != AiProvider.Gemini && provider != AiProvider.Claude && provider != AiProvider.ChatGpt)
            {
                return injectResult;
            }

            var current = injectResult ?? string.Empty;
            var shouldPoll = IsPendingSendStatus(current) || string.Equals(current.Trim(), "ok", StringComparison.OrdinalIgnoreCase);
            if (!shouldPoll)
            {
                return current;
            }

            for (var i = 0; i < 28; i++)
            {
                await Task.Delay(250);
                var status = (await EvaluateJsAsync("(function(){return window.__cdts_last_send_status||'';})();", 2000) ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(status))
                {
                    continue;
                }

                if (IsNoSendStatus(status))
                {
                    return "ok:no-send";
                }

                if (status.IndexOf("sent", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "ok:" + status;
                }
            }

            return IsPendingSendStatus(current) ? "ok:no-send" : current;
        }

        private bool LooksLikeThinkingState(string value)
        {
            var completed = (value ?? string.Empty).Trim();
            // Words such as "analyzing" can legitimately occur in a final JSON
            // summary. A complete response envelope is not a loading label.
            foreach (var marker in new[] { "CDTS_EVENT_JSON", "CDTS_BATCH", "CDTS_JSON" })
            {
                // Provider containers can prepend UI labels such as "ChatGPT said:".
                // The ordered complete envelope, rather than its DOM position,
                // distinguishes actual JSON containing loading words from a status.
                var start = completed.IndexOf(marker + "_START", StringComparison.OrdinalIgnoreCase);
                if (start >= 0 && completed.IndexOf(marker + "_END", start + marker.Length + 6,
                    StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }
            var v = (value ?? string.Empty).ToLowerInvariant();
            if (v.Contains("thinking")
                || v.Contains("generating")
                || v.Contains("drafting")
                || v.Contains("cogitating")
                || v.Contains("mulling")
                || v.Contains("picturing")
                || v.Contains("crystallizing")
                || v.Contains("triangulating")
                || v.Contains("fathoming")
                || v.Contains("contemplating")
                || v.Contains("fetching from ")
                || v.Contains("fetching page")
                || v.Contains("failed to fetch ")
                || v.Contains("analyzing")
                || v.Contains("searching the web")
                || v.Contains("searching")
                || v.Contains("continue generating"))
            {
                return true;
            }

            // Claude renders its transient reasoning state as a short status word,
            // an icon, and the same status word again. The vocabulary changes, so
            // recognize the stable UI shape instead of relying only on known labels.
            var words = new string(v.Select(c => char.IsLetter(c) ? c : ' ').ToArray())
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 2
                && words[0].Length >= 5
                && string.Equals(words[0], words[1], StringComparison.OrdinalIgnoreCase);
        }

        private bool IsLikelyAssistantContent(string value)
        {
            var v = (value ?? string.Empty).Trim();
            // The assistant container includes this accessibility label even when
            // the response body is still just the provider name.
            const string chatGptLabel = "ChatGPT said:";
            if (v.StartsWith(chatGptLabel, StringComparison.OrdinalIgnoreCase))
                v = v.Substring(chatGptLabel.Length).Trim();
            // Provider responses can be intentionally concise (for example,
            // "OpenAI READY"). The provider-specific read scripts already
            // select assistant content, so only reject empty or known UI labels.
            if (v.Length < 4) return false;

            var lower = v.ToLowerInvariant();
            if (lower == "response complete" || lower == "latest response" || lower == "new chat")
                return false;
            if (lower == "write a message." || lower == "write a message" || lower == "write a message…") return false;
            if (lower == "you" || lower == "chatgpt" || lower == "gemini" || lower == "claude") return false;
            if (lower.StartsWith("you\n") || lower.StartsWith("chatgpt\n") || lower.StartsWith("gemini\n") || lower.StartsWith("claude\n")) return false;
            if (lower.StartsWith("return only ")) return false;
            if (lower.StartsWith("you are a ")) return false;
            if (lower.Contains("json data:")) return false;
            if (lower.Contains("no wrapper keys")) return false;
            if (lower.Contains("return only this exact token:")) return false;
            if (lower.Contains("cdts_rt_") && lower.Contains("return only")) return false;
            if (lower.Contains("cdts_json_start") && lower.Contains("return only")) return false;
            if (lower.Contains("please double-check responses") && lower.Contains("return only")) return false;
            if (lower.Contains("claude is ai and can make mistakes")) return false;
            if (lower.Contains("sonnet") && lower.Contains("claude is ai")) return false;
            if (lower.Contains("window.__reactroutercontext") || lower.Contains("streamcontroller.enqueue")) return false;
            if (lower.Contains("create an image") && lower.Contains("search the web")) return false;
            if (lower.Contains("sign up for free") || lower.Contains("welcome back") && lower.Contains("log in")) return false;
            if (lower.Contains("for safety") && lower.Contains("copy of this chat")) return false;

            return true;
        }

        private string BuildInjectScript(AiProvider provider, string escapedPrompt)
        {
            string commonInject = "var injectText=function(el,txt){"
                + "try{el.focus();}catch(e){}"
                + "try{el.click();}catch(e){}"
                + "if((el.tagName||'').toLowerCase()==='textarea'){"
                + "el.value='';el.value=txt;el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));return;"
                + "}"
                + "try{var sel=window.getSelection();var range=document.createRange();range.selectNodeContents(el);sel.removeAllRanges();sel.addRange(range);document.execCommand('insertText',false,txt);}catch(e){}"
                + "var cur=(el.innerText||el.textContent||'').trim();"
                + "if(cur!==txt.trim()){"
                + "el.innerText=txt;"
                + "try{el.dispatchEvent(new InputEvent('beforeinput',{bubbles:true,data:txt,inputType:'insertText'}));}catch(e){}"
                + "try{el.dispatchEvent(new InputEvent('input',{bubbles:true,data:txt,inputType:'insertText'}));}catch(e){el.dispatchEvent(new Event('input',{bubbles:true}));}"
                + "el.dispatchEvent(new Event('change',{bubbles:true}));"
                + "}"
                + "};";

            string commonStop = "var isStopLike=function(btn){if(!btn)return false;var al=((btn.getAttribute('aria-label')||'')+'').toLowerCase();var tt=((btn.getAttribute('title')||'')+'').toLowerCase();var dt=((btn.getAttribute('data-tooltip')||'')+'').toLowerCase();var tx=((btn.innerText||btn.textContent||'')+'').toLowerCase();return al.indexOf('stop')>=0||al.indexOf('cancel')>=0||tt.indexOf('stop')>=0||tt.indexOf('cancel')>=0||dt.indexOf('stop')>=0||dt.indexOf('cancel')>=0||tx.indexOf('stop')>=0||tx.indexOf('cancel')>=0;};"
                + "var hasStopVisible=function(){var nodes=document.querySelectorAll('button,[role=\"button\"]');for(var i=0;i<nodes.length;i++){if(isStopLike(nodes[i])){var r=nodes[i].getBoundingClientRect();if(r.width>0&&r.height>0)return true;}}return false;};";

            string commonSend = "var submittedTurns=function(){return document.querySelectorAll('[data-message-author-role=\"user\"],user-query,[data-testid=\"user-message\"],[data-content-search-unit-key$=\":user\"]').length;};var baselineTurns=submittedTurns();var trySend=function(attempt,box,findSendFn){"
                + "var method='none';"
                + "if(hasStopVisible()){window.__cdts_last_send_status='sent:stop-visible';return;}"
                + "var send=findSendFn();"
                + "if(send){"
                + "try{send.click();method='button';}catch(e){}"
                + "if(method==='none'){try{send.dispatchEvent(new MouseEvent('click',{view:window,bubbles:true,cancelable:true}));method='button';}catch(e){}}"
                + "}"
                + "if(method==='none'){try{var form=box.closest('form');if(form){if(form.requestSubmit){form.requestSubmit();method='form';}else if(form.submit){form.submit();method='form';}}}catch(e){}}"
                + "if(method==='none'){var target=document.activeElement||box;try{target.focus();}catch(e){}try{target.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}));}catch(e){}try{target.dispatchEvent(new KeyboardEvent('keypress',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}));}catch(e){}try{target.dispatchEvent(new KeyboardEvent('keyup',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}));}catch(e){}method='keyboard';}"
                + "setTimeout(function(){"
                + "var probablySent=(submittedTurns()>baselineTurns || hasStopVisible());"
                + "if(probablySent){window.__cdts_last_send_status='sent:'+method;return;}"
                + "if(attempt>=10){window.__cdts_last_send_status='no-send';return;}"
                + "setTimeout(function(){trySend(attempt+1,box,findSendFn);},150);"
                + "},200);"
                + "};";

            if (provider == AiProvider.Claude)
            {
                return "(function(){"
                     + "var p=" + escapedPrompt + ";"
                     + "window.__cdts_last_send_status='pending';"
                     + "var box=document.querySelector('div.ProseMirror[contenteditable=\"true\"]')||document.querySelector('div[contenteditable=\"true\"][translate=\"no\"]')||document.querySelector('div[contenteditable=\"true\"][data-testid*=\"chat-input\"]')||document.querySelector('div[contenteditable=\"true\"][aria-label*=\"Talk to Claude\"]')||document.querySelector('div[contenteditable=\"true\"]');"
                     + "if(!box)return 'error: claude input not found';"
                     + commonInject
                     + "injectText(box, p);"
                     + commonStop
                     + "var findSend=function(){var sels=['button[aria-label*=\"Send Message\"]','button[aria-label*=\"Send message\"]','button[aria-label=\"Send\"]','button[data-testid=\"send-button\"]','button[data-testid*=\"send\"]'];for(var si=0;si<sels.length;si++){var nodes=document.querySelectorAll(sels[si]);for(var ni=0;ni<nodes.length;ni++){var n=nodes[ni];if(n&&!n.disabled&&n.getAttribute('aria-disabled')!=='true'&&!isStopLike(n)){var r=n.getBoundingClientRect();if(r.width>0&&r.height>0)return n;}}}return null;};"
                     + commonSend
                     + "setTimeout(function(){trySend(0,box,findSend);},100);"
                     + "return 'ok:pending';"
                     + "})();";
            }

            if (provider == AiProvider.Gemini)
            {
                return "(function(){"
                     + "var p=" + escapedPrompt + ";"
                     + "window.__cdts_last_send_status='pending';"
                     + "var visible=function(n){if(!n)return false;var r=n.getBoundingClientRect();return r.width>0&&r.height>0&&r.left>-1000&&r.top>-1000;};"
                     + "var pick=function(sels){for(var si=0;si<sels.length;si++){var nodes=document.querySelectorAll(sels[si]);for(var ni=0;ni<nodes.length;ni++){if(visible(nodes[ni]))return nodes[ni];}}return null;};"
                     + "var box=pick(['rich-textarea div.ql-editor[contenteditable=\"true\"][aria-label*=\"prompt\"]','div.ql-editor[contenteditable=\"true\"][aria-label*=\"prompt\"]','div[contenteditable=\"true\"][aria-label*=\"Enter a prompt\"]','rich-textarea div[contenteditable=\"true\"]','div[contenteditable=\"true\"][role=\"textbox\"]']);"
                     + "if(!box)return 'error: gemini input not found';"
                     + commonInject
                     + "injectText(box, p);"
                     + commonStop
                     + "var findSend=function(){var sels=['button[aria-label*=\"Send\"]','button[aria-label*=\"send\"]','button[aria-label*=\"Submit\"]','button[aria-label*=\"submit\"]','button[mattooltip*=\"Send\"]','button[mattooltip*=\"send\"]','button[data-test-id*=\"send\"]','button.send-button'];for(var si=0;si<sels.length;si++){var nodes=document.querySelectorAll(sels[si]);for(var ni=0;ni<nodes.length;ni++){var n=nodes[ni];if(n&&!n.disabled&&n.getAttribute('aria-disabled')!=='true'&&!isStopLike(n)){var r=n.getBoundingClientRect();if(r.width>0&&r.height>0)return n;}}}return null;};"
                     + commonSend
                     + "setTimeout(function(){trySend(0,box,findSend);},100);"
                     + "return 'ok:pending';"
                     + "})();";
            }

              return "(function(){"
                 + "var p=" + escapedPrompt + ";"
                 + "window.__cdts_last_send_status='pending';"
                  + "var box=document.querySelector('#prompt-textarea')||document.querySelector('[data-testid=\"composer-textarea\"]')||document.querySelector('div.ProseMirror[contenteditable=\"true\"]')||document.querySelector('div[contenteditable=\"true\"][data-testid*=\"composer\"]')||document.querySelector('div[contenteditable=\"true\"][aria-label*=\"Message\"]')||document.querySelector('textarea')||document.querySelector('div[contenteditable=\"true\"]');"
                 + "if(!box)return 'error: chatgpt input not found';"
                 + commonInject
                 + "injectText(box, p);"
                 + commonStop
                  + "var findSend=function(){var sels=['[data-testid=\"send-button\"]','button[data-testid*=\"send\"]','button[aria-label*=\"Send\"]','button[aria-label*=\"send\"]','button[type=\"submit\"]'];for(var si=0;si<sels.length;si++){var nodes=document.querySelectorAll(sels[si]);for(var ni=0;ni<nodes.length;ni++){var n=nodes[ni];if(n&&!n.disabled&&n.getAttribute('aria-disabled')!=='true'&&!isStopLike(n)){var r=n.getBoundingClientRect();if(r.width>0&&r.height>0)return n;}}}return null;};"
                 + commonSend
                 + "setTimeout(function(){trySend(0,box,findSend);},100);"
                 + "return 'ok:pending';"
                 + "})();";
        }

        private string BuildReadScript(AiProvider provider)
        {
            if (provider == AiProvider.Claude)
            {
                return @"(function(){
                    var direct=document.querySelectorAll('[data-transcript-engine-root] .prose');
                    for(var di=direct.length-1;di>=0;di--){
                        var directText=(direct[di].innerText||direct[di].textContent||'').trim();
                        if(directText.length>3)return directText;
                    }
                    var sels=[
                        '[data-testid=""assistant-message""]',
                        '[data-testid=""assistant-turn""]',
                        '[data-message-author-role=""assistant""]',
                        'article[data-author*=""assistant""]',
                        '[data-is-assistant=""true""]',
                        '[data-testid*=""assistant""]',
                        '[data-testid*=""message"" i]',
                        '[class*=""font-claude"" i]',
                        '[class*=""prose"" i]'
                    ];
                    var textSels=['.prose','[class*=""prose"" i]','[class*=""font-claude"" i]','[data-testid*=""message-content""]','[data-testid*=""markdown""]','p','li','pre','code'];
                    var isPromptLike=function(t){
                        var x=(t||'').trim().toLowerCase();
                        if(!x)return true;
                        if(x.indexOf('return only ')===0)return true;
                        if(x.indexOf('return only this exact token:')>=0)return true;
                        if(x.indexOf('you are a ')===0)return true;
                        if(x.indexOf('json data:')>=0)return true;
                        if(x.indexOf('no wrapper keys')>=0)return true;
                        if(x.indexOf('cdts_rt_')>=0 && x.indexOf('return only')>=0)return true;
                        if(x.indexOf('cdts_json_start')>=0 && x.indexOf('return only')>=0)return true;
                        if(x.indexOf('please double-check responses')>=0 && x.indexOf('return only')>=0)return true;
                        if(x.indexOf('window.__reactroutercontext')>=0 || x.indexOf('streamcontroller.enqueue')>=0)return true;
                        return false;
                    };
                    var hasBlockedAncestor=function(node){
                        if(!node || !node.closest)return false;
                        var blocked=node.closest('form,textarea,[role=""textbox""],[contenteditable=""true""],[data-testid*=""chat-input""],[data-testid*=""composer""],[data-testid*=""prompt""],[class*=""composer-input""],[class*=""chat-input""]');
                        return !!blocked;
                    };
                    var readNodeText=function(node){
                        if(!node)return '';
                        // Claude's Markdown typography changes JSON quotes in innerText.
                        // Read the original text only from this assistant turn's data.
                        var turnKey=node.getAttribute('data-turn-key');
                        var fiberKey=Object.keys(node).filter(function(k){return k.indexOf('__reactFiber')===0;})[0];
                        var fiber=fiberKey?node[fiberKey]:null;
                        for(var depth=0;turnKey&&fiber&&depth<16;depth++,fiber=fiber.return){
                            var turn=fiber.memoizedProps&&fiber.memoizedProps.turn;
                            if(!turn||turn.role!=='assistant'||(turn.key!==turnKey&&turn.id!==turnKey))continue;
                            var original=(turn.items||[]).filter(function(item){return item.kind==='text'&&typeof item.text==='string';})
                                .map(function(item){return item.text;}).join('\n').trim();
                            if(original)return original;
                        }
                        var best='';
                        for(var ts=0;ts<textSels.length;ts++){
                            var parts=node.querySelectorAll(textSels[ts]);
                            for(var pi=0;pi<parts.length;pi++){
                                if(hasBlockedAncestor(parts[pi]))continue;
                                var part=(parts[pi].innerText||parts[pi].textContent||'').trim();
                                if(part.length>best.length)best=part;
                            }
                        }
                        if(best.length<16){
                            best=(node.innerText||node.textContent||'').trim();
                        }
                        return best;
                    };
                    var newest='';
                    for(var s=0;s<sels.length;s++){
                        var nodes=document.querySelectorAll(sels[s]);
                        for(var i=0;i<nodes.length;i++){
                            if(hasBlockedAncestor(nodes[i]))continue;
                            var txt=readNodeText(nodes[i]);
                            if(txt.length>15 && !isPromptLike(txt))newest=txt;
                        }
                        if(newest)return newest;
                    }
                    if(!newest){
                        var all=document.querySelectorAll('[data-testid*=""assistant-turn""],[data-message-author-role=""assistant""],article[data-author*=""assistant""],[data-is-assistant=""true""],[data-testid*=""assistant""],[class*=""font-claude"" i],[class*=""prose"" i]');
                        for(var j=0;j<all.length;j++){
                            if(hasBlockedAncestor(all[j]))continue;
                            var t=readNodeText(all[j]);
                            if(t.length>15 && t.toLowerCase().indexOf('you said')<0 && !isPromptLike(t))newest=t;
                        }
                    }
                    if(!newest)return '';
                    return newest;
                })();";
            }

            if (provider == AiProvider.Gemini)
            {
                return @"(function(){
                    var candidates=[];
                    var sels=['model-response message-content .markdown','model-response message-content','.model-response-text message-content','.model-response-text .markdown','message-content .markdown','message-content'];
                    var isPromptLike=function(t){
                        var x=(t||'').trim().toLowerCase();
                        if(!x)return true;
                        if(x.indexOf('return only ')===0)return true;
                        if(x.indexOf('return only this exact token:')>=0)return true;
                        if(x.indexOf('you are a ')===0)return true;
                        if(x.indexOf('you said ')===0)return true;
                        if(x.indexOf('json data:')>=0)return true;
                        if(x.indexOf('no wrapper keys')>=0)return true;
                        if(x.indexOf('cdts_rt_')>=0 && x.indexOf('return only')>=0)return true;
                        if(x.indexOf('cdts_json_start')>=0 && x.indexOf('return only')>=0)return true;
                        if(x.indexOf('cdts_event_json_start')>=0 && x.indexOf('return only')>=0)return true;
                        return false;
                    };
                    var hasBlockedAncestor=function(node){
                        if(!node || !node.closest)return false;
                        var blocked=node.closest('rich-textarea,form,textarea,[role=""textbox""],[contenteditable=""true""],user-query');
                        return !!blocked;
                    };
                    var clean=function(txt){
                        var value=(txt||'').trim();
                        value=value.replace(/^gemini said\s*/i,'').trim();
                        return value;
                    };
                    for(var s=0;s<sels.length;s++){
                        var nodes=document.querySelectorAll(sels[s]);
                        for(var i=0;i<nodes.length;i++){
                            if(hasBlockedAncestor(nodes[i]))continue;
                            var txt=clean(nodes[i].innerText||nodes[i].textContent||'');
                            if(txt.length>8 && !isPromptLike(txt))candidates.push(txt);
                        }
                    }
                    if(candidates.length===0)return '';
                    return candidates[candidates.length-1];
                })();";
            }

            return @"(function(){
                var sels=[
                    '[data-markdown-text-style=""assistant-message""]',
                    '[data-chatgpt-copy-reference][data-dil-source-message-id]',
                    '[data-testid=""conversation-turn-assistant""]',
                    '[data-message-author-role=""assistant""]',
                    'article[data-testid*=""conversation-turn""] [data-message-author-role=""assistant""]',
                    'article[data-testid*=""conversation-turn-assistant""]',
                    '[data-content-search-unit-key$="":assistant""]'
                ];
                var textSels=['.markdown','.prose','p','li','pre','code'];
                var isPromptLike=function(t){
                    var x=(t||'').trim().toLowerCase();
                    if(!x)return true;
                    if(x.indexOf('return only ')===0)return true;
                    if(x.indexOf('return only this exact token:')>=0)return true;
                    if(x.indexOf('you are a ')===0)return true;
                    if(x.indexOf('json data:')>=0)return true;
                    if(x.indexOf('no wrapper keys')>=0)return true;
                    if(x.indexOf('cdts_rt_')>=0 && x.indexOf('return only')>=0)return true;
                    if(x.indexOf('cdts_json_start')>=0 && x.indexOf('return only')>=0)return true;
                    if(x.indexOf('please double-check responses')>=0 && x.indexOf('return only')>=0)return true;
                    if(x.indexOf('window.__reactroutercontext')>=0 || x.indexOf('streamcontroller.enqueue')>=0)return true;
                    return false;
                };
                var hasBlockedAncestor=function(node){
                    if(!node || !node.closest)return false;
                    var blocked=node.closest('form,textarea,[role=""textbox""],[contenteditable=""true""],[data-testid*=""composer""],[data-testid*=""prompt""],[class*=""composer-input""],[class*=""chat-input""]');
                    return !!blocked;
                };
                var readNodeText=function(node){
                    if(!node)return '';
                    var best=(node.innerText||node.textContent||'').trim();
                    for(var ts=0;ts<textSels.length;ts++){
                        var parts=node.querySelectorAll(textSels[ts]);
                        for(var pi=0;pi<parts.length;pi++){
                            if(hasBlockedAncestor(parts[pi]))continue;
                            var part=(parts[pi].innerText||parts[pi].textContent||'').trim();
                            if(part.length>best.length)best=part;
                        }
                    }
                    if(best.length<16){
                        best=(node.innerText||node.textContent||'').trim();
                    }
                    return best;
                };
                var newest='';
                for(var s=0;s<sels.length;s++){
                    var nodes=document.querySelectorAll(sels[s]);
                    for(var i=0;i<nodes.length;i++){
                        if(hasBlockedAncestor(nodes[i]))continue;
                        var txt=readNodeText(nodes[i]);
                        if(txt.length>8 && !isPromptLike(txt))newest=txt;
                    }
                }
                if(!newest){
                    var all=document.querySelectorAll('main article,[data-testid*=""conversation-turn""],[data-message-author-role=""assistant""]');
                    for(var j=0;j<all.length;j++){
                        if(hasBlockedAncestor(all[j]))continue;
                        var t=readNodeText(all[j]);
                        if(t.length>8 && !isPromptLike(t))newest=t;
                    }
                }
                if(!newest)return '';
                return newest;
            })();";
        }

        private string BuildGenericReadScript()
        {
            return @"(function(){
                var candidates=[];
                var nodes=document.querySelectorAll('.markdown,[role=""article""],article,div');
                for(var i=0;i<nodes.length;i++){
                    var t=(nodes[i].innerText||'').trim();
                    if(t.length>80)candidates.push(t);
                }
                if(candidates.length===0)return '';
                return candidates[candidates.length-1];
            })();";
        }

        private string BuildStartFreshChatScript(AiProvider provider)
        {
            if (provider == AiProvider.ChatGpt)
            {
                return @"(function(){
                    try{
                        window.__cdts_last_send_status='';
                        window.__cdts_navigation_pending=true;
                        var target='https://chatgpt.com/?temporary-chat=true';
                        if(location.href.indexOf('chatgpt.com')<0 && location.href.indexOf('chat.openai.com')<0){
                            location.assign(target);
                            return 'ok:navigate-root';
                        }

                        location.assign(target);
                        return 'ok:navigate-root';
                    }catch(e){
                        return 'error: '+(e && e.message ? e.message : 'unknown');
                    }
                })();";
            }

            if (provider == AiProvider.Gemini)
            {
                return @"(function(){
                    try{
                        window.__cdts_last_send_status='';
                        window.__cdts_navigation_pending=true;
                        location.assign('https://gemini.google.com/app');
                        return 'ok:navigate-root';
                    }catch(e){
                        return 'error: '+(e && e.message ? e.message : 'unknown');
                    }
                })();";
            }

            if (provider == AiProvider.Claude)
            {
                return @"(function(){
                    try{
                        var clicked=false;
                        var sels=[
                            'a[href*=""/new""]',
                            'button[aria-label*=""New chat""]',
                            'button[data-testid*=""new-chat""]'
                        ];
                        for(var s=0;s<sels.length;s++){
                            var nodes=document.querySelectorAll(sels[s]);
                            for(var i=0;i<nodes.length;i++){
                                var n=nodes[i];
                                if(n && !n.disabled){ n.click(); clicked=true; }
                            }
                        }
                        return clicked ? 'ok:new-chat' : 'ok:no-new-chat';
                    }catch(e){
                        return 'error: '+(e && e.message ? e.message : 'unknown');
                    }
                })();";
            }

            return @"(function(){return 'ok';})();";
        }

        /// <summary>
        /// Returns a JS script that checks the current page for rate-limit / quota / throttle
        /// indicators visible in the DOM. Returns a JSON object: { rateLimited: true|false, reason: "..." }.
        /// Each provider has its own DOM patterns for rate-limit pages.
        /// </summary>
        private static string BuildRateLimitDetectionScript(AiProvider provider)
        {
            // Common rate-limit text patterns searched across all providers.
            const string commonRateLimitPattern = @"
                var rateLimitTokens=['rate limit','rate-limited','too many requests','you have reached',
                    'you have exceeded','usage limit','quota exceeded','try again later',
                    'please slow down','you are sending requests too quickly','429',
                    'capacity limit','usage cap','request limit','hourly limit',
                    'daily limit','we are processing too many','too fast',
                    'cloudflare','cf-challenge','cf_captcha','cf-please-wait',
                    'verify you are human','checking your browser',
                    'access denied','blocked','temporarily unavailable',
                    'service is busy','high demand','experiencing high volume'];
                // Article text and hashes are data, not provider quota notifications.
                var contentSelector='user-query,model-response,message-content,[data-message-author-role],[data-markdown-text-style],[data-markdown-text-tone],[data-content-search-unit-key],[data-testid*=""conversation-turn""],[data-testid*=""assistant-turn""],[data-testid=""assistant-message""],[data-testid=""user-message""],.prose,[class*=""font-claude""],form,textarea,[contenteditable=""true""],script,style';
                var isConversationContent=function(node){return !!(node&&node.closest&&node.closest(contentSelector));};
                var providerPageText=function(){
                    if(!document.body)return '';
                    var copy=document.body.cloneNode(true);
                    var content=copy.querySelectorAll(contentSelector);
                    for(var i=0;i<content.length;i++)content[i].remove();
                    return (copy.innerText||copy.textContent||'').toLowerCase();
                };
                var matchesLimitToken=function(text,token){return token==='429'?/\b429\b/.test(text):text.indexOf(token)>=0;};
                // Outside notification elements require an explicit quota message.
                // Article titles can contain generic words such as blocked.
                var pageLimitTokens=['too many requests','quota exceeded','you have reached your limit',
                    'you have reached your usage limit','you have exceeded your quota',
                    'verify you are human','checking your browser'];";

            if (provider == AiProvider.ChatGpt)
            {
                return @"(function(){
                    try{
                        " + commonRateLimitPattern + @"
                        var bodyText=providerPageText();
                        // Check for ChatGPT-specific rate-limit banners.
                        var banners=document.querySelectorAll('[role=""alert""],[data-testid*=""banner""],[class*=""notification""],[class*=""alert""],[class*=""warning""],[class*=""error""],.bg-red-50,.bg-orange-50,.bg-yellow-50');
                        for(var i=0;i<banners.length;i++){
                            if(isConversationContent(banners[i]))continue;
                            var t=(banners[i].innerText||banners[i].textContent||'').toLowerCase();
                            for(var j=0;j<rateLimitTokens.length;j++){
                                if(matchesLimitToken(t,rateLimitTokens[j])){
                                    return JSON.stringify({rateLimited:true,reason:'chatgpt-banner:'+rateLimitTokens[j]});
                                }
                            }
                        }
                        // Check full body text.
                        for(var k=0;k<pageLimitTokens.length;k++){
                            if(matchesLimitToken(bodyText,pageLimitTokens[k])){
                                return JSON.stringify({rateLimited:true,reason:'chatgpt-body:'+pageLimitTokens[k]});
                            }
                        }
                        // Check for Cloudflare challenge on chatgpt.com.
                        var cfChallenge=document.getElementById('cf-challenge')||document.querySelector('[id*=""cf-""]')||document.querySelector('iframe[src*=""cloudflare""]');
                        if(cfChallenge){
                            return JSON.stringify({rateLimited:true,reason:'chatgpt-cloudflare-challenge'});
                        }
                        // Check URL for challenge indicators.
                        var href=(location.href||'').toLowerCase();
                        if(href.indexOf('challenge')>=0||href.indexOf('verify')>=0||href.indexOf('blocked')>=0){
                            return JSON.stringify({rateLimited:true,reason:'chatgpt-url-challenge'});
                        }
                        return JSON.stringify({rateLimited:false,reason:''});
                    }catch(e){return JSON.stringify({rateLimited:false,reason:'error:'+e.message});}
                })();";
            }

            if (provider == AiProvider.Gemini)
            {
                return @"(function(){
                    try{
                        " + commonRateLimitPattern + @"
                        var bodyText=providerPageText();
                        var currentUrl=(location.href||'').toLowerCase();
                        if(currentUrl.indexOf('google.com/sorry')>=0
                            || bodyText.indexOf('unusual traffic')>=0
                            || bodyText.indexOf('detected unusual traffic')>=0){
                            return JSON.stringify({rateLimited:true,reason:'gemini-google-verification'});
                        }
                        // Gemini error/snackbar banners.
                        var banners=document.querySelectorAll('[class*=""error""],[class*=""snackbar""],[class*=""toast""],[class*=""banner""],[role=""alert""]');
                        for(var i=0;i<banners.length;i++){
                            if(isConversationContent(banners[i]))continue;
                            var t=(banners[i].innerText||banners[i].textContent||'').toLowerCase();
                            for(var j=0;j<rateLimitTokens.length;j++){
                                if(matchesLimitToken(t,rateLimitTokens[j])){
                                    return JSON.stringify({rateLimited:true,reason:'gemini-banner:'+rateLimitTokens[j]});
                                }
                            }
                        }
                        for(var k=0;k<pageLimitTokens.length;k++){
                            if(matchesLimitToken(bodyText,pageLimitTokens[k])){
                                return JSON.stringify({rateLimited:true,reason:'gemini-body:'+pageLimitTokens[k]});
                            }
                        }
                        return JSON.stringify({rateLimited:false,reason:''});
                    }catch(e){return JSON.stringify({rateLimited:false,reason:'error:'+e.message});}
                })();";
            }

            if (provider == AiProvider.Claude)
            {
                return @"(function(){
                    try{
                        " + commonRateLimitPattern + @"
                        var bodyText=providerPageText();
                        // Claude rate-limit / error banners.
                        var claudeLimitTokens=rateLimitTokens.concat(['message limit','upgrade to keep chatting']);
                        var banners=document.querySelectorAll('[class*=""error""],[class*=""banner""],[class*=""alert""],[class*=""warning""],[class*=""toast""],[role=""alert""],[role=""dialog""],[role=""alertdialog""]');
                        for(var i=0;i<banners.length;i++){
                            if(isConversationContent(banners[i]))continue;
                            var rect=banners[i].getBoundingClientRect();
                            if(!rect.width||!rect.height)continue;
                            var t=(banners[i].innerText||banners[i].textContent||'').toLowerCase();
                            for(var j=0;j<claudeLimitTokens.length;j++){
                                if(matchesLimitToken(t,claudeLimitTokens[j])){
                                    return JSON.stringify({rateLimited:true,reason:'claude-banner:'+claudeLimitTokens[j]});
                                }
                            }
                        }
                        for(var k=0;k<pageLimitTokens.length;k++){
                            if(matchesLimitToken(bodyText,pageLimitTokens[k])){
                                return JSON.stringify({rateLimited:true,reason:'claude-body:'+pageLimitTokens[k]});
                            }
                        }
                        return JSON.stringify({rateLimited:false,reason:''});
                    }catch(e){return JSON.stringify({rateLimited:false,reason:'error:'+e.message});}
                })();";
            }

            return @"(function(){return JSON.stringify({rateLimited:false,reason:'unknown-provider'});})();";
        }

        /// <summary>
        /// Quick pre-injection check: reads page content to detect if the provider is currently
        /// rate-limited. Returns (true, reason) if rate-limited, (false, "") if clear.
        /// </summary>
        private async Task<Tuple<bool, string>> DetectPageRateLimitAsync(AiProvider provider)
        {
            try
            {
                var script = BuildRateLimitDetectionScript(provider);
                var raw = await EvaluateJsAsync(script, 4000).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw)) return Tuple.Create(false, string.Empty);

                var trimmed = raw.Trim();
                // Parse the JSON object.
                int rateLimitedIdx = trimmed.IndexOf("\"rateLimited\":true", StringComparison.Ordinal);
                if (rateLimitedIdx < 0) return Tuple.Create(false, string.Empty);

                // Extract reason.
                string reason = string.Empty;
                int reasonIdx = trimmed.IndexOf("\"reason\":\"", StringComparison.Ordinal);
                if (reasonIdx >= 0)
                {
                    int start = reasonIdx + 10;
                    int end = trimmed.IndexOf("\"", start, StringComparison.Ordinal);
                    if (end > start) reason = trimmed.Substring(start, end - start);
                }
                if (string.IsNullOrWhiteSpace(reason)) reason = "detected";

                Log("[RateLimit] Page rate-limited: " + ProviderToServiceName(provider) + " reason=" + reason);
                return Tuple.Create(true, reason);
            }
            catch (Exception ex)
            {
                Log("[RateLimit] Detection check failed: " + ex.Message);
                return Tuple.Create(false, string.Empty);
            }
        }

        /// <summary>
        /// Checks if a response text (from polling or direct read) looks like a rate-limit message
        /// rather than a genuine AI response. Used to bail out of polling early.
        /// </summary>
        private static bool LooksLikeRateLimitResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return false;
            var lowered = response.ToLowerInvariant();
            // Short-circuit: rate-limit pages are usually short.
            if (lowered.Length > 2000) return false;
            var tokens = new[] {
                "rate limit", "rate-limited", "too many requests", "you have reached",
                "you have exceeded", "usage limit", "quota exceeded",
                "please slow down", "try again later", "429", "hourly limit",
                "daily limit", "request limit", "cloudflare", "access denied"
            };
            int hits = 0;
            foreach (var t in tokens)
            {
                if (lowered.IndexOf(t, StringComparison.Ordinal) >= 0) hits++;
            }
            // Require at least 2 hits to avoid false positives on single-word matches.
            return hits >= 2;
        }

        private bool ContainsIgnoreCase(string value, string needle)
        {
            return (value ?? string.Empty).IndexOf(needle ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsActiveTab(dynamic tab)
        {
            try
            {
                if (tab == null) return false;
                var dict = tab as IDictionary<string, object>;
                if (dict == null || !dict.ContainsKey("active") || dict["active"] == null) return false;

                var activeObj = dict["active"];
                if (activeObj is bool) return (bool)activeObj;

                bool active;
                return bool.TryParse(activeObj.ToString(), out active) && active;
            }
            catch
            {
                return false;
            }
        }

        private void Log(string msg)
        {
            var fullMsg = "[ChromeSidecar] " + msg;
            Util.Log.Debug(fullMsg);
#if DEBUG
            try { Console.Error.WriteLine(fullMsg); } catch { }
#endif
            OnLog?.Invoke(fullMsg);
        }

        public void Dispose()
        {
            MarkDisconnected("Chrome sidecar disposed.");
            StopReceivePump();
            if (!KeepManagedChromeAlive)
            {
                StopManagedChrome();
            }
            try { _ws?.Dispose(); } catch { }
            try { _http?.Dispose(); } catch { }
            try { _connectLock?.Dispose(); } catch { }
            try { _sendLock?.Dispose(); } catch { }
            try { _queryLock?.Dispose(); } catch { }
            try { _tabCreationLock?.Dispose(); } catch { }
        }
    }
}
