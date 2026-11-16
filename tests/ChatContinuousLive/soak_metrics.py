"""Windows process counters and bounded, append-only soak evidence (no third-party packages)."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import time


class MemoryCounters(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in ('PeakWorkingSetSize', 'WorkingSetSize',
        'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage',
        'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]


def handle_counters(handle, pid):
    memory = MemoryCounters()
    memory.cb = ctypes.sizeof(memory)
    handles = wintypes.DWORD()
    if not ctypes.windll.psapi.GetProcessMemoryInfo(handle, ctypes.byref(memory), memory.cb):
        raise ctypes.WinError()
    if not ctypes.windll.kernel32.GetProcessHandleCount(handle, ctypes.byref(handles)):
        raise ctypes.WinError()
    return dict(pid=pid, workingSetBytes=memory.WorkingSetSize,
                privateBytes=memory.PrivateUsage, handles=handles.value)


def process_counters(process):
    return handle_counters(wintypes.HANDLE(int(process._handle)), process.pid)


class ProcessEntry(ctypes.Structure):
    _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD),
        ('th32ProcessID', wintypes.DWORD), ('th32DefaultHeapID', ctypes.c_size_t),
        ('th32ModuleID', wintypes.DWORD), ('cntThreads', wintypes.DWORD),
        ('th32ParentProcessID', wintypes.DWORD), ('pcPriClassBase', wintypes.LONG),
        ('dwFlags', wintypes.DWORD), ('szExeFile', wintypes.WCHAR * 260)]


def child_counters(parent):
    kernel = ctypes.windll.kernel32
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.OpenProcess.restype = wintypes.HANDLE
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    if snapshot == ctypes.c_void_p(-1).value: raise ctypes.WinError()
    entries = []
    entry = ProcessEntry()
    entry.dwSize = ctypes.sizeof(entry)
    try:
        more = kernel.Process32FirstW(wintypes.HANDLE(snapshot), ctypes.byref(entry))
        while more:
            entries.append((entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile))
            more = kernel.Process32NextW(wintypes.HANDLE(snapshot), ctypes.byref(entry))
    finally:
        kernel.CloseHandle(wintypes.HANDLE(snapshot))
    descendants = {parent}
    while True:
        found = descendants | {pid for pid, owner, _ in entries if owner in descendants}
        if found == descendants: break
        descendants = found
    result = []
    for pid, _, name in entries:
        if pid == parent or pid not in descendants: continue
        handle = kernel.OpenProcess(0x410, False, pid)
        if not handle: continue  # Short-lived verifier/agent may have exited since the snapshot.
        try:
            try: result.append(dict(name=name, **handle_counters(wintypes.HANDLE(handle), pid)))
            except OSError: pass  # Process exited during sampling.
        finally:
            kernel.CloseHandle(wintypes.HANDLE(handle))
    return result


def atomic_json(path, value):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    for attempt in range(11):
        try:
            os.replace(temporary, path)
            break
        except PermissionError:
            if attempt == 10: raise
            time.sleep(.03)


class SoakMetrics:
    def __init__(self, root):
        self.root = root
        self.last = 0
        self.epoch = None
        self.baseline = None
        self.last_disk = 0
        self.disk = {}
        self.samples = 0

    def sample(self, process, force=False):
        now = time.monotonic()
        if self.last and now-self.last > 90:
            raise RuntimeError('Observation gap exceeds 90 seconds; uninterrupted soak cannot be claimed')
        if not force and now - self.last < 15:
            return
        if process is None or process.poll() is not None:
            raise RuntimeError('Fixture service exited unexpectedly during observation')
        counters = process_counters(process)
        if self.epoch != process.pid:
            self.epoch = process.pid
            self.baseline = (now, counters)
        if now - self.last_disk >= 60 or force:
            sizes = []
            for p in self.root.rglob('*'):
                try:
                    if p.is_file(): sizes.append((p, p.stat().st_size))
                except FileNotFoundError:
                    pass  # SQLite and snapshot temporary files may disappear during the scan.
            self.disk = dict(evidenceBytes=sum(size for _, size in sizes),
                logBytes=sum(size for p, size in sizes if p.suffix in ('.log', '.out', '.err', '.jsonl')
                    or '.log.' in p.name or 'logs' in p.relative_to(self.root).parts),
                evidenceFiles=len(sizes))
            self.last_disk = now
        sample = dict(at=time.strftime('%Y-%m-%dT%H:%M:%S%z'), monotonic=now,
                      children=child_counters(process.pid),
                      processAgeSeconds=now-self.baseline[0], diskSampleMonotonic=self.last_disk,
                      **counters, **self.disk)
        with (self.root / 'metrics.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(sample) + '\n')
        self.samples += 1
        self.last = now
        # Diagnostic guardrails, not a substitute for reviewing per-process trends.
        assert counters['privateBytes'] < 1024**3, 'Service private memory exceeds 1 GiB'
        assert counters['handles'] < 5000, 'Service handles exceed 5000'
        assert self.disk['logBytes'] < 2 * 1024**3, 'Test logs exceed 2 GiB'
        assert self.disk['evidenceBytes'] < 8 * 1024**3, 'Test evidence exceeds 8 GiB'
        return sample
