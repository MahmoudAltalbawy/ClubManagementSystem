"""
FastAPI sidecar for basketball PDF extraction.
Run: uvicorn stats_extractor_api:app --host 0.0.0.0 --port 8100
Requires: pip install fastapi uvicorn pdfplumber pandas
"""

import sys
import tempfile
from pathlib import Path

from fastapi import FastAPI, UploadFile, HTTPException

# Add parent dir so extract.py can be imported
sys.path.insert(0, str(Path(__file__).resolve().parent))

try:
    from extract import extract_pdf, OUTPUT_COLUMNS
except ImportError:
    # If extract.py is not in the same directory, try the _scratch_pdf_data location
    scratch = Path(__file__).resolve().parent.parent.parent / "_scratch_pdf_data"
    sys.path.insert(0, str(scratch))
    from extract import extract_pdf, OUTPUT_COLUMNS

app = FastAPI(title="Basketball Stats Extractor", version="1.0.0")


BACKEND_FIELD_MAP = {
    "granularity": "Granularity",
    "row_type": "RowType",
    "source_file": "SourceFile",
    "game_no": "GameNo",
    "game_date": "GameDate",
    "start_time": "StartTime",
    "matchup": "Matchup",
    "team_code": "TeamCode",
    "team_name": "TeamName",
    "team_score": "TeamScore",
    "opponent_name": "OpponentName",
    "opponent_score": "OpponentScore",
    "player_no": "PlayerNo",
    "player_name": "PlayerName",
    "status": "Status",
    "is_starter": "IsStarter",
    "is_captain": "IsCaptain",
    "games_listed": "GamesListed",
    "games_played": "GamesPlayed",
    "starts": "Starts",
    "min": "Min",
    "2p_ma": "TwoPtMa",
    "3p_ma": "ThreePtMa",
    "ft_ma": "FtMa",
    "or": "Or",
    "dr": "Dr",
    "reb": "Reb",
    "ast": "Ast",
    "to": "To",
    "stl": "Stl",
    "blk": "Blk",
    "pf": "Pf",
    "fd": "Fd",
    "eff": "Eff",
    "pts": "Pts",
    "team_or": "TeamOr",
    "team_dr": "TeamDr",
    "team_reb": "TeamReb",
    "team_pf": "TeamPf",
    "team_fd": "TeamFd",
}


def with_backend_field_names(row: dict) -> dict:
    expanded = {}
    for source_key, backend_key in BACKEND_FIELD_MAP.items():
        if source_key in row:
            expanded[backend_key] = row[source_key]
    return expanded


@app.post("/extract")
async def extract(file: UploadFile):
    if not file.filename or not file.filename.lower().endswith(".pdf"):
        raise HTTPException(status_code=400, detail="Only PDF files are supported.")

    content = await file.read()
    if len(content) == 0:
        raise HTTPException(status_code=400, detail="Empty file.")

    with tempfile.NamedTemporaryFile(suffix=".pdf", delete=False) as tmp:
        tmp.write(content)
        tmp_path = Path(tmp.name)

    try:
        rows = extract_pdf(tmp_path)
        rows = [with_backend_field_names(row) for row in rows]
        return {
            "rows": rows,
            "count": len(rows),
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Extraction failed: {str(e)}")
    finally:
        try:
            tmp_path.unlink(missing_ok=True)
        except OSError:
            pass


@app.get("/health")
async def health():
    return {"status": "ok"}
