r"""Engine server for front-ends (the Windows TSF text service).

Two transports, same JSON protocol:

  python -m myinput.server            one request per line on stdin, reply per line on stdout
  python -m myinput.server --pipe     Windows named pipe \\.\pipe\MyInput, message mode:
                                      each message is one JSON request / one JSON reply.
                                      One client connection per application, each with
                                      its own session, so every app keeps its own 中/EN mode.
  python -m myinput.server --socket   the same over a Unix domain socket (macOS / Linux),
                                      framed as 4-byte big-endian length + JSON bytes.

Requests:
  {"key": "n"}                      -> session output (see session.Output)
  {"key": "space"}                  named keys: space enter tab backspace escape ...
  {"key": "h", "before": "好"}      "before": the text right before the cursor (up to
                                       two characters), when the front-end can read it
                                       and nothing is being composed; decides the space
                                       between Chinese and English (auto_space). Left
                                       out, the engine goes by what it committed last.
  {"cmd": "mode", "value": "en"}    -> switch mode ("zh" | "en" | "toggle"); add
                                       "temporary": true for a passing switch (Caps Lock)
                                       that must not become the app's remembered mode
  {"cmd": "app", "value": "WINWORD.EXE"}  -> name the application this session serves;
                                       it comes back in the 中/英 mode it was left in
                                       (remember_app_mode); the reply carries "mode"
  {"cmd": "reset"}                  -> drop the current composition
  {"cmd": "script", "value": "simplified"}  -> this session's output script
                                       ("traditional" | "simplified" | "default" = the setting)
  {"cmd": "passed", "value": "enter"}  -> a key the front-end let the application handle
                                       (enter, tab, space, a character, or "other"): ends
                                       the clause, drops the context and any predictions
  {"cmd": "sentences"}              -> {"ok": true, "count": N}: how many sentences are
                                       remembered; "value": "clear" forgets them all first,
                                       "value": "forget" with "text": "..." forgets one
  {"cmd": "refine"}                 -> the pause the last output asked for ("refine": ms)
                                       has passed: run the AI check on the sentence on
                                       offer; the reply is a session output, with
                                       "refined": true when the first candidate changed
  {"cmd": "ai"}                     -> {"ok": true, "installed": .., "enabled": ..,
                                       "state": "off|loading|ready|asleep|failed", ..}:
                                       the optional AI check; "value": "on" turns it on
                                       for this connection only, whatever the setting
                                       says (until "value": "default" or disconnect)
  {"cmd": "learn", "value": "off"}  -> nothing typed in any application is learned while
                                       this connection lives ("default" or disconnect ends
                                       it): for tests that type through Windows into the
                                       user's own engine. Replies {"ok": true, "learning": ..}
  {"cmd": "reload"}                 -> re-read config.json now (it is also watched)
  {"cmd": "shutdown"}               -> the engine process exits (the settings window
                                       uses it to restart the engine)
  {"cmd": "ping"}                   -> {"ok": true, "version": ...}
  {"cmd": "quit"}                   -> closes this connection (stdio: exits)

Every response echoes the request's "id" when one was given.
"""
from __future__ import annotations

import json
import logging
import sys
import threading

from . import __version__
from .engine import Engine
from .paths import user_data_dir

PIPE_ADDRESS = r"\\.\pipe\MyInput"
MUTEX_NAME = "Local\\MyInputEngine"
ERROR_ALREADY_EXISTS = 183

log = logging.getLogger("myinput.server")
_engine_lock = threading.Lock()
_mutex_handle = None


def handle(session, req: dict) -> dict | None:
    """Apply one request to a session. None means 'close this connection'."""
    if "key" in req:
        before = req.get("before")
        return session.process_key(str(req["key"]), before if isinstance(before, str) else None).to_dict()
    cmd = req.get("cmd")
    if cmd == "ping":
        return {"ok": True, "version": __version__}
    if cmd == "reset":
        session.reset()
        session.clause = []     # the focus moved: a new clause
        return session.process_key("escape").to_dict()
    if cmd == "mode":
        value = req.get("value", "toggle")
        temporary = bool(req.get("temporary", False))
        out = session.toggle_mode() if value == "toggle" else session.set_mode(value, temporary=temporary)
        return out.to_dict()
    if cmd == "app":
        return session.set_app(str(req.get("value", "")) or None).to_dict()
    if cmd == "script":
        value = req.get("value")
        return session.set_script(None if value in (None, "default") else str(value)).to_dict()
    if cmd == "passed":
        session.key_passed(str(req.get("value", "other")))
        return {"ok": True}
    if cmd == "sentences":
        memory = session.engine.sentences
        if req.get("value") == "clear":
            memory.clear()
        elif req.get("value") == "forget":
            memory.forget(str(req.get("text", "")))
        return {"ok": True, "count": len(memory.sentences)}
    if cmd == "refine":
        return session.refine().to_dict()
    if cmd == "ai":
        if "value" in req:
            session.set_ai(str(req["value"]))
        return {"ok": True, **session.engine.ai.status()}
    if cmd == "learn":
        if "value" in req:
            session.set_learning(str(req["value"]))
        return {"ok": True, "learning": session.learning}
    if cmd == "reload":
        return {"ok": session.engine.reload_config()}
    if cmd == "shutdown":
        return {"ok": True, "shutdown": True}  # the pipe loop exits the process after replying
    if cmd == "quit":
        return None
    return {"error": f"unknown request: {req}"}


def _respond(session, line: str) -> dict | None:
    try:
        req = json.loads(line)
    except ValueError:
        return {"error": "bad json"}
    if not isinstance(req, dict):
        return {"error": "request must be an object"}
    with _engine_lock:
        resp = handle(session, req)
    if resp is not None and "id" in req:
        resp["id"] = req["id"]
    return resp


# ---------------------------------------------------------------- stdio
def serve(engine: Engine, inp=None, outp=None) -> None:
    inp = inp or sys.stdin
    outp = outp or sys.stdout
    session = engine.new_session()
    for line in inp:
        line = line.strip()
        if not line:
            continue
        resp = _respond(session, line)
        if resp is None:
            break
        outp.write(json.dumps(resp, ensure_ascii=False) + "\n")
        outp.flush()


# ---------------------------------------------------------------- named pipe
def _acquire_single_instance() -> bool:
    """Hold a named mutex so only one engine serves the pipe."""
    global _mutex_handle
    if sys.platform != "win32":
        return True
    import ctypes
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateMutexW.restype = ctypes.c_void_p
    kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_wchar_p]
    _mutex_handle = kernel32.CreateMutexW(None, False, MUTEX_NAME)
    return ctypes.get_last_error() != ERROR_ALREADY_EXISTS


def _client_loop(engine: Engine, conn, peer: int) -> None:
    session = engine.new_session()
    log.info("client %d connected", peer)
    try:
        while True:
            try:
                data = conn.recv_bytes()
            except (EOFError, OSError):
                break
            resp = _respond(session, data.decode("utf-8", "replace"))
            if resp is None:
                break
            conn.send_bytes(json.dumps(resp, ensure_ascii=False).encode("utf-8"))
            if resp.get("shutdown"):
                import os
                log.info("shutdown requested by client %d", peer)
                logging.shutdown()
                os._exit(0)
    except Exception:  # keep serving other apps whatever one client does
        log.exception("client %d failed", peer)
    finally:
        with _engine_lock:
            session.close()
        try:
            conn.close()
        except OSError:
            pass
        log.info("client %d closed", peer)


def _current_user_sid() -> str:
    """String SID of the account running the engine (for the pipe's DACL)."""
    import ctypes
    from ctypes import wintypes
    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    TOKEN_QUERY, TokenUser = 0x0008, 1
    # Handles are pointer-sized. Without these declarations ctypes passes the
    # pseudo-handle from GetCurrentProcess (-1) as a 32-bit int, which x64
    # Windows rejects with "The handle is invalid".
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    advapi32.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    advapi32.OpenProcessToken.restype = wintypes.BOOL
    token = wintypes.HANDLE()
    if not advapi32.OpenProcessToken(kernel32.GetCurrentProcess(), TOKEN_QUERY, ctypes.byref(token)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        size = wintypes.DWORD()
        advapi32.GetTokenInformation(token, TokenUser, None, 0, ctypes.byref(size))
        buf = ctypes.create_string_buffer(size.value)
        if not advapi32.GetTokenInformation(token, TokenUser, buf, size, ctypes.byref(size)):
            raise ctypes.WinError(ctypes.get_last_error())
        sid = ctypes.cast(buf, ctypes.POINTER(ctypes.c_void_p))[0]  # TOKEN_USER.User.Sid
        out = ctypes.c_wchar_p()
        if not advapi32.ConvertSidToStringSidW(ctypes.c_void_p(sid), ctypes.byref(out)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            return out.value
        finally:
            kernel32.LocalFree(out)
    finally:
        kernel32.CloseHandle(token)


class _PipeListener:
    """Like multiprocessing.connection's pipe listener, but the pipe gets an
    explicit DACL: full access for this user, read/write for app packages
    (ALL APPLICATION PACKAGES S-1-15-2-1 and ALL RESTRICTED APPLICATION
    PACKAGES S-1-15-2-2), so the text service can reach the engine from inside
    Store / AppContainer applications too. The default DACL shuts them out."""

    BUFSIZE = 65536

    def __init__(self, address: str):
        import ctypes
        import _winapi
        self._winapi = _winapi
        self._address = address
        self._kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self._kernel32.CreateNamedPipeW.restype = ctypes.c_void_p
        self._kernel32.CreateNamedPipeW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32,
                                                    ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
        advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
        sddl = f"D:(A;;GA;;;{_current_user_sid()})(A;;GRGW;;;AC)(A;;GRGW;;;S-1-15-2-2)"
        self._sd = ctypes.c_void_p()
        if not advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, ctypes.byref(self._sd), None):
            raise ctypes.WinError(ctypes.get_last_error())

        class SECURITY_ATTRIBUTES(ctypes.Structure):
            _fields_ = [("nLength", ctypes.c_uint32), ("lpSecurityDescriptor", ctypes.c_void_p),
                        ("bInheritHandle", ctypes.c_int)]

        self._sa = SECURITY_ATTRIBUTES(ctypes.sizeof(SECURITY_ATTRIBUTES), self._sd, 0)
        self._handle = self._new_instance(first=True)

    def _new_instance(self, first: bool):
        import ctypes
        w = self._winapi
        flags = w.PIPE_ACCESS_DUPLEX | w.FILE_FLAG_OVERLAPPED
        if first:
            flags |= w.FILE_FLAG_FIRST_PIPE_INSTANCE
        h = self._kernel32.CreateNamedPipeW(
            self._address, flags, w.PIPE_TYPE_MESSAGE | w.PIPE_READMODE_MESSAGE | w.PIPE_WAIT,
            w.PIPE_UNLIMITED_INSTANCES, self.BUFSIZE, self.BUFSIZE, w.NMPWAIT_WAIT_FOREVER,
            ctypes.byref(self._sa))
        if h is None or h == w.INVALID_HANDLE_VALUE:
            raise ctypes.WinError(ctypes.get_last_error())
        return h

    ERROR_NO_DATA = 232  # a client connected and closed before we accepted (probes do this)

    def accept(self):
        from multiprocessing.connection import PipeConnection
        w = self._winapi
        while True:
            handle = self._handle
            self._handle = self._new_instance(first=False)
            try:
                ov = w.ConnectNamedPipe(handle, overlapped=True)
                ov.GetOverlappedResult(True)
            except OSError as e:
                if e.winerror == w.ERROR_PIPE_CONNECTED:
                    pass
                elif e.winerror == self.ERROR_NO_DATA:
                    w.CloseHandle(handle)
                    continue
                else:
                    w.CloseHandle(handle)
                    raise
            return PipeConnection(handle)


def _watch_config(engine: Engine, interval: float = 1.0) -> None:
    """Apply config.json whenever the settings window saves it."""
    import os
    import time
    path = engine.config_path
    last = None
    while True:
        time.sleep(interval)
        engine.ai.tick()        # drops the AI model after a long silence
        try:
            stamp = os.stat(path).st_mtime_ns if path and path.exists() else None
        except OSError:
            continue
        if stamp == last:
            continue
        last = stamp
        if stamp is None:
            continue
        with _engine_lock:
            try:
                engine.reload_config()
                log.info("config reloaded: script=%s punctuation=%s half_width=%s quotes=%s fuzzy=%s",
                         engine.config.output_script, "full" if engine.config.chinese_punctuation else "half",
                         "".join(engine.config.half_width), engine.config.quote_style, ",".join(engine.config.fuzzy))
            except Exception:
                log.exception("config reload failed")


def serve_pipe(engine: Engine, address: str = PIPE_ADDRESS) -> int:
    if not _acquire_single_instance():
        log.info("another engine already serves %s; exiting", address)
        return 0
    try:
        listener = _PipeListener(address)
    except OSError:
        log.exception("cannot create pipe %s", address)
        return 1
    threading.Thread(target=_watch_config, args=(engine,), daemon=True).start()
    log.info("MyInput engine %s serving %s (app packages allowed)", __version__, address)
    peer = 0
    while True:
        try:
            conn = listener.accept()
        except OSError:
            log.exception("accept failed")
            continue
        peer += 1
        threading.Thread(target=_client_loop, args=(engine, conn, peer), daemon=True).start()


def _setup_logging() -> None:
    log_dir = user_data_dir()
    try:
        log_dir.mkdir(parents=True, exist_ok=True)
        handler: logging.Handler = logging.FileHandler(log_dir / "server.log", encoding="utf-8")
    except OSError:
        handler = logging.StreamHandler(sys.stderr)
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logging.basicConfig(level=logging.INFO, handlers=[handler])


def serve_socket(engine: Engine, path: str) -> int:
    """macOS / Linux counterpart of the named pipe: a Unix domain socket with
    multiprocessing's framing (4-byte big-endian length, then the JSON bytes),
    one Session per connection. The macOS front-end (frontend/macos) talks this."""
    import os
    from multiprocessing.connection import Listener
    try:
        if os.path.exists(path):
            os.unlink(path)
        listener = Listener(path, family="AF_UNIX")
        os.chmod(path, 0o600)
    except OSError:
        log.exception("cannot listen on %s", path)
        return 1
    threading.Thread(target=_watch_config, args=(engine,), daemon=True).start()
    log.info("MyInput engine %s serving %s", __version__, path)
    peer = 0
    while True:
        try:
            conn = listener.accept()
        except OSError:
            log.exception("accept failed")
            continue
        peer += 1
        threading.Thread(target=_client_loop, args=(engine, conn, peer), daemon=True).start()


def default_socket_path() -> str:
    import os
    base = os.environ.get("XDG_RUNTIME_DIR") or os.path.join(os.path.expanduser("~"), "Library", "Application Support", "MyInput")
    os.makedirs(base, exist_ok=True)
    return os.path.join(base, "engine.sock")


def main(argv=None) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="MyInput engine server")
    ap.add_argument("--pipe", nargs="?", const=PIPE_ADDRESS, metavar="NAME",
                    help=f"serve a Windows named pipe (default {PIPE_ADDRESS})")
    ap.add_argument("--socket", nargs="?", const="", metavar="PATH",
                    help="serve a Unix domain socket (macOS/Linux; default ~/Library/Application Support/MyInput/engine.sock)")
    args = ap.parse_args(argv)
    _setup_logging()
    try:
        engine = Engine.load()
    except Exception:
        log.exception("engine failed to load")
        return 1
    if args.pipe:
        return serve_pipe(engine, args.pipe)
    if args.socket is not None:
        return serve_socket(engine, args.socket or default_socket_path())
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    serve(engine)
    return 0


if __name__ == "__main__":
    sys.exit(main())
