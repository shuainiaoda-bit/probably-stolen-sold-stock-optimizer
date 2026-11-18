"""Windows private-desktop process control for the promotional capture coordinator.

Importing this module defines Win32 data structures. Desktop creation and process launch
occur only through PrivateDesktopProcess after the coordinator's explicit gate.
"""
import ctypes as C
from ctypes import wintypes as W
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import uuid

WINDOW_ENUM_CALLBACK = C.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)

class PROCESSENTRY32W(C.Structure):
    _fields_ = [('dwSize', W.DWORD), ('cntUsage', W.DWORD), ('th32ProcessID', W.DWORD),
                ('th32DefaultHeapID', C.c_size_t), ('th32ModuleID', W.DWORD), ('cntThreads', W.DWORD),
                ('th32ParentProcessID', W.DWORD), ('pcPriClassBase', W.LONG), ('dwFlags', W.DWORD),
                ('szExeFile', W.WCHAR * 260)]


class STARTUPINFOW(C.Structure):
    _fields_ = [('cb', W.DWORD), ('lpReserved', W.LPWSTR), ('lpDesktop', W.LPWSTR),
                ('lpTitle', W.LPWSTR), ('dwX', W.DWORD), ('dwY', W.DWORD), ('dwXSize', W.DWORD),
                ('dwYSize', W.DWORD), ('dwXCountChars', W.DWORD), ('dwYCountChars', W.DWORD),
                ('dwFillAttribute', W.DWORD), ('dwFlags', W.DWORD), ('wShowWindow', W.WORD),
                ('cbReserved2', W.WORD), ('lpReserved2', C.c_void_p), ('hStdInput', W.HANDLE),
                ('hStdOutput', W.HANDLE), ('hStdError', W.HANDLE)]


class PROCESS_INFORMATION(C.Structure):
    _fields_ = [('hProcess', W.HANDLE), ('hThread', W.HANDLE), ('dwProcessId', W.DWORD),
                ('dwThreadId', W.DWORD)]


class BASIC_LIMIT(C.Structure):
    _fields_ = [('PerProcessUserTimeLimit', C.c_longlong), ('PerJobUserTimeLimit', C.c_longlong),
                ('LimitFlags', W.DWORD), ('MinimumWorkingSetSize', C.c_size_t),
                ('MaximumWorkingSetSize', C.c_size_t), ('ActiveProcessLimit', W.DWORD),
                ('Affinity', C.c_size_t), ('PriorityClass', W.DWORD), ('SchedulingClass', W.DWORD)]


class IO_COUNTERS(C.Structure):
    _fields_ = [(name, C.c_ulonglong) for name in ('ReadOperationCount', 'WriteOperationCount',
                'OtherOperationCount', 'ReadTransferCount', 'WriteTransferCount', 'OtherTransferCount')]


class EXTENDED_LIMIT(C.Structure):
    _fields_ = [('BasicLimitInformation', BASIC_LIMIT), ('IoInfo', IO_COUNTERS),
                ('ProcessMemoryLimit', C.c_size_t), ('JobMemoryLimit', C.c_size_t),
                ('PeakProcessMemoryUsed', C.c_size_t), ('PeakJobMemoryUsed', C.c_size_t)]


def require(value, message):
    if not value:
        raise RuntimeError(message)


def error(message):
    return OSError(C.get_last_error(), message)


def api():
    require(os.name == 'nt', 'Private desktop capture requires Windows')
    kernel = C.WinDLL('kernel32', use_last_error=True)
    user = C.WinDLL('user32', use_last_error=True)
    definitions = (
        (kernel, 'CreateToolhelp32Snapshot', W.HANDLE, [W.DWORD, W.DWORD]),
        (kernel, 'Process32FirstW', W.BOOL, [W.HANDLE, C.POINTER(PROCESSENTRY32W)]),
        (kernel, 'Process32NextW', W.BOOL, [W.HANDLE, C.POINTER(PROCESSENTRY32W)]),
        (kernel, 'CloseHandle', W.BOOL, [W.HANDLE]),
        (kernel, 'CreateProcessW', W.BOOL, [W.LPCWSTR, W.LPWSTR, C.c_void_p, C.c_void_p, W.BOOL,
          W.DWORD, C.c_void_p, W.LPCWSTR, C.POINTER(STARTUPINFOW), C.POINTER(PROCESS_INFORMATION)]),
        (kernel, 'CreateJobObjectW', W.HANDLE, [C.c_void_p, W.LPCWSTR]),
        (kernel, 'SetInformationJobObject', W.BOOL, [W.HANDLE, C.c_int, C.c_void_p, W.DWORD]),
        (kernel, 'AssignProcessToJobObject', W.BOOL, [W.HANDLE, W.HANDLE]),
        (kernel, 'ResumeThread', W.DWORD, [W.HANDLE]),
        (kernel, 'GetExitCodeProcess', W.BOOL, [W.HANDLE, C.POINTER(W.DWORD)]),
        (kernel, 'WaitForSingleObject', W.DWORD, [W.HANDLE, W.DWORD]),
        (kernel, 'TerminateProcess', W.BOOL, [W.HANDLE, W.UINT]),
        (kernel, 'TerminateJobObject', W.BOOL, [W.HANDLE, W.UINT]),
        (user, 'CreateDesktopW', W.HANDLE, [W.LPCWSTR, W.LPCWSTR, C.c_void_p, W.DWORD, W.DWORD, C.c_void_p]),
        (user, 'CloseDesktop', W.BOOL, [W.HANDLE]),
        (user, 'OpenInputDesktop', W.HANDLE, [W.DWORD, W.BOOL, W.DWORD]),
        (user, 'GetProcessWindowStation', W.HANDLE, []),
        (user, 'GetThreadDesktop', W.HANDLE, [W.DWORD]),
        (user, 'EnumDesktopWindows', W.BOOL, [W.HANDLE, WINDOW_ENUM_CALLBACK, W.LPARAM]),
        (user, 'GetWindowThreadProcessId', W.DWORD, [W.HWND, C.POINTER(W.DWORD)]),
        (user, 'GetUserObjectInformationW', W.BOOL, [W.HANDLE, C.c_int, C.c_void_p, W.DWORD, C.POINTER(W.DWORD)]),
    )
    for library, name, result, args in definitions:
        function = getattr(library, name)
        function.restype, function.argtypes = result, args
    return kernel, user


def object_name(user, handle):
    require(handle, 'Missing Windows desktop or window-station handle')
    buffer = C.create_unicode_buffer(512)
    length = W.DWORD()
    if not user.GetUserObjectInformationW(handle, 2, buffer, C.sizeof(buffer), C.byref(length)):
        raise error('Cannot read Windows user-object name')
    return buffer.value


def input_desktop():
    _, user = api()
    handle = user.OpenInputDesktop(0, False, 1)
    if not handle:
        raise error('Cannot verify the current input desktop; capture remains blocked')
    try:
        return object_name(user, handle)
    finally:
        user.CloseDesktop(handle)


def game_processes():
    kernel, _ = api()
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    if snapshot in (None, C.c_void_p(-1).value):
        raise error('Cannot inspect running processes; capture remains blocked')
    result = []
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = C.sizeof(entry)
        if not kernel.Process32FirstW(snapshot, C.byref(entry)):
            raise error('Cannot enumerate running processes; capture remains blocked')
        while True:
            if entry.szExeFile.casefold() == 'probably stolen.exe':
                result.append(entry.th32ProcessID)
            if not kernel.Process32NextW(snapshot, C.byref(entry)):
                require(C.get_last_error() == 18, 'Process enumeration did not complete')
                break
    finally:
        kernel.CloseHandle(snapshot)
    return result


class PrivateDesktopProcess:
    """A single reviewed game process plus its owned Windows job and desktop."""
    def __init__(self, command, cwd, proof_path, before_resume=None):
        require(not game_processes(), 'Probably Stolen is already running; capture is blocked')
        self.kernel, self.user = api()
        self.command = [str(value) for value in command]
        self.proof_path = Path(proof_path)
        self.desktop = self.job = self.process = None
        self.pid = self.tid = None
        self.returncode = None
        self.started_at = time.monotonic()
        self.name = 'CombatPromo_' + uuid.uuid4().hex
        self.proof = {'desktopName': self.name, 'inputDesktopBefore': input_desktop(),
                      'desktopChecks': 0, 'inputChecks': 0, 'windowsJob': 'kill-on-close',
                      'startupDesktopExplicit': True, 'switchDesktopCalled': False,
                      'osInputUsed': False, 'childHandlesInherited': False}
        station = object_name(self.user, self.user.GetProcessWindowStation())
        require(station.casefold() == 'winsta0', 'Capture requires the existing interactive window station')
        try:
            # A worker owns any thread association made by CreateDesktopW.
            handles, failures = [], []
            def create():
                handle = self.user.CreateDesktopW(self.name, None, None, 0, 1 | 2 | 4 | 64 | 128, None)
                if handle:
                    handles.append(handle)
                else:
                    failures.append(error('CreateDesktopW failed'))
            thread = threading.Thread(target=create)
            thread.start()
            thread.join()
            if failures:
                raise failures[0]
            require(len(handles) == 1, 'Private desktop was not created')
            self.desktop = handles[0]
            require(object_name(self.user, self.desktop) == self.name, 'Private desktop name mismatch')
            require(input_desktop() != self.name, 'Private desktop must remain outside the input desktop')
            self.job = self.kernel.CreateJobObjectW(None, None)
            if not self.job:
                raise error('CreateJobObjectW failed')
            limits = EXTENDED_LIMIT()
            limits.BasicLimitInformation.LimitFlags = 0x2000
            if not self.kernel.SetInformationJobObject(self.job, 9, C.byref(limits), C.sizeof(limits)):
                raise error('Cannot enable capture-process cleanup')
            startup = STARTUPINFOW()
            startup.cb = C.sizeof(startup)
            startup.lpDesktop = station + chr(92) + self.name
            startup.dwFlags = 1
            startup.wShowWindow = 0
            info = PROCESS_INFORMATION()
            arguments = C.create_unicode_buffer(subprocess.list2cmdline(self.command))
            # Create suspended so the cleanup job is attached before game code runs.
            if not self.kernel.CreateProcessW(self.command[0], arguments, None, None, False,
                    0x4 | 0x08000000 | 0x00004000, None, str(cwd), C.byref(startup), C.byref(info)):
                raise error('Private-desktop game process creation failed')
            self.process, self.pid, self.tid = info.hProcess, info.dwProcessId, info.dwThreadId
            try:
                if not self.kernel.AssignProcessToJobObject(self.job, self.process):
                    self.kernel.TerminateProcess(self.process, 2)
                    raise error('Cannot bind game to the cleanup job')
                if before_resume is not None:
                    before_resume(self.pid)
                    self.proof['beforeResumeGuardAttached'] = True
                if self.kernel.ResumeThread(info.hThread) == 0xffffffff:
                    raise error('Cannot start the private-desktop game thread')
            finally:
                self.kernel.CloseHandle(info.hThread)
            self.proof.update({'processId': self.pid, 'mainThreadId': self.tid, 'startupDesktop': startup.lpDesktop})
            self.save_proof()
        except BaseException:
            self.kill()
            self.close()
            raise

    def save_proof(self):
        self.proof_path.parent.mkdir(parents=True, exist_ok=True)
        self.proof_path.write_text(json.dumps(self.proof, indent=2) + '\n', encoding='utf-8')

    def verify_desktop(self):
        current = input_desktop()
        self.proof['inputChecks'] += 1
        require(current != self.name, 'Private capture desktop became the input desktop')
        handle = self.user.GetThreadDesktop(self.tid)
        if handle:
            actual = object_name(self.user, handle)
            require(actual == self.name, 'Game main thread is outside its private desktop')
            self.proof['desktopChecks'] += 1
            self.proof['gameThreadDesktop'] = actual
        else:
            # Unity can create the presentation window on a different thread.
            # Enumerating only the owned desktop proves where that window lives.
            windows = []
            @WINDOW_ENUM_CALLBACK
            def inspect_window(window, unused):
                owner = W.DWORD()
                thread_id = self.user.GetWindowThreadProcessId(window, C.byref(owner))
                if owner.value == self.pid:
                    windows.append({'window': int(window), 'thread': int(thread_id)})
                return True
            C.set_last_error(0)
            enumerated = self.user.EnumDesktopWindows(self.desktop, inspect_window, 0)
            if not enumerated and C.get_last_error() != 0:
                raise error('Cannot enumerate the private capture desktop')
            if windows:
                self.proof['desktopChecks'] += 1
                self.proof['gameThreadDesktop'] = self.name
                self.proof['windowVerification'] = 'Owned PID window enumerated on the explicit private desktop'
                self.proof['ownedWindows'] = windows
            elif self.poll() is None and time.monotonic() - self.started_at >= 30:
                raise error('Cannot verify a game window on its private desktop')
        self.proof['lastInputDesktop'] = current

    def poll(self):
        if self.returncode is not None:
            return self.returncode
        if not self.process:
            return None
        value = W.DWORD()
        if not self.kernel.GetExitCodeProcess(self.process, C.byref(value)):
            raise error('Cannot read private capture-process status')
        if value.value != 259:
            self.returncode = value.value
        return self.returncode

    def wait(self, timeout):
        deadline = time.monotonic() + timeout
        try:
            while self.poll() is None:
                self.verify_desktop()
                if time.monotonic() >= deadline:
                    raise subprocess.TimeoutExpired(self.command, timeout)
                self.kernel.WaitForSingleObject(self.process, 200)
            self.proof['exitCode'] = self.returncode
            self.proof['inputDesktopAfter'] = input_desktop()
            require(self.proof['desktopChecks'] > 0, 'No game-thread desktop affinity was verified')
            return self.returncode
        except BaseException as failure:
            self.proof['failure'] = str(failure)
            self.kill()
            raise
        finally:
            self.save_proof()

    def kill(self):
        if self.job:
            self.kernel.TerminateJobObject(self.job, 2)
        elif self.process:
            self.kernel.TerminateProcess(self.process, 2)

    def close(self):
        try:
            if self.job:
                self.kernel.CloseHandle(self.job)
                self.job = None
            if self.process:
                self.proof['processStoppedBeforeHandleClose'] = self.kernel.WaitForSingleObject(self.process, 5000) == 0
                self.kernel.CloseHandle(self.process)
                self.process = None
            if self.desktop:
                self.proof['desktopHandleClosed'] = bool(self.user.CloseDesktop(self.desktop))
                self.desktop = None
        finally:
            self.save_proof()
