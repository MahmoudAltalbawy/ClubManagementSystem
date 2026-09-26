from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import pandas as pd
import pdfplumber


def _atomic_write_csv(df: "pd.DataFrame", path: Path) -> None:
    """Write a DataFrame to CSV atomically (Phase 2.5d).

    Ingest rewrites these CSVs while /ask requests read them; a direct to_csv()
    truncates then streams, so a concurrent reader can see a half-written file.
    Writing to a temp file in the same directory and then os.replace()-ing it into
    place makes the swap atomic — a reader sees either the old file or the new one,
    never a partial one.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f"{path.name}.tmp.{os.getpid()}")
    try:
        df.to_csv(tmp, index=False)
        os.replace(tmp, path)
    finally:
        if tmp.exists():
            try:
                tmp.unlink()
            except OSError:
                pass


DATA_PDF_DIR = Path("data/pdfs")
LEGACY_PDF_DIR = Path("pdfs")
EXTRACTED_DIR = Path("extracted")
BOX_SCORE_CSV = EXTRACTED_DIR / "players_box_scores.csv"
CHUNKS_CSV = EXTRACTED_DIR / "pdf_chunks.csv"

REQUIRED_PLAYER_COLUMNS = [
    "match_name",
    "date",
    "team",
    "opponent",
    "player_name",
    "minutes",
    "fg_made",
    "fg_attempted",
    "two_made",
    "two_attempted",
    "three_made",
    "three_attempted",
    "ft_made",
    "ft_attempted",
    "offensive_rebounds",
    "defensive_rebounds",
    "total_rebounds",
    "assists",
    "turnovers",
    "steals",
    "blocks",
    "personal_fouls",
    "fouls_drawn",
    "plus_minus",
    "efficiency",
    "points",
]

PLAYER_ROW_RE = re.compile(
    r"^(?P<number>\*?\d+)\s+"
    r"(?P<name>.+?)\s+"
    r"(?P<minutes>\d{1,3}:\d{2})\s+"
    r"(?P<fg>\d+/\d+)\s+(?P<fg_pct>\d+(?:\.\d+)?)\s+"
    r"(?P<two>\d+/\d+)\s+(?P<two_pct>\d+(?:\.\d+)?)\s+"
    r"(?P<three>\d+/\d+)\s+(?P<three_pct>\d+(?:\.\d+)?)\s+"
    r"(?P<ft>\d+/\d+)\s+(?P<ft_pct>\d+(?:\.\d+)?)\s+"
    r"(?P<oreb>-?\d+)\s+(?P<dreb>-?\d+)\s+(?P<treb>-?\d+)\s+"
    r"(?P<ast>-?\d+)\s+(?P<tov>-?\d+)\s+(?P<stl>-?\d+)\s+(?P<blk>-?\d+)\s+"
    r"(?P<pf>-?\d+)\s+(?P<fd>-?\d+)\s+(?P<pm>[+-]?\d+)\s+"
    r"(?P<eff>-?\d+)\s+(?P<pts>-?\d+)$"
)

TEAM_HEADER_RE = re.compile(r"^(?P<team_name>.+?)\s+\((?P<code>[A-Z]{3})\)(?:\s|$)")
SCORE_LINE_RE = re.compile(
    r"^(?P<home>[A-Za-z][A-Za-z .'-]+?)\s+(?P<home_score>\d+)\s+[-\u2013]\s+"
    r"(?P<away_score>\d+)\s+(?P<away>[A-Za-z][A-Za-z .'-]+?)$",
    re.MULTILINE,
)
DATE_RE = re.compile(
    r"\b(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)\s+(?P<date>\d{1,2}\s+[A-Za-z]+\s+\d{4})\b"
)

TEAM_NAME_TO_CODE = {
    "egypt": "EGY",
    "angola": "ANG",
    "mali": "MLI",
    "uganda": "UGA",
}


@dataclass(frozen=True)
class PdfPageText:
    source_pdf: str
    page_number: int
    text: str
    report_type: str
    match_name: str


def clean_text(text: str) -> str:
    return (
        text.replace("\u200b", "")
        .replace("\ufeff", "")
        .replace("\u2013", "-")
        .replace("\u2014", "-")
        .strip()
    )


def list_pdf_files(pdf_dir: Path = DATA_PDF_DIR, include_legacy: bool = True) -> list[Path]:
    """Read PDFs from a project folder, optionally including the starter repo's legacy pdfs/ folder."""
    pdf_dir.mkdir(parents=True, exist_ok=True)
    seen: set[str] = set()
    files: list[Path] = []
    folders = [pdf_dir]
    if include_legacy:
        folders.append(LEGACY_PDF_DIR)
    for folder in folders:
        if not folder.exists():
            continue
        for pdf_path in sorted(folder.glob("*.pdf")):
            key = pdf_path.name.lower()
            if key not in seen:
                files.append(pdf_path)
                seen.add(key)
    return files


def detect_report_type(pdf_name: str) -> str:
    lowered = pdf_name.lower()
    if "box score" in lowered:
        return "FIBA Box Score"
    if "line up" in lowered or "lineup" in lowered:
        return "Line Up Analysis"
    if "play by play" in lowered:
        return "Play by Play"
    if "plusminus" in lowered or "plus/minus" in lowered or "plus minus" in lowered:
        return "Player PlusMinus Summary"
    return "Unknown"


def normalise_date(raw_date: str | None) -> str:
    if not raw_date:
        return ""
    return " ".join(raw_date.split())


def code_for_team_name(team_name: str) -> str:
    return TEAM_NAME_TO_CODE.get(team_name.strip().lower(), team_name.strip()[:3].upper())


def extract_match_metadata(text: str, pdf_name: str) -> dict[str, str]:
    date_match = DATE_RE.search(text)
    date = normalise_date(date_match.group("date") if date_match else "")

    score_match = SCORE_LINE_RE.search(text)
    if score_match:
        home_name = " ".join(score_match.group("home").split())
        away_name = " ".join(score_match.group("away").split())
        home_code = code_for_team_name(home_name)
        away_code = code_for_team_name(away_name)
        match_name = f"{home_code} vs {away_code}"
        if date:
            match_name = f"{match_name} - {date}"
        return {
            "date": date,
            "match_name": match_name,
            "home_team": home_code,
            "away_team": away_code,
        }

    stem = Path(pdf_name).stem
    cleaned = re.sub(
        r"^(FIBA Box Score|Line Up Analysis|Play by Play|Player PlusMinus Summary)\s+",
        "",
        stem,
        flags=re.IGNORECASE,
    )
    return {"date": date, "match_name": cleaned, "home_team": "", "away_team": ""}


def extract_page_texts(pdf_path: Path) -> list[PdfPageText]:
    pages: list[PdfPageText] = []
    with pdfplumber.open(pdf_path) as pdf:
        first_text = clean_text(pdf.pages[0].extract_text(x_tolerance=1, y_tolerance=3) or "")
        metadata = extract_match_metadata(first_text, pdf_path.name)
        report_type = detect_report_type(pdf_path.name)
        for index, page in enumerate(pdf.pages, start=1):
            text = clean_text(page.extract_text(x_tolerance=1, y_tolerance=3) or "")
            pages.append(
                PdfPageText(
                    source_pdf=pdf_path.name,
                    page_number=index,
                    text=text,
                    report_type=report_type,
                    match_name=metadata["match_name"],
                )
            )
    return pages


def split_made_attempted(value: str) -> tuple[int, int]:
    made, attempted = value.split("/", 1)
    return int(made), int(attempted)


def parse_player_line(
    line: str,
    team: str,
    opponent: str,
    match_name: str,
    date: str,
    source_pdf: str,
) -> dict[str, object] | None:
    line = " ".join(line.replace("*", "").split())
    if not line or line.startswith(("Team/Coach", "Totals")) or line.endswith(" DNP"):
        return None

    match = PLAYER_ROW_RE.match(line)
    if not match:
        return None

    data = match.groupdict()
    fg_made, fg_attempted = split_made_attempted(data["fg"])
    two_made, two_attempted = split_made_attempted(data["two"])
    three_made, three_attempted = split_made_attempted(data["three"])
    ft_made, ft_attempted = split_made_attempted(data["ft"])

    player_name = re.sub(r"\s+\(C\)$", "", data["name"]).strip()
    return {
        "match_name": match_name,
        "date": date,
        "team": team,
        "opponent": opponent,
        "player_name": player_name,
        "minutes": data["minutes"],
        "fg_made": fg_made,
        "fg_attempted": fg_attempted,
        "two_made": two_made,
        "two_attempted": two_attempted,
        "three_made": three_made,
        "three_attempted": three_attempted,
        "ft_made": ft_made,
        "ft_attempted": ft_attempted,
        "offensive_rebounds": int(data["oreb"]),
        "defensive_rebounds": int(data["dreb"]),
        "total_rebounds": int(data["treb"]),
        "assists": int(data["ast"]),
        "turnovers": int(data["tov"]),
        "steals": int(data["stl"]),
        "blocks": int(data["blk"]),
        "personal_fouls": int(data["pf"]),
        "fouls_drawn": int(data["fd"]),
        "plus_minus": int(data["pm"]),
        "efficiency": int(data["eff"]),
        "points": int(data["pts"]),
        "source_pdf": source_pdf,
    }


def extract_box_score_players(pdf_path: Path) -> list[dict[str, object]]:
    if detect_report_type(pdf_path.name) != "FIBA Box Score":
        return []

    with pdfplumber.open(pdf_path) as pdf:
        text = clean_text("\n".join(page.extract_text(x_tolerance=1, y_tolerance=3) or "" for page in pdf.pages))

    metadata = extract_match_metadata(text, pdf_path.name)
    teams = [team.group("code") for team in TEAM_HEADER_RE.finditer(text)]
    if not teams:
        teams = [metadata.get("home_team", ""), metadata.get("away_team", "")]
    teams = [team for team in teams if team]

    players: list[dict[str, object]] = []
    current_team = ""
    for raw_line in text.splitlines():
        line = " ".join(raw_line.split())
        header_match = TEAM_HEADER_RE.match(line)
        if header_match:
            current_team = header_match.group("code")
            continue
        if not current_team:
            continue

        opponent_candidates = [team for team in teams if team != current_team]
        opponent = opponent_candidates[0] if opponent_candidates else ""
        player = parse_player_line(
            line=line,
            team=current_team,
            opponent=opponent,
            match_name=metadata["match_name"],
            date=metadata["date"],
            source_pdf=pdf_path.name,
        )
        if player:
            players.append(player)

    return players


def chunk_words(words: list[str], chunk_size: int = 260, overlap: int = 50) -> Iterable[str]:
    if not words:
        return
    start = 0
    while start < len(words):
        end = min(start + chunk_size, len(words))
        yield " ".join(words[start:end])
        if end == len(words):
            break
        start = max(0, end - overlap)


def build_pdf_chunks(page_texts: list[PdfPageText]) -> list[dict[str, object]]:
    chunks: list[dict[str, object]] = []
    chunk_id = 1
    for page in page_texts:
        words = page.text.split()
        for chunk_text in chunk_words(words):
            chunks.append(
                {
                    "chunk_id": chunk_id,
                    "source_pdf": page.source_pdf,
                    "page_number": page.page_number,
                    "report_type": page.report_type,
                    "match_name": page.match_name,
                    "text": chunk_text,
                }
            )
            chunk_id += 1
    return chunks


def build_extracted_data(
    pdf_dir: Path = DATA_PDF_DIR,
    extracted_dir: Path = EXTRACTED_DIR,
    include_legacy: bool = True,
) -> dict[str, object]:
    extracted_dir.mkdir(parents=True, exist_ok=True)
    box_score_csv = extracted_dir / "players_box_scores.csv"
    chunks_csv = extracted_dir / "pdf_chunks.csv"
    pdf_files = list_pdf_files(pdf_dir=pdf_dir, include_legacy=include_legacy)

    all_pages: list[PdfPageText] = []
    all_players: list[dict[str, object]] = []
    for pdf_path in pdf_files:
        all_pages.extend(extract_page_texts(pdf_path))
        all_players.extend(extract_box_score_players(pdf_path))

    player_columns = REQUIRED_PLAYER_COLUMNS + ["source_pdf"]
    players_df = pd.DataFrame(all_players, columns=player_columns)
    _atomic_write_csv(players_df, box_score_csv)

    chunk_columns = ["chunk_id", "source_pdf", "page_number", "report_type", "match_name", "text"]
    chunks_df = pd.DataFrame(build_pdf_chunks(all_pages), columns=chunk_columns)
    _atomic_write_csv(chunks_df, chunks_csv)
    chroma_index_built = False
    try:
        from rag_engine import build_chroma_pdf_index

        chroma_index_built = build_chroma_pdf_index(
            chunks_csv=chunks_csv,
            persist_dir=extracted_dir / "chroma_pdf_index",
        )
    except Exception:
        chroma_index_built = False

    return {
        "pdf_count": len(pdf_files),
        "player_rows": len(players_df),
        "chunk_rows": len(chunks_df),
        "box_score_csv": str(box_score_csv),
        "chunks_csv": str(chunks_csv),
        "chroma_index_built": chroma_index_built,
    }


if __name__ == "__main__":
    summary = build_extracted_data()
    print(
        "Extracted {player_rows} player rows and {chunk_rows} PDF chunks from {pdf_count} PDFs.".format(
            **summary
        )
    )
    print(f"Box score CSV: {summary['box_score_csv']}")
    print(f"Chunks CSV: {summary['chunks_csv']}")
