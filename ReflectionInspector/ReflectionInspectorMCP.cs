#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private static JObject Prop(string type) => new JObject { ["type"] = type };

        private static JObject Prop(string type, string description) =>
            new JObject { ["type"] = type, ["description"] = description };

        private static string BuildToolsList(string idRaw)
        {
            return BuildResultJson(idRaw, new JObject
            {
                ["tools"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = LIST_ASSEMBLIES_TOOL_NAME,
                        ["description"] = "Lists the names of all assemblies currently loaded in the Unity editor.",
                        ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = new JObject() }
                    },
                    new JObject
                    {
                        ["name"] = SEARCH_TYPES_TOOL_NAME,
                        ["description"] = "Searches C# types in the loaded assemblies by regex. Returns a numbered, paged list of matches (kind, full name, assembly, base type). Use inspect_type on a match for details.",
                        ["inputSchema"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["query"] = Prop("string", "Regex matched case-insensitively against the type's simple name, or against the full name when it contains '.'."),
                                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("Class", "Struct", "Enum", "Interface"), ["description"] = "Optional: only this kind of type." },
                                ["assembly"] = Prop("string", "Optional: case-insensitive substring of the assembly name."),
                                ["derivedFrom"] = Prop("string", "Optional: full name of a base type; only types assignable to it are returned."),
                                ["includePrivate"] = new JObject { ["type"] = "boolean", ["description"] = "Optional, default false: also include non-public types." },
                                ["range"] = Prop("string", $"Optional, default '1-{ReflectionTools.DefaultPageSize}': which numbered matches to return, e.g. '31-42'.")
                            },
                            ["required"] = new JArray("query")
                        }
                    },
                    new JObject
                    {
                        ["name"] = INSPECT_TYPE_TOOL_NAME,
                        ["description"] = "Shows detailed information about a C# type: header (kind, assembly, base, inheritance chain, interfaces, attributes) plus numbered, paged sections of fields, properties, methods and constructors. Use ranges to fetch more items.",
                        ["inputSchema"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["typeName"] = Prop("string", "Type to inspect. Fuzzy match: exact full name, simple name, full-name suffix, or substring."),
                                ["includePrivate"] = new JObject { ["type"] = "boolean", ["description"] = "Optional, default false: include non-public members." },
                                ["includeInherited"] = new JObject { ["type"] = "boolean", ["description"] = "Optional, default false: also include members inherited from base types." },
                                ["members"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray("fields", "properties", "methods", "constructors") }, ["description"] = "Optional: which sections to include. Default: all." },
                                ["memberFilter"] = Prop("string", "Optional: case-insensitive substring to filter member names by (before paging)."),
                                ["range"] = Prop("string", $"Optional, default '1-{ReflectionTools.DefaultPageSize}': which numbered items each section should return, e.g. '31-42'.")
                            },
                            ["required"] = new JArray("typeName")
                        }
                    }
                }
            });
        }

        private const string LIST_ASSEMBLIES_TOOL_NAME = "list_assemblies";
        private const string SEARCH_TYPES_TOOL_NAME = "search_types";
        private const string INSPECT_TYPE_TOOL_NAME = "inspect_type";

        private static string BuildToolCallResponse(string idRaw, string name, JObject args)
        {
            var a = (JObject)args ?? new JObject();

            string text;
            try
            {
                switch (name)
                {
                case LIST_ASSEMBLIES_TOOL_NAME:
                    text = ReflectionTools.ListAssemblies();
                    break;

                case SEARCH_TYPES_TOOL_NAME:
                    text = ReflectionTools.SearchTypes(
                        a["query"]?.Value<string>(),
                        a["kind"]?.Value<string>(),
                        a["assembly"]?.Value<string>(),
                        a["derivedFrom"]?.Value<string>(),
                        a["includePrivate"]?.Value<bool>() ?? false,
                        a["range"]?.Value<string>());
                    break;

                case INSPECT_TYPE_TOOL_NAME:
                    text = ReflectionTools.InspectType(
                        a["typeName"]?.Value<string>(),
                        a["includePrivate"]?.Value<bool>() ?? false,
                        a["includeInherited"]?.Value<bool>() ?? false,
                        a["members"] is JArray mar ? mar.Select(x => x.Value<string>()).ToList() : null,
                        a["memberFilter"]?.Value<string>(),
                        a["range"]?.Value<string>());
                    break;

                default:
                    return BuildErrorJson(idRaw, -32602, $"Unknown tool: {name}");
                }
            }
            catch (Exception e)
            {
                return BuildResultJson(idRaw, new JObject
                {
                    ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = e.Message } },
                    ["isError"] = true
                });
            }

            return BuildResultJson(idRaw, new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
                ["isError"] = false
            });
        }
    }
}
#endif