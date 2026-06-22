"""快照 + 启发式 findings → LLM prompt(system+user)。纯逻辑。"""
SYSTEM = """你是酒馆战棋(Battlegrounds)军师。根据当前对局状态给出**类型化的行动建议**。
你必须只返回一个 JSON 对象,不要任何额外文字,结构为:
{"rationale": "一句话大局观", "actions": [{"kind": "...", "cardId": "...", "name": "...", "index": null, "note": "..."}]}
actions[].kind 按决策类型取值:
- ShopPhase: BUY | SELL | PLAY | REPOSITION | LEVEL_UP | REROLL | FREEZE | HERO_POWER
- Positioning: PLACE (带 index,给出完整排序,0=最左)
- HeroPick: PICK_HERO ; TrinketPick: PICK_TRINKET
你只建议,不假设玩家会自动执行。"""


def _minion(m):
    name = m.get("name") or m.get("cardId") or "?"
    atk = m.get("attack")
    hp = m.get("health")
    stats = f"{atk}/{hp}" if atk is not None and hp is not None else ""
    kws = " ".join(m.get("keywords") or [])
    return f"{name} {stats}" + (f" [{kws}]" if kws else "")


def _list(items):
    return ", ".join(_minion(m) for m in (items or [])) or "(空)"


def encode_prompt(snapshot: dict, dtype: str, findings: list) -> tuple:
    match = snapshot.get("match") or {}
    player = snapshot.get("player") or {}
    shop = snapshot.get("shop") or {}
    anomaly = match.get("anomaly")
    hero = player.get("hero") or {}

    lines = [f"决策类型: {dtype}", f"回合: {match.get('turn')}  相位: {match.get('phase')}"]
    if anomaly:
        lines.append(f"畸变: {anomaly.get('name')} ({anomaly.get('text')})")
    races = snapshot.get("availableRaces") or []
    if races:
        lines.append("可用种族: " + ", ".join(races))
    lines.append(f"酒馆等级(tier): {player.get('tier')}")
    if hero:
        lines.append(f"英雄: {hero.get('name')} {hero.get('health')}hp" +
                     (f" +{hero['armor']}" if hero.get("armor") else ""))
    hp_ = player.get("heroPower")
    if hp_:
        lines.append(f"英雄技能: {hp_.get('name')} ({hp_.get('text')})")
    for t in player.get("trinkets") or []:
        lines.append(f"饰品[{t.get('slot')}]: {t.get('name')} ({t.get('text')})")
    qr = player.get("questReward")
    if qr:
        prog = qr.get("progress"); tot = qr.get("total")
        lines.append(f"任务奖励: {qr.get('name')} 进度 {prog}/{tot}")
    lines.append("你的阵容: " + _list(player.get("board")))
    lines.append("手牌: " + _list(player.get("hand")))
    if shop and shop.get("available"):
        lines.append(f"商店(tier {shop.get('tier')}): " + _list(shop.get("offers")))
    if findings:
        lines.append("启发式提示(请据此校准建议): " + "; ".join(f.message for f in findings))
    lines.append(f"\n请针对 {dtype} 返回上述 JSON。")
    return SYSTEM, "\n".join(lines)
