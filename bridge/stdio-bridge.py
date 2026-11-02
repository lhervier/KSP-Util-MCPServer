"""Lets an MCP client that talks over stdio, such as Claude Code, keep using KSP-Util-MCPServer while KSP stops and
starts again.

    python stdio-bridge.py [--url <url>] [--ksp <name>=<KSP folder>]... [--cache <file>]

The client starts this script and talks to it on its standard input and output; the script passes each message
on to the server of KSP-Util-MCPServer, over HTTP, and gives back its answer. It lives as long as the client does:

- while KSP is not running, the tools of the game stay listed (the last list seen, kept in a file), and calling
  one answers that KSP is not running, instead of the connection going down;
- every few seconds it checks the server, and when the list of tools changes (KSP started, or started with
  other mods), it tells the client, which reads the list again;
- it adds tools of its own, which work while KSP is not running: ksp_status, and, for the KSP folders given
  with --ksp (and no other), ksp_start and ksp_quit. Without --ksp, the folder of the environment variable
  KSPDIR, when it is set, is the one KSP folder, named KSPDIR.

It is meant for exploring the game by hand with an AI assistant. A script that plays a protocol posts to the
server over HTTP itself, and must not need this bridge or its tools.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

import kspwindow

DEFAULT_URL = "http://127.0.0.1:8770/mcp/"
PROBE_PERIOD = 5
PROBE_TIMEOUT = 5
EXECUTABLES = ["KSP_x64.exe", "KSP.x86_64", os.path.join("KSP.app", "Contents", "MacOS", "KSP")]


class Unreachable(Exception):
    """The server of KSP-Util-MCPServer does not answer: KSP is not running, or not far enough into its start."""


def log(text):
    """Writes one line to the standard error, which the client keeps as the log of this server."""
    sys.stderr.write("[ksp-mcp-bridge] " + text + "\n")
    sys.stderr.flush()


class KspServer:
    """The server of KSP-Util-MCPServer, reached over HTTP."""

    def __init__(self, url):
        self.url = url

    def post(self, message, timeout=None):
        """Posts one JSON-RPC message and returns the answer, or None for a notification. Raises Unreachable
        when the server cannot be reached, or stops answering midway; timeout None waits as long as it takes."""
        request = urllib.request.Request(self.url, json.dumps(message).encode("utf-8"),
                                         {"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                body = response.read()
        except urllib.error.HTTPError as e:
            # The server answers a malformed message with an HTTP error, and a JSON-RPC error in the body.
            body = e.read()
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError) as e:
            raise Unreachable(str(getattr(e, "reason", e)))
        return json.loads(body) if body.strip() else None

    def tools(self, timeout=PROBE_TIMEOUT):
        """Returns the tools the server offers, as tools/list gives them. Raises Unreachable."""
        answer = self.post({"jsonrpc": "2.0", "id": "bridge-probe", "method": "tools/list"}, timeout)
        return answer["result"]["tools"]

    def call(self, tool, arguments=None, timeout=None):
        """Calls one tool and returns its text, decoded from JSON when it is JSON. Raises Unreachable, or
        RuntimeError when the tool fails."""
        answer = self.post({"jsonrpc": "2.0", "id": "bridge-call", "method": "tools/call",
                            "params": {"name": tool, "arguments": arguments or {}}}, timeout)
        if "error" in answer:
            raise RuntimeError(tool + ": " + answer["error"]["message"])
        result = answer["result"]
        text = result["content"][0].get("text", "") if result["content"] else ""
        if result.get("isError"):
            raise RuntimeError(tool + ": " + text)
        try:
            return json.loads(text)
        except ValueError:
            return text


def ksp_running():
    """Tells whether a KSP process runs on this computer, from whatever folder."""
    if os.name == "nt":
        # Bytes, not text: a localized tasklist writes in the OEM code page, which text mode may fail to decode.
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KSP_x64.exe"], capture_output=True,
                             creationflags=subprocess.CREATE_NO_WINDOW).stdout
        return b"KSP_x64.exe" in out
    return subprocess.run(["pgrep", "-f", "KSP.x86_64|KSP.app/Contents/MacOS/KSP"],
                          capture_output=True).returncode == 0


def launch(folder, arguments):
    """Starts KSP from folder, with the given command-line arguments, so that it keeps running after this
    script ends, and returns its process. Raises RuntimeError when the folder holds no KSP executable."""
    for name in EXECUTABLES:
        executable = os.path.join(folder, name)
        if os.path.isfile(executable):
            break
    else:
        raise RuntimeError("no KSP executable in " + folder)
    options = {"cwd": folder, "stdin": subprocess.DEVNULL, "stdout": subprocess.DEVNULL,
               "stderr": subprocess.DEVNULL, "close_fds": True}
    if os.name != "nt":
        return subprocess.Popen([executable] + arguments, start_new_session=True, **options)
    # Out of the client's process group, and out of its job when the job lets it, so that closing the client
    # does not close KSP with it.
    flags = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
    try:
        return subprocess.Popen([executable] + arguments,
                                creationflags=flags | subprocess.CREATE_BREAKAWAY_FROM_JOB, **options)
    except OSError:
        return subprocess.Popen([executable] + arguments, creationflags=flags, **options)


def canonical(tools):
    """The tools of a list, in a form that compares equal whatever their order."""
    return sorted(json.dumps(t, sort_keys=True) for t in tools)


def schema(properties=None, required=None):
    """The JSON schema of an object with the given properties."""
    result = {"type": "object", "properties": properties or {}}
    if required:
        result["required"] = required
    return result


class Bridge:
    """Answers the client on the standard input and output, passing on to the server what is not its own."""

    def __init__(self, server, installs, cache):
        self.server = server
        self.installs = installs
        self.cache = cache
        self.started = None
        self.output = threading.Lock()
        self.state = threading.Lock()
        self.initialized = False
        self.game_tools = self.read_cache()
        self.served = None
        self.up = None
        self.own = self.own_tools()

    # ---- the client side

    def send(self, message):
        """Writes one JSON-RPC message to the client."""
        data = json.dumps(message, ensure_ascii=False).encode("utf-8") + b"\n"
        with self.output:
            sys.stdout.buffer.write(data)
            sys.stdout.buffer.flush()

    def run(self):
        """Answers the client until it closes the standard input, each request on a thread of its own, so that a
        long tool does not hold up the others, nor their cancellation."""
        threading.Thread(target=self.watch, daemon=True).start()
        for line in sys.stdin.buffer:
            if not line.strip():
                continue
            try:
                message = json.loads(line)
            except ValueError as e:
                self.send({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error: %s" % e}})
                continue
            threading.Thread(target=self.handle, args=(message,), daemon=True).start()

    def handle(self, message):
        """Answers one message from the client."""
        method = message.get("method")
        if "id" not in message:
            # A notification: the server only acts on notifications/cancelled, which it matches by the id of
            # the request, passed on unchanged.
            if method == "notifications/initialized":
                with self.state:
                    self.initialized = True
            elif method is not None:
                try:
                    self.server.post(message, PROBE_TIMEOUT)
                except Unreachable:
                    pass
            return
        if method is None:
            return  # an answer to a request: this bridge sends none
        id = message["id"]
        params = message.get("params") or {}
        try:
            if method == "initialize":
                result = {"protocolVersion": params.get("protocolVersion", "2025-06-18"),
                          "capabilities": {"tools": {"listChanged": True}},
                          "serverInfo": {"name": "KSP-Util-MCPServer stdio bridge", "version": "0.1.0"}}
            elif method == "ping":
                result = {}
            elif method == "tools/list":
                result = {"tools": self.list_tools()}
            elif method == "tools/call" and params.get("name") in self.own:
                result = self.call_own(params["name"], params.get("arguments") or {})
            else:
                self.send(self.forward(message))
                return
            self.send({"jsonrpc": "2.0", "id": id, "result": result})
        except Exception as e:
            log("%s failed: %r" % (method, e))
            self.send({"jsonrpc": "2.0", "id": id, "error": {"code": -32603, "message": str(e)}})

    def forward(self, message):
        """Passes one request on to the server and returns its answer, or what to answer when it is not there."""
        try:
            return self.server.post(message)
        except Unreachable as e:
            text = ("KSP is not running, or its server does not answer yet (%s). ksp_status tells which; "
                    "ksp_start starts KSP." % e)
            if message.get("method") == "tools/call":
                if ksp_running():
                    text = "The server of KSP-Util-MCPServer does not answer (%s): KSP is running, but may be " \
                           "still starting, stopping, or stuck." % e
                return {"jsonrpc": "2.0", "id": message["id"],
                        "result": {"content": [{"type": "text", "text": text}], "isError": True}}
            return {"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32603, "message": text}}

    # ---- the tools listed

    def list_tools(self):
        """Returns the tools to list: the game's, read now when the server answers, the last ones read when it
        does not, and this bridge's own."""
        # A refused connection takes a couple of seconds to fail on Windows: not asked again when the last check
        # found the server down.
        if self.up is not False:
            try:
                self.game_seen(self.server.tools())
            except Unreachable:
                pass
        with self.state:
            tools = [t for t in self.game_tools if t["name"] not in self.own] + \
                    [{"name": n, "description": d, "inputSchema": s} for n, (d, s, _) in self.own.items()]
            self.served = canonical(self.game_tools)
        return tools

    def game_seen(self, tools):
        """Keeps the tools the server just listed, and tells the client when they differ from those it got."""
        with self.state:
            if tools != self.game_tools:
                self.game_tools = tools
                self.write_cache()
            # Told once per change: the client may not read the list again at once, or at all.
            changed = self.initialized and self.served is not None and canonical(tools) != self.served
            if changed:
                self.served = canonical(tools)
        if changed:
            log("the tools of the game changed: telling the client")
            self.send({"jsonrpc": "2.0", "method": "notifications/tools/list_changed"})

    def watch(self):
        """Reads the list of tools of the server every few seconds, for as long as this script runs."""
        while True:
            try:
                self.game_seen(self.server.tools())
                self.up = True
            except Unreachable:
                self.up = False
            except Exception as e:
                log("checking the server failed: %r" % e)
            time.sleep(PROBE_PERIOD)

    def read_cache(self):
        """Returns the tools of the game kept in the cache file, or none when there is no such file."""
        try:
            with open(self.cache, encoding="utf-8") as f:
                return json.load(f)
        except (OSError, ValueError):
            return []

    def write_cache(self):
        """Keeps the tools of the game in the cache file."""
        try:
            os.makedirs(os.path.dirname(self.cache), exist_ok=True)
            with open(self.cache, "w", encoding="utf-8", newline="") as f:
                json.dump(self.game_tools, f)
        except OSError as e:
            log("could not write %s: %s" % (self.cache, e))

    # ---- this bridge's own tools

    def own_tools(self):
        """Returns this bridge's tools, by name: their description, the schema of their arguments, and what
        runs them."""
        tools = {"ksp_status": (
            "Tells whether KSP is running, whether its server answers, the folder of the KSP that answers and "
            "which of this bridge's installs it is (none for another KSP), and in which scene the game is; also "
            "the KSP folders ksp_start can start. Works while KSP is not running.",
            schema(), self.status)}
        if not self.installs:
            return tools
        names = sorted(self.installs)
        tools["ksp_start"] = (
            "Starts KSP from one of the folders this bridge was given, and by default waits until the game is at "
            "its main menu. Refused while a KSP runs. KSP.log is overwritten when KSP starts: keep_log copies the "
            "previous one first. window_x moves the window of KSP (Windows only, not in full screen) as soon as it "
            "shows.",
            schema({"install": {"type": "string", "enum": names, "description": "which KSP folder"},
                    "arguments": {"type": "array", "items": {"type": "string"},
                                  "description": "command-line arguments for KSP, such as -popupwindow"},
                    "keep_log": {"type": "string", "description": "a file to copy the previous KSP.log to"},
                    "wait": {"type": "boolean", "description": "wait for the main menu (default true)"},
                    "timeout": {"type": "number", "description": "seconds to wait for the main menu (default 540)"},
                    "window_x": {"type": "integer",
                                 "description": "pixels between the left edge of the work area of the primary "
                                                "screen and the left edge of the window of KSP, 0 to stick it to "
                                                "the left; its height on the screen and its size do not change "
                                                "(default: where Windows puts it)"}},
                   ["install"]),
            self.start)
        tools["ksp_quit"] = (
            "Quits KSP and waits until its process has ended; log copies its KSP.log, which the next start "
            "overwrites. Quitting from a flight saves the active vessel in the game's persistent save. Refused "
            "for a KSP that does not run from one of the folders this bridge was given.",
            schema({"install": {"type": "string", "enum": names,
                                "description": "the KSP folder whose KSP.log to copy (default: the one ksp_start "
                                               "started)"},
                    "log": {"type": "string", "description": "a file to copy KSP.log to, once KSP has ended"},
                    "timeout": {"type": "number", "description": "seconds to wait for KSP to end (default 120)"}}),
            self.quit)
        return tools

    def call_own(self, name, arguments):
        """Runs one of this bridge's tools and returns its result, in the shape of the result of tools/call."""
        try:
            text = self.own[name][2](arguments)
            error = False
        except Exception as e:
            text = "%s: %s" % (name, e)
            error = True
        if not isinstance(text, str):
            text = json.dumps(text, indent=1, ensure_ascii=False)
        return {"content": [{"type": "text", "text": text}], "isError": error}

    def status(self, arguments):
        state = {"ksp_running": ksp_running(), "server": "down", "scene": None, "ksp_folder": None,
                 "install": None, "installs": self.installs, "started_by_bridge": self.started}
        try:
            self.server.tools()
            state["server"] = "up"
            game = self.server.call("get_state", timeout=PROBE_TIMEOUT)
            state["scene"] = game.get("scene")
            state["ksp_folder"] = game.get("kspFolder")
            state["install"] = self.install_of(state["ksp_folder"])
        except Unreachable:
            pass
        except Exception as e:
            state["scene"] = "unknown (%s)" % e
        return state

    def install_of(self, folder):
        """Returns the name of the install of this bridge that folder is, or None when it is none of them."""
        if not folder:
            return None
        for name, path in self.installs.items():
            if same_folder(path, folder):
                return name
        return None

    def folder(self, arguments, name):
        install = arguments.get(name)
        if install not in self.installs:
            raise RuntimeError("unknown install %r: one of %s" % (install, ", ".join(sorted(self.installs))))
        return install, self.installs[install]

    def start(self, arguments):
        install, folder = self.folder(arguments, "install")
        if ksp_running():
            raise RuntimeError("KSP is already running")
        previous = os.path.join(folder, "KSP.log")
        if arguments.get("keep_log") and os.path.isfile(previous):
            copy(previous, arguments["keep_log"])
        process = launch(folder, list(arguments.get("arguments") or []))
        self.started = install
        placed = []
        if arguments.get("window_x") is not None:
            def report(line):
                log(line)
                placed.append(line)
            kspwindow.place_window_later(process.pid, int(arguments["window_x"]), report)
        if arguments.get("wait") is False:
            return "KSP started from " + folder
        began = time.time()
        limit = float(arguments.get("timeout", 540))
        while time.time() - began < limit:
            time.sleep(PROBE_PERIOD)
            if not ksp_running():
                raise RuntimeError("KSP ended %.0f s after starting" % (time.time() - began))
            try:
                if self.server.call("get_state", timeout=PROBE_TIMEOUT).get("scene") == "MAINMENU":
                    # Read the tools now, rather than at the next check, so that the client has them on return.
                    self.game_seen(self.server.tools())
                    # The window shows long before the main menu: its line is there by now.
                    text = "KSP started from %s; main menu after %.0f s" % (folder, time.time() - began)
                    return text + "".join("; " + line for line in placed)
            except (Unreachable, RuntimeError):
                pass
        raise RuntimeError("no main menu after %.0f s; KSP is still running" % limit)

    def quit(self, arguments):
        install, folder = None, None
        if arguments.get("install") is not None or arguments.get("log"):
            if arguments.get("install") is None and self.started is None:
                raise RuntimeError("install is needed to copy KSP.log, as this bridge did not start KSP")
            install, folder = self.folder(arguments if arguments.get("install") else {"install": self.started},
                                          "install")
        if ksp_running():
            # Only a KSP of the folders this bridge was given, as for ksp_start: the player's own game is theirs
            # to quit.
            try:
                running = self.server.call("get_state", timeout=PROBE_TIMEOUT).get("kspFolder")
            except (Unreachable, RuntimeError) as e:
                raise RuntimeError("could not ask KSP which folder it runs from: %s" % e)
            if self.install_of(running) is None:
                raise RuntimeError("the KSP that runs is not one of this bridge's folders (%s): not quitting it"
                                   % (running or "an older server, which does not tell its folder"))
            try:
                self.server.call("quit_game", timeout=30)
            except (Unreachable, RuntimeError) as e:
                raise RuntimeError("could not ask KSP to quit: %s" % e)
            began = time.time()
            limit = float(arguments.get("timeout", 120))
            while ksp_running():
                if time.time() - began > limit:
                    raise RuntimeError("KSP still running after %.0f s" % limit)
                time.sleep(1)
            text = "KSP ended after %.0f s" % (time.time() - began)
        else:
            text = "KSP was not running"
        if arguments.get("log"):
            copy(os.path.join(folder, "KSP.log"), arguments["log"])
            text += "; KSP.log copied to " + os.path.abspath(arguments["log"])
        return text


def same_folder(a, b):
    """Tells whether two paths name the same folder, whatever their case or trailing separator."""
    return os.path.normcase(os.path.normpath(os.path.abspath(a))) == \
        os.path.normcase(os.path.normpath(os.path.abspath(b)))


def copy(source, destination):
    """Copies a file, making the folders of its destination when needed."""
    folder = os.path.dirname(os.path.abspath(destination))
    os.makedirs(folder, exist_ok=True)
    shutil.copy2(source, destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", default=DEFAULT_URL, help="the address of the server (default %(default)s)")
    parser.add_argument("--ksp", action="append", default=[], metavar="NAME=FOLDER",
                        help="a KSP folder ksp_start may start, under a name of your choice; repeat for several "
                             "(default: the folder of the environment variable KSPDIR, named KSPDIR)")
    parser.add_argument("--cache", help="the file that keeps the last tools listed by the server")
    options = parser.parse_args()

    installs = {}
    for item in options.ksp:
        name, sep, folder = item.partition("=")
        if not sep or not name or not folder:
            parser.error("--ksp wants NAME=FOLDER, got " + item)
        installs[name] = os.path.abspath(folder)
    if not installs and os.environ.get("KSPDIR"):
        installs["KSPDIR"] = os.path.abspath(os.environ["KSPDIR"])
    cache = options.cache
    if cache is None:
        base = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), ".cache")
        port = urllib.parse.urlparse(options.url).port or 80
        cache = os.path.join(base, "KSP-Util-MCPServer", "bridge-tools-%d.json" % port)

    log("passing on to %s; KSP folders: %s" % (options.url, installs or "none"))
    Bridge(KspServer(options.url), installs, cache).run()
    # The standard input closed: the client is gone. Threads still waiting on the server go with this process.
    os._exit(0)


if __name__ == "__main__":
    main()
