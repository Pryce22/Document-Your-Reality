using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>HTTP client for the Document Your Reality backend (v0.5.0).</summary>
public class DyrBackendClient : MonoBehaviour
{
    const string Tag = "client";

    [SerializeField] private string backendBaseUrl = "http://127.0.0.1:8000";
    [Tooltip("Ask the LAN where the backend is before using the URL above, which a build cannot know.")]
    [SerializeField] private bool discoverBackend = true;
    [Tooltip("How long to wait for an answer before using the URL above.")]
    [SerializeField] private float discoverySeconds = 1.5f;
    [Tooltip("Seconds before a /scan is abandoned. Must be LARGER than the backend's llm.timeout_s.")]
    [SerializeField] private int scanTimeoutSeconds = 420;
    [SerializeField] private int defaultTimeoutSeconds = 15;
    [Tooltip("Log every request start/end (recommended while wiring the scene up).")]
    [SerializeField] private bool verboseLogging = true;
    [Tooltip("Ping GET /health on Start to verify the backend is reachable.")]
    [SerializeField] private bool healthCheckOnStart = true;

    public enum LlmState { Unknown, Up, Down, Untested }

    /// <summary>LLM verdict from the backend's /health (generation probe result).</summary>
    public LlmState LlmProbeState { get; private set; } = LlmState.Unknown;
    /// <summary>Human-readable LLM status line, for an on-screen HUD.</summary>
    public string LlmStatusLine { get; private set; } = "LLM: not checked yet";

    /// <summary>Set the URL on a client created at runtime.</summary>
    public void SetBaseUrlIfUnset(string url)
    {
        if (!string.IsNullOrEmpty(url)) backendBaseUrl = url;
    }

    string Url(string path) { return backendBaseUrl.TrimEnd('/') + path; }

    /// <summary>False until the address is settled, so no request goes to one about to change.</summary>
    public bool AddressResolved { get; private set; }

    void Start()
    {
        DyrLog.Info(Tag, "Backend base URL: " + backendBaseUrl
            + " | platform=" + Application.platform
            + " | timeouts: scan=" + scanTimeoutSeconds + "s default=" + defaultTimeoutSeconds + "s");
        StartCoroutine(Open());
    }

    /// <summary>Settle on an address, then check it.</summary>
    IEnumerator Open()
    {
        if (discoverBackend && !LoopbackIsAlreadyRight()) yield return Discover();
        AddressResolved = true;

        // Classic mistake: localhost on the headset points to the HEADSET itself.
        bool isLocalhost = backendBaseUrl.Contains("127.0.0.1") || backendBaseUrl.Contains("localhost");
        if (isLocalhost && Application.platform == RuntimePlatform.Android)
        {
            DyrLog.Error(Tag, "backendBaseUrl is localhost but we are running ON THE HEADSET: "
                + "127.0.0.1 here is the Quest itself, NOT your PC. Nothing answered the "
                + "discovery probe either, so either the backend is not running, it is on "
                + "another network, or Windows Firewall is blocking UDP " + DiscoveryPort
                + " and TCP 8000. Failing that, set the PC's LAN IP by hand.");
        }

        if (healthCheckOnStart) yield return CheckHealth();
    }

    /// <summary>Leave a loopback address alone off Android: on this PC it needs no firewall rule.</summary>
    bool LoopbackIsAlreadyRight()
    {
        bool loopback = backendBaseUrl.Contains("127.0.0.1") || backendBaseUrl.Contains("localhost");
        return loopback && Application.platform != RuntimePlatform.Android;
    }

    const int DiscoveryPort = 8001;
    string _discovered;
    string _discoveryError;

    /// <summary>Ask the LAN where the backend is, on a worker thread: the socket call blocks.</summary>
    IEnumerator Discover()
    {
        _discovered = null;
        _discoveryError = "";
        int timeoutMs = Mathf.RoundToInt(Mathf.Max(0.1f, discoverySeconds) * 1000f);

        Thread worker = new Thread(() =>
        {
            try
            {
                using (UdpClient socket = new UdpClient())
                {
                    socket.EnableBroadcast = true;
                    socket.Client.ReceiveTimeout = timeoutMs;
                    byte[] probe = Encoding.UTF8.GetBytes("DYR?");
                    socket.Send(probe, probe.Length,
                        new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    string reply = Encoding.UTF8.GetString(socket.Receive(ref from)).Trim();
                    if (reply.StartsWith("DYR "))
                        _discovered = reply.Substring(4).Trim();
                    else
                        _discoveryError = "unexpected reply '" + reply + "' from " + from.Address;
                }
            }
            catch (Exception error)
            {
                _discoveryError = error.Message;
            }
        });
        worker.IsBackground = true;
        worker.Start();

        float deadline = Time.realtimeSinceStartup + discoverySeconds + 1f;
        while (worker.IsAlive && Time.realtimeSinceStartup < deadline) yield return null;

        if (!string.IsNullOrEmpty(_discovered))
        {
            DyrLog.Info(Tag, "Discovery: the backend answered from " + _discovered
                + (_discovered == backendBaseUrl ? " (same as the built-in address)"
                    : " -> using it instead of the built-in " + backendBaseUrl));
            backendBaseUrl = _discovered;
            yield break;
        }

        DyrLog.Warn(Tag, "Discovery found nothing in " + discoverySeconds.ToString("F1") + "s"
            + (string.IsNullOrEmpty(_discoveryError) ? "" : " (" + _discoveryError + ")")
            + " -> falling back to the built-in " + backendBaseUrl
            + ". A network that blocks broadcast does this, and so does a backend that is not "
            + "running yet.");
    }


    public IEnumerator CheckHealth()
    {
        DyrLog.Info(Tag, "Health check -> GET " + Url("/health") + " ...");
        using (UnityWebRequest req = UnityWebRequest.Get(Url("/health")))
        {
            req.timeout = defaultTimeoutSeconds;
            float t0 = Time.realtimeSinceStartup;
            yield return req.SendWebRequest();
            float ms = (Time.realtimeSinceStartup - t0) * 1000f;

            if (req.result == UnityWebRequest.Result.Success)
            {
                DyrLog.Info(Tag, "HEALTH OK in " + ms.ToString("F0") + " ms -> " + req.downloadHandler.text);
                ParseLlmStatus(req.downloadHandler.text);
            }
            else
            {
                DyrLog.Error(Tag, "HEALTH CHECK FAILED after " + ms.ToString("F0") + " ms -> " + DescribeError(req));
                LogConnectionHints(req);
            }
        }
    }

    void ParseLlmStatus(string json)
    {
        HealthDto health = ParseJson<HealthDto>(json, "health");
        if (health == null) return;

        if (health.llm_state == "up") LlmProbeState = LlmState.Up;
        else if (health.llm_state == "down") LlmProbeState = LlmState.Down;
        else LlmProbeState = LlmState.Untested;

        LlmStatusLine = "LLM " + health.llm_state + " (" + health.llm_model + ")"
            + (LlmProbeState == LlmState.Up ? " " + health.llm_probe_latency_s.ToString("F1") + "s" : "")
            + " | " + health.objects + " objects known";

        if (LlmProbeState == LlmState.Down)
            DyrLog.Error(Tag, "The backend is up but THE MODEL IS DOWN: " + health.llm_error
                + " -> every scan will fail with HTTP 502 until the model answers.");
        else if (LlmProbeState == LlmState.Untested)
            DyrLog.Warn(Tag, "The model has not answered its startup probe yet (still loading?). "
                + "The first scans may time out.");
    }


    /// <summary>Send one frame and receive each label AS THE MODEL WRITES IT.</summary>
    public IEnumerator ScanStream(
        byte[] frameJpg,
        string source,
        string roomId,
        Action<ObjectLabelDto> onLabel,
        Action<ScanDoneDto> onDone,
        Action<string> onError)
    {
        if (frameJpg == null || frameJpg.Length == 0)
        {
            onError?.Invoke("Empty frame: the capture produced no bytes");
            yield break;
        }

        WWWForm form = new WWWForm();
        form.AddBinaryData("image", frameJpg, "frame.jpg", "image/jpeg");
        form.AddField("source", source ?? "");
        form.AddField("room_id", roomId ?? "");

        string url = Url("/scan/stream");
        if (verboseLogging)
            DyrLog.Info(Tag, "POST " + url + " (" + (frameJpg.Length / 1024f).ToString("F1")
                + " KB frame, streaming) ...");

        int labels = 0;
        string failure = null;
        float t0 = Time.realtimeSinceStartup;

        using (UnityWebRequest req = UnityWebRequest.Post(url, form))
        {
            req.downloadHandler = new DyrNdjsonHandler(line =>
            {
                ScanStreamLineDto parsed = ParseJson<ScanStreamLineDto>(line, "/scan/stream line");
                if (parsed == null) return;

                if (parsed.t == "label" && parsed.label != null
                    && !string.IsNullOrEmpty(parsed.label.entity_id))
                {
                    labels++;
                    if (verboseLogging)
                        DyrLog.Info(Tag, "label " + labels + " after "
                            + Mathf.Max(0f, Time.realtimeSinceStartup - t0).ToString("F1") + "s: '"
                            + parsed.label.name + "' (" + parsed.label.entity_id + ")");
                    onLabel?.Invoke(parsed.label);
                }
                else if (parsed.t == "error")
                {
                    failure = string.IsNullOrEmpty(parsed.error) ? "the model failed" : parsed.error;
                }
                else if (parsed.t == "done" && parsed.done != null)
                {
                    onDone?.Invoke(parsed.done);
                }
            });
            req.timeout = scanTimeoutSeconds;
            yield return req.SendWebRequest();

            // Clamp timestamps after Editor recompilation.
            float seconds = Mathf.Max(0f, Time.realtimeSinceStartup - t0);
            if (req.result != UnityWebRequest.Result.Success)
            {
                string error = DescribeError(req);
                DyrLog.Error(Tag, "/scan/stream FAILED after " + seconds.ToString("F1") + "s -> " + error);
                LogConnectionHints(req);
                onError?.Invoke(error);
                yield break;
            }
            if (failure != null)
            {
                DyrLog.Error(Tag, "/scan/stream: the backend reported " + failure);
                onError?.Invoke(failure);
                yield break;
            }
            if (verboseLogging)
                DyrLog.Info(Tag, "/scan/stream OK in " + seconds.ToString("F1") + "s -> "
                    + labels + " label(s)");
        }
    }

    /// <summary>Reads an NDJSON body one line at a time instead of waiting for it to end.</summary>
    class DyrNdjsonHandler : DownloadHandlerScript
    {
        readonly System.Text.Decoder _decoder = Encoding.UTF8.GetDecoder();
        readonly StringBuilder _line = new StringBuilder();
        readonly Action<string> _onLine;
        char[] _chars = new char[4096];

        public DyrNdjsonHandler(Action<string> onLine) : base(new byte[16 * 1024])
        {
            _onLine = onLine;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength == 0) return false;

            int needed = _decoder.GetCharCount(data, 0, dataLength, false);
            if (_chars.Length < needed) _chars = new char[needed];
            int count = _decoder.GetChars(data, 0, dataLength, _chars, 0, false);

            for (int i = 0; i < count; i++)
            {
                char c = _chars[i];
                if (c == '\n') Emit();
                else if (c != '\r') _line.Append(c);
            }
            return true;
        }

        protected override void CompleteContent() { Emit(); }

        void Emit()
        {
            if (_line.Length == 0) return;
            string line = _line.ToString();
            _line.Length = 0;
            try
            {
                _onLine(line);
            }
            catch (Exception ex)
            {
                // Prevent callbacks from terminating the network loop.
                DyrLog.Error(Tag, "Failed to handle a /scan/stream line (" + ex.Message
                    + "). Line was: " + line.Substring(0, Mathf.Min(200, line.Length)));
            }
        }
    }


    /// <summary>Everything the backend knows.</summary>
    public IEnumerator GetKnownObjects(string source, Action<ObjectListResponseDto> onOk, Action<string> onError)
    {
        // Scoped to this run's world, or a virtual session comes up wearing the real room's labels.
        string query = string.IsNullOrEmpty(source) ? "" : "?source=" + Uri.EscapeDataString(source);
        yield return Get("/objects" + query, onOk, onError);
    }

    /// <summary>Tell the backend where an object was anchored, so the label survives the session.</summary>
    public IEnumerator BindAnchor(
        string entityId, string anchorUuid, string anchorKind, string roomId,
        Action<string> onError)
    {
        AnchorBindRequestDto body = new AnchorBindRequestDto();
        body.anchor_uuid = anchorUuid;
        body.anchor_kind = anchorKind;
        body.room_id = roomId ?? "";

        string url = Url("/objects/" + entityId + "/anchor");
        using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
        {
            byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));
            req.uploadHandler = new UploadHandlerRaw(payload);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            // The scan timeout, not the default: this call runs DURING a scan and queues behind the stream.
            req.timeout = scanTimeoutSeconds;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                if (verboseLogging)
                    DyrLog.Info(Tag, "Anchor bound: object " + entityId + " -> " + anchorKind + " " + anchorUuid);
            }
            else
            {
                DyrLog.Warn(Tag, "Anchor bind FAILED for " + entityId + ": " + DescribeError(req)
                    + " (the label will still show now, but will NOT come back next session)");
                onError?.Invoke(DescribeError(req));
            }
        }
    }


    IEnumerator Get<T>(string path, Action<T> onOk, Action<string> onError) where T : class
    {
        string url = Url(path);
        if (verboseLogging) DyrLog.Info(Tag, "GET " + url + " ...");
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.timeout = defaultTimeoutSeconds;
            float t0 = Time.realtimeSinceStartup;
            yield return req.SendWebRequest();
            HandleResponse(req, path, Time.realtimeSinceStartup - t0, onOk, onError);
        }
    }

    IEnumerator Send<T>(string path, WWWForm form, int timeout,
        Action<T> onOk, Action<string> onError, string what) where T : class
    {
        string url = Url(path);
        if (verboseLogging) DyrLog.Info(Tag, "POST " + url + " (" + what + ") ...");
        using (UnityWebRequest req = UnityWebRequest.Post(url, form))
        {
            req.timeout = timeout;
            float t0 = Time.realtimeSinceStartup;
            yield return req.SendWebRequest();
            HandleResponse(req, path, Time.realtimeSinceStartup - t0, onOk, onError);
        }
    }

    void HandleResponse<T>(UnityWebRequest req, string path, float seconds,
        Action<T> onOk, Action<string> onError) where T : class
    {
        if (req.result != UnityWebRequest.Result.Success)
        {
            string error = DescribeError(req);
            DyrLog.Error(Tag, path + " FAILED after " + seconds.ToString("F1") + "s -> " + error);
            LogConnectionHints(req);
            onError?.Invoke(error);
            return;
        }

        T parsed = ParseJson<T>(req.downloadHandler.text, path);
        if (parsed == null)
        {
            onError?.Invoke("Malformed response from " + path);
            return;
        }
        if (verboseLogging)
            DyrLog.Info(Tag, path + " OK in " + seconds.ToString("F1") + "s");
        onOk?.Invoke(parsed);
    }

    static T ParseJson<T>(string json, string what) where T : class
    {
        try
        {
            return JsonUtility.FromJson<T>(json);
        }
        catch (Exception ex)
        {
            DyrLog.Error(Tag, "Could not parse the " + what + " response (" + ex.Message
                + "). Body was: " + (json ?? "").Substring(0, Mathf.Min(300, (json ?? "").Length)));
            return null;
        }
    }

    static string DescribeError(UnityWebRequest req)
    {
        return req.result + " | HTTP " + req.responseCode + " | " + req.error
            + (string.IsNullOrEmpty(req.downloadHandler?.text) ? "" : " | " + req.downloadHandler.text);
    }

    void LogConnectionHints(UnityWebRequest req)
    {
        if (req.result == UnityWebRequest.Result.ConnectionError)
        {
            DyrLog.Error(Tag, "HINT: the request never reached the backend. Check that the backend is "
                + "running, that " + backendBaseUrl + " is the right address from THIS device, "
                + "and that the firewall allows the port. On the headset use the PC's LAN IP.");
        }
        else if (req.responseCode == 502)
        {
            DyrLog.Error(Tag, "HINT: HTTP 502 means the backend is fine but the VISION MODEL did not "
                + "answer. Look at the backend console for the LLM error.");
        }
        else if (req.responseCode == 422)
        {
            DyrLog.Error(Tag, "HINT: HTTP 422 means the fields we send do not match the endpoint. "
                + "The backend log prints exactly which one.");
        }
    }
}
