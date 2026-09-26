import re
import argparse
from pathlib import Path
from collections import defaultdict

import pandas as pd
import pdfplumber

OUTPUT_COLUMNS = [
    "granularity", "row_type", "source_file",
    "game_no", "game_date", "start_time", "matchup",
    "team_code", "team_name",
    "team_score", "opponent_name", "opponent_score",
    "player_no", "player_name", "status",
    "is_starter", "is_captain",
    "games_listed", "games_played", "starts",
    "min",
    "2p_ma", "3p_ma", "ft_ma",
    "or", "dr", "reb",
    "ast", "to", "stl", "blk",
    "pf", "fd", "eff", "pts",
    "team_or", "team_dr", "team_reb", "team_pf", "team_fd",
]

STAT_COLS = ["or", "dr", "reb", "ast", "to", "stl", "blk", "pf", "fd", "eff", "pts"]
NUMERIC_COLS = [
    "is_starter", "is_captain", "games_listed", "games_played", "starts",
    "team_score", "opponent_score",
    "or", "dr", "reb", "ast", "to", "stl", "blk", "pf", "fd", "eff", "pts",
    "team_or", "team_dr", "team_reb", "team_pf", "team_fd",
]

MONTHS = {
    "Jan": "Jan", "January": "Jan",
    "Feb": "Feb", "February": "Feb",
    "Mar": "Mar", "March": "Mar",
    "Apr": "Apr", "April": "Apr",
    "May": "May",
    "Jun": "Jun", "June": "Jun",
    "Jul": "Jul", "July": "Jul",
    "Aug": "Aug", "August": "Aug",
    "Sep": "Sep", "September": "Sep",
    "Oct": "Oct", "October": "Oct",
    "Nov": "Nov", "November": "Nov",
    "Dec": "Dec", "December": "Dec",
}


def clean_text(text: str) -> str:
    text = text.replace("​", " ")
    text = text.replace("﻿", " ")
    text = text.replace("–", "-")
    text = text.replace("–", "-")
    text = text.replace("—", "-")
    text = re.sub(r"[ \t]+", " ", text)
    return text.strip()


def read_pdf_text(pdf_path: Path) -> str:
    parts = []
    with pdfplumber.open(pdf_path) as pdf:
        for page in pdf.pages:
            page_text = page.extract_text(x_tolerance=1, y_tolerance=3) or ""
            parts.append(page_text)
    return clean_text("\n".join(parts))


def normalize_date(date_text: str) -> str:
    date_text = clean_text(date_text)
    m = re.search(r"(\d{1,2})\s+([A-Za-z]+)\s+(\d{4})", date_text)
    if not m:
        return ""
    day = m.group(1).zfill(2)
    mon = MONTHS.get(m.group(2), m.group(2)[:3])
    year = m.group(3)
    return f"{day} {mon} {year}"


def extract_game_meta(text: str, source_name: str) -> dict:
    game_no = ""
    m = re.search(r"Game\s*No\.?:\s*([A-Z0-9-]+)", text, flags=re.I)
    if m:
        game_no = m.group(1).strip()

    game_date = ""
    start_time = ""
    m = re.search(
        r"Borg Elarab Arena,\s*([^\n]+?)\s+Start time:\s*(\d{1,2}:\d{2})",
        text,
        flags=re.I,
    )
    if m:
        game_date = normalize_date(m.group(1))
        start_time = m.group(2).strip()

    matchup = ""
    teams = []
    scores = []
    m = re.search(r"\n\s*([A-Za-z0-9 ]+?)\s+(\d+)\s*-\s*(\d+)\s+([A-Za-z0-9 ]+?)\s*\n", text)
    if m:
        team1 = clean_text(m.group(1))
        team2 = clean_text(m.group(4))
        teams = [team1, team2]
        scores = [int(m.group(2)), int(m.group(3))]
        matchup = f"{team1} vs {team2}"
    else:
        matchup = Path(source_name).stem

    return {
        "game_no": game_no,
        "game_date": game_date,
        "start_time": start_time,
        "matchup": matchup,
        "teams": teams,
        "scores": scores,
    }


def zero_stats() -> dict:
    row = {col: 0 for col in STAT_COLS}
    row.update({"2p_ma": "0/0", "3p_ma": "0/0", "ft_ma": "0/0"})
    return row


def parse_team_header(line: str):
    # Codes can contain digits (e.g. "U23"), so accept 2-4 alphanumerics.
    m = re.search(r"^(.+?)\s*\(([A-Z0-9]{2,4})\)\s*Assistant Coach", line, flags=re.I)
    if not m:
        return None
    return clean_text(m.group(1)), m.group(2).upper()


def extract_team_headers(text: str) -> list[tuple[str, str]]:
    """All team headers in document order, e.g. [("Mali", "MLI"), ("U23", "U23")]."""
    results = []
    for m in re.finditer(
        r"(?m)^(.+?)\s*\(([A-Z0-9]{2,4})\)\s*\S*\s*Assistant Coach",
        text,
    ):
        name = clean_text(m.group(1))
        code = m.group(2).upper()
        if name:
            results.append((name, code))
    return results


def infer_team_code(team_name: str, fallback_index: int) -> str:
    code = re.sub(r"[^A-Za-z0-9]", "", team_name).upper()
    if code:
        return code[:3]
    return f"T{fallback_index + 1}"


def inferred_team(meta: dict, team_index: int) -> tuple[str, str]:
    teams = meta.get("teams") or []
    if team_index < len(teams):
        team_name = teams[team_index]
    else:
        team_name = f"Team {team_index + 1}"
    return team_name, infer_team_code(team_name, team_index)


def normalized_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "", value or "").upper()


def score_context(meta: dict, team_name: str) -> dict:
    teams = meta.get("teams") or []
    scores = meta.get("scores") or []
    if len(teams) < 2 or len(scores) < 2:
        return {"team_score": None, "opponent_name": "", "opponent_score": None}

    normalized_team_name = normalized_name(team_name)
    normalized_teams = [normalized_name(team) for team in teams]
    team_index = 0
    for index, candidate in enumerate(normalized_teams):
        if candidate and (
            candidate == normalized_team_name
            or candidate in normalized_team_name
            or normalized_team_name in candidate
        ):
            team_index = index
            break

    opponent_index = 1 - team_index
    return {
        "team_score": scores[team_index],
        "opponent_name": teams[opponent_index],
        "opponent_score": scores[opponent_index],
    }


def parse_team_coach_line(line: str) -> dict:
    nums = re.findall(r"-?\d+", line)
    nums = [int(x) for x in nums]
    while len(nums) < 5:
        nums.append(0)
    return {
        "team_or": nums[0],
        "team_dr": nums[1],
        "team_reb": nums[2],
        "team_pf": nums[3],
        "team_fd": nums[4],
    }


def parse_name_flags(raw_name: str):
    is_captain = 1 if re.search(r"\(C\)", raw_name, flags=re.I) else 0
    name = re.sub(r"\(C\)", "", raw_name, flags=re.I)
    name = clean_text(name)
    return name, is_captain


def parse_played_line(line: str):
    m = re.match(r"^(\*?)(\d+)\s+(.+?)\s+(\d{1,3}:\d{2})\s+(.+)$", line)
    if not m:
        return None

    starter_mark, player_no, raw_name, minutes, rest = m.groups()
    tokens = rest.split()

    if len(tokens) < 19:
        return None

    name, is_captain = parse_name_flags(raw_name)
    tail = tokens[8:]
    if len(tail) < 11:
        return None

    row = {
        "player_no": int(player_no),
        "player_name": name,
        "status": "PLAYED",
        "is_starter": 1 if starter_mark == "*" else 0,
        "is_captain": is_captain,
        "min": minutes,
        "2p_ma": tokens[2],
        "3p_ma": tokens[4],
        "ft_ma": tokens[6],
        "or": int(tail[0]),
        "dr": int(tail[1]),
        "reb": int(tail[2]),
        "ast": int(tail[3]),
        "to": int(tail[4]),
        "stl": int(tail[5]),
        "blk": int(tail[6]),
        "pf": int(tail[7]),
        "fd": int(tail[8]),
        "eff": int(tail[-2]),
        "pts": int(tail[-1]),
        "team_or": 0,
        "team_dr": 0,
        "team_reb": 0,
        "team_pf": 0,
        "team_fd": 0,
    }
    return row


def parse_dnp_line(line: str):
    m = re.match(r"^(\*?)(\d+)\s+(.+?)\s+DNP\s*$", line, flags=re.I)
    if not m:
        return None

    starter_mark, player_no, raw_name = m.groups()
    name, is_captain = parse_name_flags(raw_name)

    row = {
        "player_no": int(player_no),
        "player_name": name,
        "status": "DNP",
        "is_starter": 1 if starter_mark == "*" else 0,
        "is_captain": is_captain,
        "min": "00:00",
        "team_or": 0,
        "team_dr": 0,
        "team_reb": 0,
        "team_pf": 0,
        "team_fd": 0,
    }
    row.update(zero_stats())
    return row


def parse_totals_line(line: str, team_coach_stats: dict):
    m = re.match(r"^Totals\s+(\d{1,3}:\d{2})\s+(.+)$", line, flags=re.I)
    if not m:
        return None

    minutes, rest = m.groups()
    tokens = rest.split()
    if len(tokens) < 19:
        return None
    tail = tokens[8:]
    if len(tail) < 11:
        return None

    row = {
        "player_no": 50,
        "player_name": "TEAM TOTALS",
        "status": "PLAYED",
        "is_starter": 0,
        "is_captain": 0,
        "min": minutes,
        "2p_ma": tokens[2],
        "3p_ma": tokens[4],
        "ft_ma": tokens[6],
        "or": int(tail[0]),
        "dr": int(tail[1]),
        "reb": int(tail[2]),
        "ast": int(tail[3]),
        "to": int(tail[4]),
        "stl": int(tail[5]),
        "blk": int(tail[6]),
        "pf": int(tail[7]),
        "fd": int(tail[8]),
        "eff": int(tail[-2]),
        "pts": int(tail[-1]),
    }
    row.update(team_coach_stats)
    return row


def base_row(meta: dict, source_file: str, team_name: str, team_code: str) -> dict:
    row = {
        "source_file": source_file,
        "game_no": meta.get("game_no", ""),
        "game_date": meta.get("game_date", ""),
        "start_time": meta.get("start_time", ""),
        "matchup": meta.get("matchup", ""),
        "team_code": team_code,
        "team_name": team_name,
        "games_listed": 1,
        "games_played": 1,
        "starts": 0,
    }
    row.update(score_context(meta, team_name))
    return row


# ── Grid (table) based extraction ──────────────────────────────────────────
#
# FIBA box-score PDFs are real bordered tables. Player names wrap onto two
# physical lines ("Soumaila / Sissouma"), which scrambles plain text
# extraction and pushes each row's stats onto a neighbouring line. Reading the
# table by its ruling lines (pdfplumber.extract_tables) sidesteps all of that:
# every value is read from its own grid cell regardless of how text wraps.

TABLE_SETTINGS = {
    "vertical_strategy": "lines",
    "horizontal_strategy": "lines",
    "snap_tolerance": 4,
}

def clean_cell(value) -> str:
    if value is None:
        return ""
    return clean_text(str(value).replace("\n", " "))


def int_or_zero(value) -> int:
    m = re.search(r"-?\d+", str(value))
    return int(m.group(0)) if m else 0


def ma_or_default(value) -> str:
    m = re.match(r"^\s*(\d+)\s*/\s*(\d+)\s*$", str(value))
    return f"{int(m.group(1))}/{int(m.group(2))}" if m else "0/0"


def _is_number_cell(value: str) -> bool:
    return bool(re.match(r"^\*?\s*\d{1,3}$", value))


def _is_dnp(cells: list[str]) -> bool:
    return any(c.strip().upper() == "DNP" for c in cells)


# Single-token header codes -> output stat key.
_HEADER_CODE_MAP = {
    "or": "or", "dr": "dr", "tot": "reb",
    "as": "ast", "to": "to", "st": "stl", "bs": "blk",
    "pf": "pf", "fd": "fd", "ef": "eff", "pts": "pts",
}


def classify_header(label: str) -> str | None:
    """Map a column's combined header text to an output key, or None to ignore.

    Reading the header (rather than assuming fixed column positions) keeps the
    parser correct even when a table is clipped at the page edge or its columns
    shift — missing columns simply resolve to nothing and default to 0."""
    t = label.lower().strip()
    if not t:
        return None
    has_ma = "m/a" in t
    if "name" in t:
        return "name"
    if t == "no" or "playing number" in t:
        return "no"
    if "min" in t:
        return "min"
    if has_ma:
        if "2 point" in t or "2point" in t or "2pt" in t:
            return "2p_ma"
        if "3 point" in t or "3point" in t or "3pt" in t:
            return "3p_ma"
        if "free throw" in t or "freethrow" in t:
            return "ft_ma"
        return None  # Field-goal M/A is the 2P+3P combined column; not stored.
    if "%" in t:
        return None
    return _HEADER_CODE_MAP.get(t)


def build_colmap(header_rows: list[list[str]]) -> dict:
    """Combine the (possibly multi-line) header rows column-by-column and map
    each column index to an output key."""
    width = max((len(r) for r in header_rows), default=0)
    colmap: dict[str, int] = {}
    for col in range(width):
        parts = []
        for row in header_rows:
            if col < len(row) and row[col].strip():
                parts.append(row[col].strip())
        key = classify_header(" ".join(parts))
        if key and key not in colmap:
            colmap[key] = col
    return colmap


_STAT_KEYS = [
    "2p_ma", "3p_ma", "ft_ma",
    "or", "dr", "reb", "ast", "to", "stl", "blk", "pf", "fd", "eff", "pts",
]
_MA_KEYS = {"2p_ma", "3p_ma", "ft_ma"}


def _read_stats(cells: list[str], colmap: dict) -> dict:
    out = {}
    for key in _STAT_KEYS:
        col = colmap.get(key)
        raw = cells[col] if (col is not None and 0 <= col < len(cells)) else ""
        if key in _MA_KEYS:
            out[key] = ma_or_default(raw)
        else:
            out[key] = int_or_zero(raw)
    return out


def map_player_row(cells: list[str], colmap: dict) -> dict | None:
    no_col = colmap.get("no", 0)
    name_col = colmap.get("name", 1)
    min_col = colmap.get("min", 2)
    no_cell = (cells[no_col] if no_col < len(cells) else "").strip()
    if not _is_number_cell(no_cell):
        return None

    starter = "*" in no_cell
    player_no = int_or_zero(no_cell)
    raw_name = cells[name_col] if name_col < len(cells) else ""
    name, is_captain = parse_name_flags(raw_name)
    # Guard against summary/comparison tables whose first column is numeric:
    # a real player always has a non-numeric name.
    if not name or name.replace(" ", "").isdigit():
        return None
    minutes = (cells[min_col] if min_col < len(cells) else "").strip()

    base = {
        "player_no": player_no,
        "player_name": name,
        "is_starter": 1 if starter else 0,
        "is_captain": is_captain,
        "team_or": 0, "team_dr": 0, "team_reb": 0, "team_pf": 0, "team_fd": 0,
    }

    if _is_dnp(cells) or minutes.upper() == "DNP":
        base.update({"status": "DNP", "min": "00:00"})
        base.update(zero_stats())
        return base

    base.update({"status": "PLAYED", "min": minutes or "00:00"})
    base.update(_read_stats(cells, colmap))
    return base


def map_totals_row(cells: list[str], colmap: dict, team_coach_stats: dict) -> dict:
    min_col = colmap.get("min", 2)
    row = {
        "player_no": 50,
        "player_name": "TEAM TOTALS",
        "status": "PLAYED",
        "is_starter": 0,
        "is_captain": 0,
        "min": (cells[min_col] if min_col < len(cells) else "").strip() or "200:00",
    }
    row.update(_read_stats(cells, colmap))
    row.update(team_coach_stats)
    return row


def map_team_coach_row(cells: list[str], colmap: dict) -> dict:
    stats = _read_stats(cells, colmap)
    return {
        "team_or": stats["or"],
        "team_dr": stats["dr"],
        "team_reb": stats["reb"],
        "team_pf": stats["pf"],
        "team_fd": stats["fd"],
    }


def _row_kind(cells: list[str]) -> str:
    first = cells[0].strip().lower() if cells else ""
    if first.startswith("team/coach") or first.startswith("team / coach"):
        return "team_coach"
    if first.startswith("totals"):
        return "totals"
    # A player row's first cell is a (possibly starred) jersey number.
    if _is_number_cell(cells[0].strip()):
        return "player"
    if not cells[0].strip() and len(cells) > 1 and _is_number_cell(cells[1].strip()):
        return "player"
    return "header"


def extract_pdf_tables(pdf_path: Path) -> list[dict]:
    """Grid-based extraction. Returns [] if no player tables are found so the
    caller can fall back to text-mode parsing."""
    rows: list[dict] = []

    with pdfplumber.open(pdf_path) as pdf:
        full_text = clean_text(
            "\n".join(
                (page.extract_text(x_tolerance=1, y_tolerance=3) or "")
                for page in pdf.pages
            )
        )
        meta = extract_game_meta(full_text, pdf_path.name)
        headers = extract_team_headers(full_text)

        tables = []
        for page in pdf.pages:
            for table in page.extract_tables(TABLE_SETTINGS):
                tables.append(table)

        team_index = 0

        def current_identity() -> tuple[str, str]:
            if team_index < len(headers):
                return headers[team_index]
            return inferred_team(meta, team_index)

        team_coach_stats = {
            "team_or": 0, "team_dr": 0, "team_reb": 0, "team_pf": 0, "team_fd": 0
        }
        # Reused across pages: a team's table can split over two pages, and the
        # continuation page carries no header of its own.
        colmap: dict = {}

        for table in tables:
            cell_rows = [[clean_cell(c) for c in raw] for raw in table]
            cell_rows = [r for r in cell_rows if any(c for c in r)]
            if not cell_rows:
                continue

            # Collect the leading header rows (everything before the first data
            # row) and (re)build the column map from them. If this table has no
            # header (a continuation page), keep the previous map.
            header_rows = []
            for r in cell_rows:
                if _row_kind(r) == "header":
                    header_rows.append(r)
                else:
                    break
            if header_rows:
                new_map = build_colmap(header_rows)
                if "min" in new_map or "pts" in new_map:
                    colmap = new_map
            if not colmap:
                continue  # Not a player table (e.g. the summary tables).

            for cells in cell_rows:
                kind = _row_kind(cells)

                if kind == "team_coach":
                    team_coach_stats = map_team_coach_row(cells, colmap)
                    continue

                if kind == "totals":
                    parsed = map_totals_row(cells, colmap, team_coach_stats)
                    team_name, team_code = current_identity()
                    row = base_row(meta, pdf_path.name, team_name, team_code)
                    row.update(parsed)
                    row["granularity"] = "game_team_total"
                    row["row_type"] = "team_total"
                    rows.append(row)
                    team_index += 1
                    team_coach_stats = {
                        "team_or": 0, "team_dr": 0, "team_reb": 0,
                        "team_pf": 0, "team_fd": 0,
                    }
                    continue

                if kind == "player":
                    parsed = map_player_row(cells, colmap)
                    if not parsed:
                        continue
                    team_name, team_code = current_identity()
                    row = base_row(meta, pdf_path.name, team_name, team_code)
                    row.update(parsed)
                    row["granularity"] = "game_player"
                    row["row_type"] = "player"
                    row["games_played"] = (
                        1 if row["status"] == "PLAYED" and row["min"] != "00:00" else 0
                    )
                    row["starts"] = row["is_starter"]
                    rows.append(row)

    if not any(r.get("row_type") == "player" for r in rows):
        return []
    return rows


def extract_pdf(pdf_path: Path) -> list[dict]:
    """Grid-based extraction first; fall back to legacy text parsing only if the
    table reader finds no player rows (e.g. a borderless/scanned PDF)."""
    try:
        grid_rows = extract_pdf_tables(pdf_path)
    except Exception:
        grid_rows = []
    if grid_rows:
        return grid_rows
    return extract_pdf_textmode(pdf_path)


def extract_pdf_textmode(pdf_path: Path) -> list[dict]:
    text = read_pdf_text(pdf_path)
    meta = extract_game_meta(text, pdf_path.name)
    lines = [clean_text(line) for line in text.splitlines()]
    lines = [line for line in lines if line]

    rows = []
    team_index = 0
    current_team_name = None
    current_team_code = None
    in_team_table = False
    last_team_coach_stats = {"team_or": 0, "team_dr": 0, "team_reb": 0, "team_pf": 0, "team_fd": 0}

    for line in lines:
        team_header = parse_team_header(line)
        if team_header:
            current_team_name, current_team_code = team_header
            in_team_table = True
            last_team_coach_stats = {"team_or": 0, "team_dr": 0, "team_reb": 0, "team_pf": 0, "team_fd": 0}
            continue

        if line.startswith("Coach:") and not in_team_table:
            current_team_name, current_team_code = inferred_team(meta, team_index)
            last_team_coach_stats = {"team_or": 0, "team_dr": 0, "team_reb": 0, "team_pf": 0, "team_fd": 0}
            continue

        if line.startswith("No Name"):
            if not current_team_name or not current_team_code:
                current_team_name, current_team_code = inferred_team(meta, team_index)
            in_team_table = True
            continue

        if not in_team_table or not current_team_code:
            continue

        if line.startswith("Team/Coach"):
            last_team_coach_stats = parse_team_coach_line(line)
            continue

        if line.startswith("Totals"):
            parsed = parse_totals_line(line, last_team_coach_stats)
            if parsed:
                row = base_row(meta, pdf_path.name, current_team_name, current_team_code)
                row.update(parsed)
                row["granularity"] = "game_team_total"
                row["row_type"] = "team_total"
                rows.append(row)
            in_team_table = False
            current_team_name = None
            current_team_code = None
            team_index += 1
            continue

        if line == "(C)" and rows and rows[-1].get("team_name") == current_team_name:
            rows[-1]["is_captain"] = 1
            continue

        if any(line.startswith(x) for x in ["Field Goals", "No Name", "M/A %", "Coach:", "Fouls"]):
            continue

        parsed = parse_played_line(line)
        if parsed is None:
            parsed = parse_dnp_line(line)

        if parsed:
            row = base_row(meta, pdf_path.name, current_team_name, current_team_code)
            row.update(parsed)
            row["granularity"] = "game_player"
            row["row_type"] = "player"
            row["games_played"] = 1 if row["status"] == "PLAYED" and row["min"] != "00:00" else 0
            row["starts"] = row["is_starter"]
            rows.append(row)

    return rows


def ma_to_pair(value):
    m = re.match(r"^(\d+)\s*/\s*(\d+)$", str(value).strip())
    if not m:
        return 0, 0
    return int(m.group(1)), int(m.group(2))


def sum_ma(series) -> str:
    made = 0
    attempts = 0
    for value in series.dropna():
        m, a = ma_to_pair(value)
        made += m
        attempts += a
    return f"{made}/{attempts}"


def min_to_seconds(value) -> int:
    m = re.match(r"^(\d+):(\d{2})$", str(value).strip())
    if not m:
        return 0
    return int(m.group(1)) * 60 + int(m.group(2))


def seconds_to_min(seconds: int) -> str:
    return f"{seconds // 60}:{seconds % 60:02d}"


def normalize_df(df: pd.DataFrame) -> pd.DataFrame:
    for col in OUTPUT_COLUMNS:
        if col not in df.columns:
            df[col] = 0 if col in NUMERIC_COLS else ""

    df = df[OUTPUT_COLUMNS].copy()

    for col in NUMERIC_COLS:
        df[col] = pd.to_numeric(df[col], errors="coerce").fillna(0).astype(int)

    for col in ["game_no", "player_no"]:
        df[col] = df[col].astype(str).replace("nan", "")

    return df


def add_cumulative_rows(game_df: pd.DataFrame) -> pd.DataFrame:
    cumulative_rows = []

    players = game_df[game_df["granularity"] == "game_player"].copy()
    if not players.empty:
        group_cols = ["team_code", "team_name", "player_no", "player_name"]
        for keys, group in players.groupby(group_cols, dropna=False):
            team_code, team_name, player_no, player_name = keys
            row = {
                "granularity": "cumulative_player",
                "row_type": "player",
                "source_file": "CUMULATIVE",
                "game_no": "ALL",
                "game_date": "ALL",
                "start_time": "",
                "matchup": "ALL GAMES",
                "team_code": team_code,
                "team_name": team_name,
                "player_no": player_no,
                "player_name": player_name,
                "status": "CUMULATIVE",
                "is_starter": 0,
                "is_captain": int(group["is_captain"].max()),
                "games_listed": int(group["game_no"].nunique()),
                "games_played": int((group["min"].apply(min_to_seconds) > 0).sum()),
                "starts": int(group["is_starter"].sum()),
                "min": seconds_to_min(int(group["min"].apply(min_to_seconds).sum())),
                "2p_ma": sum_ma(group["2p_ma"]),
                "3p_ma": sum_ma(group["3p_ma"]),
                "ft_ma": sum_ma(group["ft_ma"]),
                "team_or": 0,
                "team_dr": 0,
                "team_reb": 0,
                "team_pf": 0,
                "team_fd": 0,
            }
            for col in STAT_COLS:
                row[col] = int(pd.to_numeric(group[col], errors="coerce").fillna(0).sum())
            cumulative_rows.append(row)

    teams = game_df[game_df["granularity"] == "game_team_total"].copy()
    if not teams.empty:
        for keys, group in teams.groupby(["team_code", "team_name"], dropna=False):
            team_code, team_name = keys
            row = {
                "granularity": "cumulative_team_total",
                "row_type": "team_total",
                "source_file": "CUMULATIVE",
                "game_no": "ALL",
                "game_date": "ALL",
                "start_time": "",
                "matchup": "ALL GAMES",
                "team_code": team_code,
                "team_name": team_name,
                "player_no": 50,
                "player_name": "TEAM TOTALS",
                "status": "CUMULATIVE",
                "is_starter": 0,
                "is_captain": 0,
                "games_listed": int(group["game_no"].nunique()),
                "games_played": int(group["game_no"].nunique()),
                "starts": 0,
                "min": seconds_to_min(int(group["min"].apply(min_to_seconds).sum())),
                "2p_ma": sum_ma(group["2p_ma"]),
                "3p_ma": sum_ma(group["3p_ma"]),
                "ft_ma": sum_ma(group["ft_ma"]),
                "team_or": 0,
                "team_dr": 0,
                "team_reb": 0,
                "team_pf": 0,
                "team_fd": 0,
            }
            for col in STAT_COLS:
                row[col] = int(pd.to_numeric(group[col], errors="coerce").fillna(0).sum())
            cumulative_rows.append(row)

    if not cumulative_rows:
        return pd.DataFrame(columns=OUTPUT_COLUMNS)

    return normalize_df(pd.DataFrame(cumulative_rows))


def extract_folder(pdf_dir: str, output_csv: str) -> pd.DataFrame:
    pdf_dir = Path(pdf_dir)
    pdf_files = sorted(pdf_dir.glob("*.pdf"))

    if not pdf_files:
        raise FileNotFoundError(f"No PDF files found in: {pdf_dir}")

    all_rows = []
    for pdf_path in pdf_files:
        rows = extract_pdf(pdf_path)
        print(f"{pdf_path.name}: extracted {len(rows)} rows")
        all_rows.extend(rows)

    game_df = normalize_df(pd.DataFrame(all_rows))
    cumulative_df = add_cumulative_rows(game_df)
    final_df = normalize_df(pd.concat([game_df, cumulative_df], ignore_index=True))

    final_df.to_csv(output_csv, index=False, encoding="utf-8-sig")
    print(f"Saved: {output_csv}")
    return final_df


def main():
    parser = argparse.ArgumentParser(description="Extract FIBA box score PDFs to CSV.")
    parser.add_argument("--pdf_dir", required=True, help="Folder containing PDF files")
    parser.add_argument("--output_csv", default="fiba_box_scores_extracted_and_cumulative.csv")
    args = parser.parse_args()

    extract_folder(args.pdf_dir, args.output_csv)


if __name__ == "__main__":
    main()
