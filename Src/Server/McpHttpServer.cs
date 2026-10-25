using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// The MCP endpoint: JSON-RPC 2.0 over HTTP POST (the "streamable HTTP" transport, answering every
    /// request with a plain JSON body), on the loopback interface only. Each request is read and answered on
    /// a thread of its own, so that a long tool does not hold the others up; tool calls are handed to
    /// <see cref="MainThread"/> and the thread waits for their result. The server checks on its own that it
    /// still answers, and says so through <see cref="Unresponsive"/>.
    /// </summary>
    internal sealed class McpHttpServer
    {
        /// <summary>How long reading a request, or writing its answer, may take before its connection is dropped.</summary>
        private const int TransferMilliseconds = 30000;

        /// <summary>How long <see cref="Stop"/> leaves the requests being answered before closing the port anyway.</summary>
        private const int StopGraceMilliseconds = 5000;

        /// <summary>How long the loop waits before taking requests again after failing to take one.</summary>
        private const int AcceptRetryMilliseconds = 1000;

        /// <summary>How often the server asks itself whether it answers, and how long it waits for the answer.</summary>
        private const int ProbePeriodMilliseconds = 15000;
        private const int ProbeTimeoutMilliseconds = 5000;

        /// <summary>How many checks in a row must go unanswered before the server is said not to answer.</summary>
        private const int ProbeFailuresAllowed = 2;

        private static readonly byte[] ProbeBody =
            Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\"self-probe\",\"method\":\"ping\"}");

        private readonly HttpListener _listener = new HttpListener();
        private readonly McpProtocol _protocol;
        private readonly int _port;
        private Thread _thread;
        private Thread _probe;
        private Timer _stopTimer;
        private volatile bool _running;
        private volatile bool _unresponsive;
        // The requests being answered, and whether the listener was closed (1) or not (0).
        private int _inFlight;
        private int _closed;

        public McpHttpServer(int port, McpProtocol protocol)
        {
            _port = port;
            _protocol = protocol;
        }

        private string Url
        {
            get { return "http://127.0.0.1:" + _port + "/mcp/"; }
        }

        /// <summary>
        /// Whether the server stopped answering its own checks while listening: it then has to be stopped and
        /// started again. Read from any thread.
        /// </summary>
        public bool Unresponsive
        {
            get { return _unresponsive; }
        }

        /// <summary>Starts listening on http://127.0.0.1:port/mcp/. Throws when the port cannot be bound.</summary>
        public void Start()
        {
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = McpProtocol.ServerName };
            _thread.Start();
            _probe = new Thread(Probe) { IsBackground = true, Name = McpProtocol.ServerName + " probe" };
            _probe.Start();
            Log.Info("Listening on " + Url + " with " + _protocol.ToolCount + " tools");
        }

        /// <summary>
        /// Stops serving: the requests coming in from now on are refused, those already taken still get their
        /// answer, and the port closes once they have it, or after a few seconds whatever they do. With
        /// <paramref name="now"/>, the port closes at once, the requests being answered dropped.
        /// </summary>
        public void Stop(bool now = false)
        {
            _running = false;
            // Mono's listener drops the answers still being written when it stops: the last request to end
            // closes it, unless none is running now.
            Thread.MemoryBarrier();
            if (now || Interlocked.CompareExchange(ref _inFlight, 0, 0) == 0)
            {
                CloseListener();
                return;
            }
            // A request stuck on its connection would keep the port for ever, and the server could not be turned
            // on again.
            _stopTimer = new Timer(_ => CloseListener(), null, StopGraceMilliseconds, Timeout.Infinite);
        }

        // Closes the port, once.
        private void CloseListener()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
            {
                return;
            }
            _stopTimer?.Dispose();
            try
            {
                _listener.Close();
            }
            catch (Exception)
            {
                // Closing a listener that is already gone is not worth reporting.
            }
        }

        private void Loop()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception e)
                {
                    // Closing the listener makes GetContext throw: that is how the loop ends. Anything else is
                    // reported and the loop goes on: leaving, it would keep the port open with nobody to answer.
                    if (!_running || Interlocked.CompareExchange(ref _closed, 0, 0) == 1)
                    {
                        return;
                    }
                    Log.Error("Taking a request failed, still listening: " + e);
                    Thread.Sleep(AcceptRetryMilliseconds);
                    continue;
                }
                new Thread(() => Serve(context)) { IsBackground = true, Name = McpProtocol.ServerName + " request" }.Start();
            }
        }

        // Asks the server, every few seconds, whether it answers; marks it unresponsive when it no longer does.
        private void Probe()
        {
            int failures = 0;
            while (true)
            {
                Thread.Sleep(ProbePeriodMilliseconds);
                if (!_running)
                {
                    return;
                }
                if (Answers())
                {
                    failures = 0;
                    continue;
                }
                failures++;
                Log.Info("The server did not answer its own check (" + failures + " in a row)");
                if (failures >= ProbeFailuresAllowed && _running)
                {
                    _unresponsive = true;
                    return;
                }
            }
        }

        // Whether a ping posted to the server gets its answer in time.
        private bool Answers()
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Proxy = null;
                request.KeepAlive = false;
                request.Timeout = ProbeTimeoutMilliseconds;
                request.ReadWriteTimeout = ProbeTimeoutMilliseconds;
                request.ContentLength = ProbeBody.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(ProbeBody, 0, ProbeBody.Length);
                }
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Answers one request, counted among those being answered.
        private void Serve(HttpListenerContext context)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                Answer(context);
            }
            finally
            {
                if (Interlocked.Decrement(ref _inFlight) == 0 && !_running)
                {
                    CloseListener();
                }
            }
        }

        // Answers one request, a server error when anything goes wrong.
        private void Answer(HttpListenerContext context)
        {
            try
            {
                Handle(context);
            }
            catch (IOException e)
            {
                // The client hung up before its answer was read or written: nothing went wrong on this side.
                Log.Info("Client gone before its answer: " + e.Message.Replace("\r", "").Replace("\n", " "));
                Close(context, 500);
            }
            catch (Exception e)
            {
                Log.Error("Request failed: " + e);
                Close(context, 500);
            }
        }

        // Ends the answer with this status, if the client is still there to get it.
        private static void Close(HttpListenerContext context, int status)
        {
            try
            {
                context.Response.StatusCode = status;
                context.Response.Close();
            }
            catch (Exception)
            {
                // The client may already be gone.
            }
        }

        // Drops the connection of a request if it is not disposed of in time: a read or a write stuck on a client
        // that sends or reads nothing would otherwise hold its thread, and the port with it, for ever.
        private static Timer Deadline(HttpListenerContext context)
        {
            return new Timer(_ =>
            {
                Log.Info("A request took over " + TransferMilliseconds / 1000 + " s to read or answer: connection dropped");
                try
                {
                    context.Response.Abort();
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }, null, TransferMilliseconds, Timeout.Infinite);
        }

        private void Handle(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            if (request.HttpMethod != "POST")
            {
                // No server-initiated stream: GET is not offered, as the transport allows.
                response.StatusCode = 405;
                response.Close();
                return;
            }

            string body;
            using (Deadline(context))
            using (StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }
            if (!_running)
            {
                // Turned off while the request came in: it must not reach the game any more.
                response.StatusCode = 503;
                response.Close();
                return;
            }
            int status;
            object answer = _protocol.Handle(body, out status);
            if (answer == null)
            {
                response.StatusCode = status;
                response.Close();
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(answer));
            using (Deadline(context))
            {
                response.StatusCode = status;
                response.ContentType = "application/json";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.Close();
            }
        }
    }
}
