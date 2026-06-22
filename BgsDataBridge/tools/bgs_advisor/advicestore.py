"""按 decisionType 存最近建议,线程安全,带 monotonic ts 供增量拉取。纯逻辑。"""
import threading


class AdviceStore:
    def __init__(self):
        self._lock = threading.Lock()
        self._latest = {}     # decisionType -> Advice
        self._counter = 0

    def put(self, advice):
        with self._lock:
            self._counter += 1
            advice.ts = self._counter
            self._latest[advice.decisionType] = advice
        return advice

    def latest(self, decisionType=None):
        with self._lock:
            if decisionType is None:
                advs = list(self._latest.values())
                return max(advs, key=lambda a: a.ts) if advs else None
            return self._latest.get(decisionType)

    def since(self, ts):
        with self._lock:
            advs = [a for a in self._latest.values() if a.ts > ts]
        advs.sort(key=lambda a: a.ts)
        return advs

    def all(self):
        with self._lock:
            advs = list(self._latest.values())
        advs.sort(key=lambda a: a.ts)
        return advs
