from __future__ import annotations

import re
from datetime import datetime
from pathlib import Path
from typing import Any

import pandas as pd

def safe_str(v: Any) -> str:
    return '' if v is None else str(v).replace('\u200b','').strip()

def normalize_name(name: Any) -> str:
    """Normalize player names for more reliable identity matching across PDFs."""
    s = safe_str(name).lower()
    s = s.replace('(c)', ' ').replace('(cap)', ' ')
    s = re.sub(r'[^a-z0-9\s]', ' ', s)
    s = re.sub(r'\s+', ' ', s).strip()
    return s

def build_player_key(team_code: Any, name: Any, jersey_no: Any) -> str:
    """Composite player identity: Team_Code + normalized Name + No."""
    team = safe_str(team_code).upper() or 'UNK'
    norm_name = normalize_name(name) or 'unknown'
    no = safe_int(jersey_no)
    no_part = 'NA' if no is None else str(no)
    return f"{team}|{norm_name}|{no_part}"

def safe_float(v: Any) -> float | None:
    s = safe_str(v).replace(',','')
    if s in {'','-','--','NA','N/A','None','nan'}: return None
    try: return float(s)
    except: return None

def safe_int(v: Any) -> int | None:
    f = safe_float(v)
    return None if f is None else int(round(f))

def minutes_to_float(t: str) -> float | None:
    m = re.match(r'^(\d{1,3}):(\d{2})$', safe_str(t))
    if not m: return None
    return round(int(m.group(1)) + int(m.group(2))/60.0, 4)

def parse_match_date(lines: list[str], filename: str) -> pd.Timestamp | pd.NaT:
    p = re.compile(r'(Mon|Tue|Wed|Thu|Fri|Sat|Sun)\s+(\d{1,2}\s+[A-Za-z]{3,9}\s+\d{4})')
    for ln in lines:
        m = p.search(ln)
        if m:
            d = m.group(2)
            for fmt in ('%d %b %Y','%d %B %Y'):
                try: return pd.Timestamp(datetime.strptime(d, fmt))
                except: pass
    fm = re.search(r'(\d{1,2})\s+([A-Za-z]{3,9})', filename)
    if fm:
        d = f"{int(fm.group(1))} {fm.group(2)} 2026"
        for fmt in ('%d %b %Y','%d %B %Y'):
            try: return pd.Timestamp(datetime.strptime(d, fmt))
            except: pass
    return pd.NaT

def parse_match_id(all_text: str, pdf_path: Path) -> str:
    m = re.search(r'Game No\.\:\s*([A-Za-z0-9\-]+)', all_text)
    return m.group(1) if m else pdf_path.stem.replace(' ','_')

def parse_game_teams(lines: list[str]) -> tuple[str | None, str | None]:
    for ln in lines:
        txt = ln.replace('–','-')
        m = re.match(r'^(.+?)\s+\d+\s+-\s+\d+\s+(.+)$', txt)
        if m: return safe_str(m.group(1)), safe_str(m.group(2))
    return None, None

def parse_team_headers(lines: list[str]) -> list[dict[str, Any]]:
    out=[]
    p=re.compile(r'^(.+?)\s+\(([A-Z]{3})\)\s+Assistant Coach\(es\)\:')
    for i,ln in enumerate(lines):
        m=p.match(ln.strip())
        if m: out.append({'line_idx':i,'team_name':safe_str(m.group(1)),'team_code':m.group(2)})
    return out

def parse_pair_stats(lines: list[str]) -> tuple[dict[str, tuple[int | None,int | None]], list[str]]:
    labels={
      'Points from Turnovers':'points_from_turnovers',
      'Second Chance Points':'second_chance_points',
      'Fast Break Points':'fast_break_points',
      'Bench Points':'bench_points'
    }
    parsed={}; notes=[]
    for ln in lines:
        s=ln.strip()
        if 'Fast Break Points from Turnovers' in s: continue
        for lbl,key in labels.items():
            if s.startswith(lbl):
                m=re.match(rf'^{re.escape(lbl)}\s+(-?\d+)\s+(-?\d+)', s)
                if m: parsed[key]=(int(m.group(1)),int(m.group(2)))
                else:
                    parsed[key]=(None,None)
                    notes.append(f"Could not parse pair for '{lbl}' in line: {s}")
    return parsed, notes

def parse_pair_order(lines: list[str], fallback: list[str]) -> list[str]:
    p=re.compile(r'^([A-Z]{3})\s+([A-Z]{3})\s+[A-Z]{3}\s+[A-Z]{3}$')
    for ln in lines:
        m=p.match(ln.strip())
        if m: return [m.group(1),m.group(2)]
    return fallback

def parse_ma(v: str) -> tuple[int | None,int | None]:
    m=re.match(r'^(\d+)\/(\d+)$', safe_str(v))
    return (int(m.group(1)),int(m.group(2))) if m else (None,None)

def parse_totals_line(line: str) -> dict[str, Any] | None:
    m=re.match(r'^Totals\s+(\d{1,3}:\d{2})\s+(.+)$', line.strip())
    if not m: return None
    toks=m.group(2).split()
    if len(toks)<20: return None
    return {
      'team_dr':safe_int(toks[9]), 'team_to':safe_int(toks[14]),
      'team_fast_break_points':None, 'team_points_from_turnovers':None,
      'team_second_chance_points':None, 'team_bench_points':None
    }
def parse_player_line(line: str) -> dict[str, Any] | None:
    s=line.strip()
    if not s or s.startswith('Team/Coach') or s.startswith('Totals'): return None
    m_dnp=re.match(r'^\*?(\d+)\s+(.+?)\s+DNP\s*$', s)
    if m_dnp:
        return {'No.':safe_int(m_dnp.group(1)),'Name':safe_str(m_dnp.group(2)).replace('(C)','').strip(),'DNP':1}
    m=re.match(r'^\*?(\d+)\s+(.+?)\s+(\d{1,3}:\d{2})\s+(.+)$', s)
    if not m: return None
    no=safe_int(m.group(1)); name=safe_str(m.group(2)).replace('(C)','').strip(); mtxt=m.group(3)
    toks=m.group(4).split()
    if len(toks)<20:
        return {'No.':no,'Name':name,'MIN':mtxt,'MIN_num':minutes_to_float(mtxt),'DNP':0,
                'parse_error':f'Malformed stats token count ({len(toks)}): {s}'}
    fgm,fga=parse_ma(toks[0]); t2m,t2a=parse_ma(toks[2]); t3m,t3a=parse_ma(toks[4]); ftm,fta=parse_ma(toks[6])
    return {
      'No.':no,'Name':name,'MIN':mtxt,'MIN_num':minutes_to_float(mtxt),
      'FG_MA':toks[0],'FG_PCT':safe_float(toks[1]),'2PT_MA':toks[2],'2PT_PCT':safe_float(toks[3]),
      '3PT_MA':toks[4],'3PT_PCT':safe_float(toks[5]),'FT_MA':toks[6],'FT_PCT':safe_float(toks[7]),
      'OR':safe_int(toks[8]),'DR':safe_int(toks[9]),'REB':safe_int(toks[10]),'PF':safe_int(toks[11]),
      'FD':safe_int(toks[12]),'AST':safe_int(toks[13]),'TO':safe_int(toks[14]),'STL':safe_int(toks[15]),
      'BLK':safe_int(toks[16]),'+/-':safe_int(toks[17]),'EF':safe_int(toks[18]),'PTS':safe_int(toks[19]),
      'FGM':fgm,'FGA':fga,'2PTM':t2m,'2PTA':t2a,'3PTM':t3m,'3PTA':t3a,'FTM':ftm,'FTA':fta,'DNP':0
    }
