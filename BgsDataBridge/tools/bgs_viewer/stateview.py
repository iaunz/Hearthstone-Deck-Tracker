"""对归一化事件列表做 fold：重建快照视图 + 计算回合进度。"""


def _fold_events(events):
    """yield (event, view_after_event)；view 为累积的快照视图。"""
    view = {}
    for e in events:
        data = e.get("data") or {}
        if isinstance(data, dict) and "player" in data:
            view = dict(data)
        elif isinstance(data, dict) and "shop" in data and "player" not in data:
            view = dict(view)
            view["shop"] = data.get("shop")
        yield e, view


def reconstruct_state(events):
    view = {}
    for _, v in _fold_events(events):
        view = v
    return view


def compute_progression(events):
    out = []
    for e, view in _fold_events(events):
        if e.get("type") != "CombatPhaseStart" or not view:
            continue
        player = view.get("player") or {}
        hero = player.get("hero") or {}
        board = player.get("board") or []
        turn = (view.get("match") or {}).get("turn")
        if turn is None:
            turn = (e.get("match") or {}).get("turn")
        out.append({
            "turn": turn,
            "heroHp": hero.get("health"),
            "boardAtk": sum((m.get("attack") or 0) for m in board),
            "tier": player.get("tier"),
        })
    return out
