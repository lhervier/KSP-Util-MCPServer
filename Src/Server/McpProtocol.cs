using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// The MCP methods, over JSON-RPC 2.0: <c>initialize</c>, <c>ping</c>, <c>tools/list</c>,
    /// <c>tools/call</c>, and the notification <c>notifications/cancelled</c>, which stops a running tool.
    /// Called from the HTTP threads, several at once.
    /// </summary>
    internal sealed class McpProtocol
    {
        public const string ServerName = "KSP-Util-MCPServer";
        private const string ServerVersion = "0.1.1";
        private const string DefaultProtocolVersion = "2025-06-18";

        private readonly Dictionary<string, Tool> _tools;

        public McpProtocol(IEnumerable<Tool> tools)
        {
            _tools = tools.ToDictionary(t => t.Name);
        }

        /// <summary>The number of tools offered.</summary>
        public int ToolCount
        {
            get { return _tools.Count; }
        }

        /// <summary>
        /// Answers one JSON-RPC message: returns the answer to write back, or null for a notification, which
        /// gets none. Sets <paramref name="status"/> to the HTTP status to answer with.
        /// </summary>
        public object Handle(string body, out int status)
        {
            object parsed;
            try
            {
                parsed = Json.Parse(body);
            }
            catch (FormatException e)
            {
                status = 400;
                return Error(null, -32700, "Parse error: " + e.Message);
            }

            Dictionary<string, object> message = parsed as Dictionary<string, object>;
            if (message == null)
            {
                status = 400;
                return Error(null, -32600, "Invalid request: batches are not supported");
            }
            string method = message.ContainsKey("method") ? message["method"] as string : null;
            Dictionary<string, object> parameters = message.ContainsKey("params")
                ? message["params"] as Dictionary<string, object>
                : null;

            // A notification has no id and gets no answer.
            if (!message.ContainsKey("id"))
            {
                if (method == "notifications/cancelled")
                {
                    Cancelled(parameters);
                }
                status = 202;
                return null;
            }

            object id = message["id"];
            status = 200;
            switch (method)
            {
                case "initialize":
                    return Result(id, Initialize(parameters));
                case "ping":
                    return Result(id, new Dictionary<string, object>());
                case "tools/list":
                    return Result(id, new Dictionary<string, object> { { "tools", ListTools() } });
                case "tools/call":
                    return CallTool(id, parameters);
                default:
                    return Error(id, -32601, "Method not found: " + method);
            }
        }

        private static Dictionary<string, object> Initialize(Dictionary<string, object> parameters)
        {
            string version = parameters != null && parameters.ContainsKey("protocolVersion")
                ? parameters["protocolVersion"] as string ?? DefaultProtocolVersion
                : DefaultProtocolVersion;
            return new Dictionary<string, object>
            {
                { "protocolVersion", version },
                { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                { "serverInfo", new Dictionary<string, object> { { "name", ServerName }, { "version", ServerVersion } } }
            };
        }

        private List<object> ListTools()
        {
            return _tools.Values.Select(t => (object)new Dictionary<string, object>
            {
                { "name", t.Name },
                { "description", t.Description },
                { "inputSchema", t.InputSchema }
            }).ToList();
        }

        private object CallTool(object id, Dictionary<string, object> parameters)
        {
            string name = parameters != null && parameters.ContainsKey("name") ? parameters["name"] as string : null;
            Tool tool;
            if (name == null || !_tools.TryGetValue(name, out tool))
            {
                return Error(id, -32602, "Unknown tool: " + name);
            }
            Dictionary<string, object> arguments = parameters.ContainsKey("arguments")
                ? parameters["arguments"] as Dictionary<string, object>
                : null;

            ToolCall call = new ToolCall(arguments);
            // A tool that acts waits for the scene being loaded: one that leaves it too early breaks the game.
            IEnumerator run = tool.Concurrent ? tool.Run(call) : SceneLoad.Then(call, () => tool.Run(call));
            Exception failure = MainThread.RunAndWait(name, id, tool.Concurrent,
                ToolMessage.Announced(name, call.Arguments, run));
            if (failure is MainThread.BusyException || failure is TimeoutException)
            {
                call.Fail(failure.Message);
            }
            else if (failure is MainThread.CancelledException)
            {
                call.Fail(name + " stopped: " + failure.Message);
            }
            else if (failure != null)
            {
                call.Fail(name + " threw: " + failure.Message);
                Log.Error(name + " threw: " + failure);
            }
            if (call.Content.Count == 0)
            {
                call.Text("");
            }
            return Result(id, new Dictionary<string, object>
            {
                { "content", call.Content },
                { "isError", call.IsError }
            });
        }

        // The client gave up a request: the tool it called, if it still runs, is stopped.
        private static void Cancelled(Dictionary<string, object> parameters)
        {
            if (parameters == null || !parameters.ContainsKey("requestId"))
            {
                return;
            }
            string requestId = Json.Write(parameters["requestId"]);
            string reason = parameters.ContainsKey("reason") ? parameters["reason"] as string : null;
            foreach (MainThread.Job job in MainThread.Cancel(j => Json.Write(j.RequestId) == requestId,
                         "cancelled by the client" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")")))
            {
                Log.Info(job.Name + " cancelled by the client");
            }
        }

        private static Dictionary<string, object> Result(object id, object result)
        {
            return new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } };
        }

        private static Dictionary<string, object> Error(object id, int code, string message)
        {
            return new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } }
            };
        }
    }
}
