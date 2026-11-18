"""Core Audio guard for disposable Windows game tests; this is not a mod asset."""
from __future__ import annotations

import ctypes as C
from ctypes import wintypes as W
from pathlib import Path
import hashlib
import json
import os
import subprocess
import threading
import time
import uuid


class GUID(C.Structure):
    _fields_ = [("bytes", C.c_ubyte * 16)]

    @classmethod
    def of(cls, value):
        return cls((C.c_ubyte * 16).from_buffer_copy(uuid.UUID(value).bytes_le))


IID_ENUM = GUID.of("a95664d2-9614-4f35-a746-de8db63617e6")
CLSID_ENUM = GUID.of("bcde0395-e52f-467c-8e3d-c4579291692e")
IID_MANAGER = GUID.of("77aa99a0-1bd6-484f-8bc7-2c654c9a9b6f")
IID_CONTROL2 = GUID.of("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")
IID_VOLUME = GUID.of("87ce5498-68d6-44e5-9215-6da47ef883d8")
IID_METER = GUID.of("c02216f6-8c67-4b5b-9d00-d008e73e0064")
IID_NOTIFY = GUID.of("641dd20b-4d41-49cc-aba3-174b9477bb08")
IID_UNKNOWN = GUID.of("00000000-0000-0000-c000-000000000046")
EVENT_CONTEXT = GUID.of("cdcf263e-45a9-4a65-9b7b-c7c0af60e5c3")
HR = C.c_long
PTR = C.c_void_p


def check(hr, operation):
    if hr < 0:
        raise OSError(f"{operation}: HRESULT 0x{hr & 0xffffffff:08x}")
    return hr


def call(pointer, slot, *typed):
    """Invoke an IUnknown-derived ABI method with explicit argument declarations."""
    if not pointer:
        raise RuntimeError("Missing COM interface")
    types, values = zip(*typed) if typed else ((), ())
    table = C.cast(pointer, C.POINTER(C.POINTER(PTR))).contents
    function = C.WINFUNCTYPE(HR, PTR, *types)(table[slot])
    return function(pointer, *values)


def release(pointer):
    if pointer:
        call(pointer, 2)


def query(pointer, iid):
    result = PTR()
    check(call(pointer, 0, (C.POINTER(GUID), C.byref(iid)), (C.POINTER(PTR), C.byref(result))), "QueryInterface")
    return result


def text_out(pointer, slot, ole):
    result = PTR()
    check(call(pointer, slot, (C.POINTER(PTR), C.byref(result))), "COM string")
    try:
        return C.wstring_at(result) if result else ""
    finally:
        if result:
            ole.CoTaskMemFree(result)


class ProcessEntry(C.Structure):
    _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
               ("th32DefaultHeapID", C.c_size_t), ("th32ModuleID", W.DWORD),
               ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
               ("pcPriClassBase", W.LONG), ("dwFlags", W.DWORD), ("szExeFile", W.WCHAR * 260)]


class SessionNotification:
    """Keep the Python callbacks and COM vtable alive until unregistration."""
    def __init__(self, on_created):
        self.references = 1
        def qi(this, iid, output):
            wanted = bytes(iid.contents.bytes)
            if wanted in (bytes(IID_NOTIFY.bytes), bytes(IID_UNKNOWN.bytes)):
                self.references += 1
                output[0] = C.cast(C.pointer(self.instance), PTR)
                return 0
            output[0] = None
            return -2147467262
        def add(this):
            self.references += 1
            return self.references
        def drop(this):
            self.references -= 1
            return self.references
        def created(this, session):
            on_created(session)
            return 0
        self.callbacks = [C.WINFUNCTYPE(HR, PTR, C.POINTER(GUID), C.POINTER(PTR))(qi),
                          C.WINFUNCTYPE(W.ULONG, PTR)(add), C.WINFUNCTYPE(W.ULONG, PTR)(drop),
                          C.WINFUNCTYPE(HR, PTR, PTR)(created)]
        self.table = (PTR * 4)(*[C.cast(f, PTR) for f in self.callbacks])
        class Instance(C.Structure):
            _fields_ = [("vtable", C.POINTER(PTR))]
        self.instance = Instance(self.table)
        self.pointer = C.cast(C.pointer(self.instance), PTR)


class GameAudioGuard:
    """Mute only attached test PIDs and their descendants; abort on audio or lost verification.

    Enter before launching the game. The caller must prepare zero game volumes and
    pass -noaudio. attach_pid can run while a newly created game is suspended.
    """
    def __init__(self, proof_path, interval=0.02):
        if os.name != "nt":
            raise RuntimeError("Windows Core Audio is required")
        self.path = Path(proof_path)
        self.interval = interval
        self.lock = threading.RLock()
        self.ready = threading.Event()
        self.stop = threading.Event()
        self.attached = threading.Event()
        self.proc = None
        self.owned = {}
        self.roots = set()
        self.failure = None
        self.scans = 0
        self.last_save = 0.0
        self.verified_scans = 0
        self.managers = {}
        self.sessions = {}
        self.proof = dict(status="PREPARING", policy="game volumes zero + -noaudio + Core Audio session mute + peak checks",
                          started=time.time(), endpointScans=0, sessionScans=0, targetSessionObservations=0,
                          maximumTargetPeak=0.0, noSessionScans=0, notificationCallbacks=0,
                          otherAppVolumeWrites=0, attachedPids=[], endpointIds=[], sessions=[], errors=[])
        self.kernel = C.WinDLL("kernel32", use_last_error=True)
        definitions = [("OpenProcess", W.HANDLE, [W.DWORD, W.BOOL, W.DWORD]),
                       ("CloseHandle", W.BOOL, [W.HANDLE]),
                       ("GetProcessTimes", W.BOOL, [W.HANDLE] + [C.POINTER(W.FILETIME)] * 4),
                       ("GetExitCodeProcess", W.BOOL, [W.HANDLE, C.POINTER(W.DWORD)]),
                       ("TerminateProcess", W.BOOL, [W.HANDLE, W.UINT]),
                       ("CreateToolhelp32Snapshot", W.HANDLE, [W.DWORD, W.DWORD]),
                       ("Process32FirstW", W.BOOL, [W.HANDLE, C.POINTER(ProcessEntry)]),
                       ("Process32NextW", W.BOOL, [W.HANDLE, C.POINTER(ProcessEntry)])]
        for name, result, args in definitions:
            f = getattr(self.kernel, name); f.restype = result; f.argtypes = args

    def _identity(self, pid):
        handle = self.kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            return None
        try:
            values = [W.FILETIME() for _ in range(4)]
            if not self.kernel.GetProcessTimes(handle, *[C.byref(v) for v in values]):
                raise C.WinError(C.get_last_error())
            exit_code = W.DWORD()
            if not self.kernel.GetExitCodeProcess(handle, C.byref(exit_code)):
                raise C.WinError(C.get_last_error())
            if exit_code.value != 259:
                return None
            return values[0].dwLowDateTime | values[0].dwHighDateTime << 32
        finally:
            self.kernel.CloseHandle(handle)

    def attach_pid(self, pid):
        """Register the PID returned by this test's CreateProcess, preferably before ResumeThread."""
        self.raise_if_failed()
        identity = self._identity(pid)
        if identity is None:
            raise RuntimeError("The owned test process is not running")
        with self.lock:
            if pid in self.owned and self.owned[pid] != identity:
                raise RuntimeError("Test PID was reused")
            self.owned[pid] = identity
            self.roots.add(pid)
            self.proof["attachedPids"] = sorted(self.owned)
            self.attached.set()
            self.proof["status"] = "ATTACHED_AWAITING_VERIFICATION"
            self._save_proof()

    def attach(self, proc):
        if proc.poll() is not None:
            raise RuntimeError("Test process exited before audio verification")
        self.attach_pid(proc.pid)
        self.proc = proc
        return self

    def _descendants(self):
        handle = self.kernel.CreateToolhelp32Snapshot(2, 0)
        if handle in (None, C.c_void_p(-1).value):
            raise C.WinError(C.get_last_error())
        tree = {}
        try:
            entry = ProcessEntry(); entry.dwSize = C.sizeof(entry)
            if not self.kernel.Process32FirstW(handle, C.byref(entry)):
                raise C.WinError(C.get_last_error())
            while True:
                tree[entry.th32ProcessID] = entry.th32ParentProcessID
                if not self.kernel.Process32NextW(handle, C.byref(entry)):
                    if C.get_last_error() != 18:
                        raise C.WinError(C.get_last_error())
                    break
        finally:
            self.kernel.CloseHandle(handle)
        with self.lock:
            live = {pid for pid, identity in self.owned.items() if self._identity(pid) == identity}
            changed = True
            while changed:
                changed = False
                for pid, parent in tree.items():
                    if parent in live and pid not in self.owned:
                        identity = self._identity(pid)
                        if identity is not None and identity >= self.owned[parent]:
                            self.owned[pid] = identity; live.add(pid); changed = True
            self.proof["attachedPids"] = sorted(self.owned)

    def _kill_owned(self):
        with self.lock:
            if self.proc is not None and self._identity(self.proc.pid) == self.owned.get(self.proc.pid):
                try:
                    self.proc.kill()
                except Exception as error:
                    self.proof["errors"].append("Owned-process kill: " + str(error))
            # A PID identity check prevents terminating another process after PID reuse.
            for pid, identity in self.owned.items():
                if self._identity(pid) != identity:
                    continue
                handle = self.kernel.OpenProcess(0x1001, False, pid)
                if not handle:
                    continue
                try:
                    values = [W.FILETIME() for _ in range(4)]
                    if not self.kernel.GetProcessTimes(handle, *[C.byref(v) for v in values]):
                        continue
                    actual = values[0].dwLowDateTime | values[0].dwHighDateTime << 32
                    if actual == identity:
                        self.kernel.TerminateProcess(handle, 3)
                finally:
                    self.kernel.CloseHandle(handle)
            self.proof["ownedTestProcessesStopped"] = True
            if self.failure:
                self.proof["ownedTestStoppedOnFailure"] = True

    def _fail(self, error):
        with self.lock:
            if self.failure is None:
                self.failure = str(error)
                self.proof["errors"].append(self.failure)
                self.proof["status"] = "FAIL"
        self._kill_owned()
        self.ready.set()
        self._save_proof()

    def _save_proof(self):
        with self.lock:
            self.proof["verifiedScans"] = self.verified_scans
            self.path.parent.mkdir(parents=True, exist_ok=True)
            temporary = self.path.with_name(self.path.name + ".tmp")
            temporary.write_text(json.dumps(self.proof, indent=2) + "\n", encoding="utf-8")
            temporary.replace(self.path)
            self.last_save = time.monotonic()

    def _observe(self, pointer, endpoint, notification=False):
        control = volume = meter = None
        try:
            control = query(pointer, IID_CONTROL2)
            pid = W.DWORD()
            check(call(control, 14, (C.POINTER(W.DWORD), C.byref(pid))), "Session process ID")
            with self.lock:
                if pid.value not in self.owned or self._identity(pid.value) != self.owned[pid.value]:
                    return False
                volume = query(control, IID_VOLUME)
                meter = query(control, IID_METER)
                key = (endpoint, text_out(control, 13, self.ole))
                muted = W.BOOL()
                check(call(volume, 6, (C.POINTER(W.BOOL), C.byref(muted))), "Read application mute")
                if key not in self.sessions:
                    # Retain this interface until the test exits, so the original application mute can be restored.
                    retained = query(volume, IID_VOLUME)
                    row = dict(pid=pid.value, endpointId=endpoint,
                               sessionHash=hashlib.sha256(key[1].encode()).hexdigest(),
                               originalMute=bool(muted.value), muteVerified=False, maximumPeak=0.0,
                               observations=0, restored=False)
                    self.sessions[key] = (retained, row)
                    self.proof["sessions"].append(row)
                row = self.sessions[key][1]
                peak = C.c_float()
                check(call(meter, 3, (C.POINTER(C.c_float), C.byref(peak))), "Read application output peak")
                row["maximumPeak"] = max(row["maximumPeak"], peak.value)
                self.proof["maximumTargetPeak"] = max(self.proof["maximumTargetPeak"], peak.value)
                if peak.value > 0:
                    # Mute immediately as well as stopping the owned test; never alter the endpoint master volume.
                    check(call(volume, 5, (W.BOOL, True), (C.POINTER(GUID), C.byref(EVENT_CONTEXT))), "Mute audible test session")
                    raise RuntimeError(f"Owned test PID {pid.value} produced a nonzero session peak ({peak.value})")
                if not muted.value:
                    check(call(volume, 5, (W.BOOL, True), (C.POINTER(GUID), C.byref(EVENT_CONTEXT))), "Mute test application")
                check(call(volume, 6, (C.POINTER(W.BOOL), C.byref(muted))), "Verify application mute")
                check(call(meter, 3, (C.POINTER(C.c_float), C.byref(peak))), "Verify application output peak")
                if not muted.value or peak.value != 0:
                    raise RuntimeError("Application mute or zero-output verification failed")
                row["muteVerified"] = True
                row["observations"] += 1
                self.proof["targetSessionObservations"] += 1
                if notification:
                    self.proof["notificationCallbacks"] += 1
                return True
        finally:
            release(meter); release(volume); release(control)

    def _notification(self, pointer, endpoint):
        try:
            # Descendants can open a session between periodic process snapshots.
            if self.attached.is_set():
                self._descendants()
            self._observe(pointer, endpoint, True)
        except BaseException as error:
            self._fail(error)

    def _scan(self):
        collection = PTR()
        try:
            check(call(self.enumerator, 3, (C.c_int, 0), (W.DWORD, 1), (C.POINTER(PTR), C.byref(collection))), "Enumerate active render endpoints")
            count = W.UINT()
            check(call(collection, 3, (C.POINTER(W.UINT), C.byref(count))), "Count active render endpoints")
            if count.value == 0:
                raise RuntimeError("No active render endpoint is available for audio verification")
            observed = 0
            for index in range(count.value):
                device = PTR(); sessions = PTR()
                try:
                    check(call(collection, 4, (W.UINT, index), (C.POINTER(PTR), C.byref(device))), "Read render endpoint")
                    endpoint = text_out(device, 5, self.ole)
                    if endpoint not in self.managers:
                        manager = PTR()
                        check(call(device, 3, (C.POINTER(GUID), C.byref(IID_MANAGER)), (W.DWORD, 23),
                                   (PTR, None), (C.POINTER(PTR), C.byref(manager))), "Activate session manager")
                        notify = SessionNotification(lambda session, endpoint=endpoint: self._notification(session, endpoint))
                        check(call(manager, 6, (PTR, notify.pointer)), "Register new audio-session notification")
                        self.managers[endpoint] = (manager, notify)
                        self.proof["endpointIds"].append(endpoint)
                    manager = self.managers[endpoint][0]
                    check(call(manager, 5, (C.POINTER(PTR), C.byref(sessions))), "Enumerate audio sessions")
                    session_count = C.c_int()
                    # The first GetCount also enables session-created notifications.
                    check(call(sessions, 3, (C.POINTER(C.c_int), C.byref(session_count))), "Count audio sessions")
                    for n in range(session_count.value):
                        session = PTR()
                        try:
                            check(call(sessions, 4, (C.c_int, n), (C.POINTER(PTR), C.byref(session))), "Read audio session")
                            observed += int(self._observe(session, endpoint))
                            self.proof["sessionScans"] += 1
                        finally:
                            release(session)
                finally:
                    release(sessions); release(device)
            self.proof["endpointScans"] += count.value
            self.scans += 1
            if self.attached.is_set():
                self.verified_scans += 1
                if observed == 0:
                    self.proof["noSessionScans"] += 1
        finally:
            release(collection)

    def _run(self):
        initialized = False
        self.enumerator = PTR()
        try:
            self.ole = C.OleDLL("ole32")
            self.ole.CoInitializeEx.argtypes = [PTR, W.DWORD]; self.ole.CoInitializeEx.restype = HR
            self.ole.CoCreateInstance.argtypes = [C.POINTER(GUID), PTR, W.DWORD, C.POINTER(GUID), C.POINTER(PTR)]
            self.ole.CoCreateInstance.restype = HR
            self.ole.CoTaskMemFree.argtypes = [PTR]
            check(self.ole.CoInitializeEx(None, 0), "Initialize Core Audio MTA")
            initialized = True
            check(self.ole.CoCreateInstance(C.byref(CLSID_ENUM), None, 23, C.byref(IID_ENUM), C.byref(self.enumerator)), "Create endpoint enumerator")
            self._scan()
            self.proof["status"] = "ARMED"
            self._save_proof()
            self.ready.set()
            while not self.stop.wait(self.interval):
                if self.attached.is_set():
                    self._descendants()
                self._scan()
                if time.monotonic() - self.last_save >= 1:
                    self._save_proof()
                if self.failure:
                    break
        except BaseException as error:
            self._fail(error)
        finally:
            # TerminateProcess is asynchronous. Keep every owned session muted until
            # the corresponding test processes have actually stopped.
            deadline = time.monotonic() + 5
            live = lambda: any(self._identity(pid) == identity for pid, identity in self.owned.items())
            if self.failure:
                self._kill_owned()
                while live() and time.monotonic() < deadline:
                    time.sleep(0.02)
            safe_to_restore = not live()
            # close() stops attached test processes before releasing their mute protection.
            for manager, notify in self.managers.values():
                try:
                    check(call(manager, 7, (PTR, notify.pointer)), "Unregister audio-session notification")
                except Exception as error:
                    self._fail(error)
                release(manager)
            for volume, row in self.sessions.values():
                try:
                    if safe_to_restore:
                        check(call(volume, 5, (W.BOOL, row["originalMute"]), (C.POINTER(GUID), C.byref(EVENT_CONTEXT))), "Restore test session mute")
                        row["restored"] = True
                    else:
                        row["muteRetainedForLiveOwnedTest"] = True
                except Exception as error:
                    # Expired sessions can reject an update; no live process remains to play them.
                    row["restoreError"] = str(error)
                finally:
                    release(volume)
            release(self.enumerator)
            if initialized:
                self.ole.CoUninitialize()
            self.ready.set()

    def start(self):
        self.thread = threading.Thread(target=self._run, name="ProbablyStolen.CoreAudioGuard", daemon=True)
        self.thread.start()
        if not self.ready.wait(10):
            self._fail("Core Audio preflight timed out")
        self.raise_if_failed()
        return self

    def raise_if_failed(self):
        if self.failure:
            raise RuntimeError("Background game audio guard: " + self.failure)

    def verify(self, timeout=5):
        if not self.attached.is_set():
            raise RuntimeError("Attach the owned test process before verifying its audio")
        before = self.verified_scans
        deadline = time.monotonic() + timeout
        while self.verified_scans < before + 3:
            self.raise_if_failed()
            if time.monotonic() >= deadline:
                self._fail("Audio verification did not complete")
                self.raise_if_failed()
            time.sleep(self.interval)
        self.raise_if_failed()
        self.proof["status"] = "VERIFIED_MONITORING"
        self._save_proof()
        return dict(verifiedScans=self.verified_scans, targetSessions=len(self.sessions),
                    maximumPeak=self.proof["maximumTargetPeak"],
                    noAudioSession=self.proof["targetSessionObservations"] == 0)

    def wait(self, proc, timeout):
        if self.proc is not proc:
            self.attach(proc)
        self.verify()
        deadline = time.monotonic() + timeout
        while proc.poll() is None:
            self.raise_if_failed()
            if time.monotonic() >= deadline:
                proc.kill()
                raise subprocess.TimeoutExpired(getattr(proc, "args", "owned game test"), timeout)
            if hasattr(proc, "verify_desktop"):
                proc.verify_desktop()
            time.sleep(self.interval)
        self.raise_if_failed()
        return proc.poll()

    def close(self):
        # Scope exit never leaves a monitored test game running or unmutes a live test.
        if self.attached.is_set():
            self._kill_owned()
            deadline = time.monotonic() + 5
            while any(self._identity(pid) == identity for pid, identity in self.owned.items()):
                if time.monotonic() >= deadline:
                    self._fail("Owned process did not stop; mute protection retained")
                    raise RuntimeError(self.failure)
                time.sleep(0.02)
        self.stop.set()
        if hasattr(self, "thread"):
            self.thread.join(10)
            if self.thread.is_alive():
                self._fail("Audio guard did not stop")
        self.proof["finished"] = time.time()
        self.proof["verifiedScans"] = self.verified_scans
        self.proof["status"] = "FAIL" if self.failure else "PASS" if self.attached.is_set() else "PREFLIGHT_PASS"
        self._save_proof()
        self.raise_if_failed()

    def __enter__(self):
        return self.start()

    def __exit__(self, kind, value, trace):
        self.close()


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--proof", type=Path, required=True)
    parser.add_argument("--self-check", action="store_true", help="Read-only Core Audio preflight plus a silent, owned Python child")
    args = parser.parse_args()
    with GameAudioGuard(args.proof) as guard:
        if args.self_check:
            child = subprocess.Popen([os.sys.executable, "-c", "import time; time.sleep(0.5)"],
                                     creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            guard.attach(child)
            guard.wait(child, 3)
        else:
            time.sleep(0.1)
    print(json.dumps({"status": guard.proof["status"], "proof": str(args.proof),
                      "verifiedScans": guard.verified_scans, "targetSessions": len(guard.sessions),
                      "maximumTargetPeak": guard.proof["maximumTargetPeak"]}))
