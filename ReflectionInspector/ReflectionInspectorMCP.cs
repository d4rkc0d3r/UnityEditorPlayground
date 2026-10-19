#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace d4rkpl4y3r.ReflectionInspector
{
    public static class ReflectionInspectorMCP
    {
        static HttpListener _listener;
        static CancellationTokenSource _cts;
        static int _port = 8251;
        
        [InitializeOnLoad]
        private class OnLoadHook
        {
            static OnLoadHook() => Start();
        }

        public static void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            Application.quitting += Stop;
            _ = RunLoop(_cts.Token);
        }

        public static void Stop()
        {
            Application.quitting -= Stop;
            _cts?.Cancel();
            try { _listener?.Close(); } catch { }
        }

        private static async Task RunLoop(CancellationToken ct)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            try
            {
                _listener.Start();
                Debug.Log($"[ReflectionInspectorMCP] Listening on http://localhost:{_port}/");

                while (!ct.IsCancellationRequested)
                {
                    // Plain await (no ConfigureAwait(false)) → resumes on Unity's
                    // main thread, and the editor is NOT blocked while awaiting.
                    var ctx = await _listener.GetContextAsync();
                    await HandleJsonRpcAsync(ctx);
                }
            }
            catch (HttpListenerException e)
            {
                if (ct.IsCancellationRequested) return; /* shutdown: listener closed on purpose */
                Debug.LogError($"[ReflectionInspectorMCP] Failed to start listener on port {_port}: {e.Message}");
            }
            finally { _listener.Close(); }
        }

        private static async Task HandleJsonRpcAsync(HttpListenerContext ctx)
        {
            string idRaw = null;
            string method = null;
            JObject request = null;

            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                var body = await reader.ReadToEndAsync();
                try
                {
                    request = JObject.Parse(body);
                    method = request["method"]?.Value<string>();
                    idRaw = request["id"]?.ToString();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ReflectionInspectorMCP] Failed to parse request body: {e.Message}");
                    SendHttp(ctx, 400, BuildErrorJson(null, -32700, "Parse error"));
                    return;
                }
            }

            if (method == null)
            {
                SendHttp(ctx, 400, BuildErrorJson(idRaw, -32600, "Invalid Request: missing method"));
                return;
            }

            // Notifications have no "id" and expect no response (202 Accepted).
            var isNotification = request["id"] == null;

            string response = null;
            switch (method)
            {
                case "initialize":
                    response = BuildInitializeResponse(idRaw);
                    break;
                case "ping":
                    response = BuildResultJson(idRaw, new JObject());
                    break;
                case "tools/list":
                    response = BuildToolsList(idRaw);
                    break;
                case "tools/call":
                    var paramsNode = request["params"] as JObject;
                    response = BuildToolCallResponse(idRaw, paramsNode?["name"]?.Value<string>(), paramsNode?["arguments"] as JObject);
                    break;
                case "notifications/initialized":
                case "notifications/cancelled":
                case "notifications/progress":
                    break;
                default:
                    response = BuildErrorJson(idRaw, -32601, $"Method not supported: {method}");
                    break;
            }

            if (isNotification || response == null)
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            SendHttp(ctx, 200, response);
        }

        private static void SendHttp(HttpListenerContext ctx, int statusCode, string jsonBody)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        // --- JSON-RPC 2.0 envelope helpers (all JSON via Newtonsoft.Json) ---

        private static string BuildResultJson(string idRaw, object result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = ToJToken(idRaw),
                ["result"] = result as JObject ?? JToken.FromObject(result)
            }.ToString(Formatting.None);
        }

        private static string BuildErrorJson(string idRaw, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = ToJToken(idRaw),
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            }.ToString(Formatting.None);
        }

        /// <summary>
        /// Restores the request id to its original JSON type (null, string, number, or
        /// any other token) so it is echoed back verbatim in the response.
        /// </summary>
        private static JToken ToJToken(string idRaw)
        {
            if (idRaw == null) return null;
            if (long.TryParse(idRaw, out _)) return JValue.Parse(idRaw); // numeric id
            if (idRaw == "true" || idRaw == "false") return JValue.Parse(idRaw); // bool id (rare)
            return new JValue(idRaw); // string id
        }

        // --- MCP methods ---

        private static string BuildInitializeResponse(string idRaw)
        {
            return BuildResultJson(idRaw, new JObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject
                    {
                        ["listChanged"] = false
                    }
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = "reflection-inspector",
                    ["version"] = "0.0.1"
                }
            });
        }

        private static string BuildToolsList(string idRaw)
        {
            return BuildResultJson(idRaw, new JObject
            {
                ["tools"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = TEST_TOOL_NAME,
                        ["description"] = "Smoke test tool. Returns a basic OK response to verify the MCP server is reachable.",
                        ["inputSchema"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject(),
                        }
                    }
                }
            });
        }

        private const string TEST_TOOL_NAME = "test";

        private static string BuildToolCallResponse(string idRaw, string name, JObject args)
        {
            if (name != TEST_TOOL_NAME)
            {
                // Unknown tool → invalid params error
                return BuildErrorJson(idRaw, -32602, $"Unknown tool: {name}");
            }

            var message = "OK — ReflectionInspector MCP server is running. (" + DateTime.Now.ToString("u") + ")";
            return BuildResultJson(idRaw, new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = message
                    }
                },
                ["isError"] = false
            });
        }
    }
}
#endif