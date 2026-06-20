"""归一化事件的内存存储，线程安全，按 seq 去重排序。"""
import threading


class EventStore:
    def __init__(self):
        self._lock = threading.Lock()
        self._events = []
        self._seqs = set()

    @staticmethod
    def _key(e):
        seq = e.get("seq")
        return seq if seq is not None else float("inf")

    def append(self, event):
        """按 seq 去重；返回是否新增。seq 为 None 时不去重。"""
        with self._lock:
            seq = event.get("seq")
            if seq is not None and seq in self._seqs:
                return False
            if seq is not None:
                self._seqs.add(seq)
            self._events.append(event)
            self._events.sort(key=self._key)
            return True

    def all(self):
        with self._lock:
            return list(self._events)

    def since(self, seq):
        with self._lock:
            if seq is None:
                return list(self._events)
            return [e for e in self._events if (e.get("seq") or 0) > seq]

    def last_seq(self):
        with self._lock:
            return self._events[-1].get("seq") if self._events else None
