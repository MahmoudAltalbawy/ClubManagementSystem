from __future__ import annotations

import math
import re
from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np
import pandas as pd
import pdfplumber

from config import ExtractionResult, logger
from utils import (
    minutes_to_float,
    parse_game_teams,
    parse_match_date,
    parse_match_id,
    parse_pair_order,
    parse_pair_stats,
    parse_player_line,
    parse_team_headers,
    parse_totals_line,
    safe_float,
    safe_int,
    safe_str,
)

def discover_pdf_files(data_dir: Path) -> list[Path]:
    return sorted([p for p in data_dir.glob('*.pdf') if p.is_file()])


def try_table_signal(page: pdfplumber.page.Page) -> tuple[bool,int]:
    try: tables=page.extract_tables() or []
    except: return False,0
    cnt=0
    for t in tables:
        if not t: continue
        flat=' '.join([safe_str(c).lower() for r in t if r for c in r if safe_str(c)])
        if all(k in flat for k in ['no','name','min','ef','pts']): cnt+=1
    return cnt>0,cnt

def extract_from_box_score_pdf(pdf_path: Path) -> ExtractionResult:
    players=[]; team_profiles=[]; logs=[]; notes=[]
    with pdfplumber.open(pdf_path) as pdf:
        all_lines=[]; all_text=[]
        for pidx,page in enumerate(pdf.pages, start=1):
            has_tbl,cnt=try_table_signal(page)
            txt=page.extract_text() or ''
            all_text.append(txt)
            all_lines.extend([safe_str(x) for x in txt.splitlines()])
            status='failed'
            if txt.strip(): status='extracted_successfully' if has_tbl else 'partially_extracted'
            logs.append({'file':str(pdf_path),'page':pidx,'table_index':'n/a','status':status,
                         'candidate_tables_detected':cnt,'message':'Parsed with text fallback extractor'})

    joined='\n'.join(all_text)
    match_id=parse_match_id(joined,pdf_path)
    match_date=parse_match_date(all_lines,pdf_path.name)
    home_name,away_name=parse_game_teams(all_lines)
    team_headers=parse_team_headers(all_lines)
    if len(team_headers)<2:
        logs.append({'file':str(pdf_path),'page':'n/a','table_index':'n/a','status':'failed',
                     'candidate_tables_detected':0,'message':'Could not detect two team header blocks'})
        return ExtractionResult(players,team_profiles,logs)

    pair_stats,pair_notes=parse_pair_stats(all_lines); notes.extend(pair_notes)
    pair_order=parse_pair_order(all_lines,[team_headers[0]['team_code'],team_headers[1]['team_code']])
    starts=[h['line_idx'] for h in team_headers]; ends=starts[1:]+[len(all_lines)]
    team_totals={}

    for i,h in enumerate(team_headers):
        team_name=h['team_name']; team_code=h['team_code']
        sect=all_lines[starts[i]:ends[i]]
        for ln in sect:
            if ln.startswith('Totals'):
                t=parse_totals_line(ln)
                if t: team_totals[team_code]=t
                break
        for ln in sect:
            s=ln.strip()
            if not s: continue
            if s.startswith(('Field Goals','No Name','M/A %','Coach:','Team/Coach','Totals')): continue
            parsed=parse_player_line(s)
            if not parsed: continue
            if parsed.get('parse_error'): notes.append(parsed['parse_error'])
            row={
              'Match_ID':match_id,'Match_Date':match_date,'Team':team_name,'Team_Code':team_code,
              'Source_File':str(pdf_path),'Name':parsed.get('Name'),'No.':parsed.get('No.'),'MIN':parsed.get('MIN'),
              'MIN_num':parsed.get('MIN_num'),'PTS':parsed.get('PTS'),'REB':parsed.get('REB'),'AST':parsed.get('AST'),
              'STL':parsed.get('STL'),'BLK':parsed.get('BLK'),'TO':parsed.get('TO'),'EF':parsed.get('EF'),
              'OR':parsed.get('OR'),'DR':parsed.get('DR'),'PF':parsed.get('PF'),'FD':parsed.get('FD'),'+/-':parsed.get('+/-'),
              'FG_MA':parsed.get('FG_MA'),'FG_PCT':parsed.get('FG_PCT'),'2PT_MA':parsed.get('2PT_MA'),'2PT_PCT':parsed.get('2PT_PCT'),
              '3PT_MA':parsed.get('3PT_MA'),'3PT_PCT':parsed.get('3PT_PCT'),'FT_MA':parsed.get('FT_MA'),'FT_PCT':parsed.get('FT_PCT'),
              'FGM':parsed.get('FGM'),'FGA':parsed.get('FGA'),'2PTM':parsed.get('2PTM'),'2PTA':parsed.get('2PTA'),
              '3PTM':parsed.get('3PTM'),'3PTA':parsed.get('3PTA'),'FTM':parsed.get('FTM'),'FTA':parsed.get('FTA'),'DNP':parsed.get('DNP',0)
            }
            players.append(row)

    team_codes=[h['team_code'] for h in team_headers]; names={h['team_code']:h['team_name'] for h in team_headers}
    for tc in team_codes:
        opp=[c for c in team_codes if c!=tc]; opp_code=opp[0] if opp else None
        totals=team_totals.get(tc,{})
        idx=pair_order.index(tc) if tc in pair_order else None
        def pair_val(k: str):
            if idx is None: return None
            p=pair_stats.get(k)
            return None if not p else p[idx]
        team_profiles.append({
          'Match_ID':match_id,'Match_Date':match_date,'Team_Code':tc,'Team':names.get(tc),
          'Opponent_Code':opp_code,'Opponent':names.get(opp_code),'team_def_rebounds':totals.get('team_dr'),
          'team_turnovers':totals.get('team_to'),'team_fast_break_points':pair_val('fast_break_points'),
          'team_points_from_turnovers':pair_val('points_from_turnovers'),'team_second_chance_points':pair_val('second_chance_points'),
          'team_bench_points':pair_val('bench_points'),'Source_File':str(pdf_path)
        })

    if len(team_codes)==2:
        code_to_opp={team_codes[0]:team_codes[1],team_codes[1]:team_codes[0]}
        for r in players:
            oc=code_to_opp.get(r['Team_Code']); r['Opponent_Code']=oc; r['Opponent']=names.get(oc)

    if home_name and away_name:
        for r in players:
            if safe_str(r['Team'])==home_name: r['Opponent']=away_name
            elif safe_str(r['Team'])==away_name: r['Opponent']=home_name

    if notes:
        logs.append({'file':str(pdf_path),'page':'all','table_index':'n/a','status':'partially_extracted',
                     'candidate_tables_detected':0,'message':'; '.join(notes[:8])})
    return ExtractionResult(players,team_profiles,logs)


def extract_team_code_map(lines: list[str]) -> dict[str, str]:
    """Build {team_full_name: 3-letter team_code} from scoring-intervals header."""
    home, away = parse_game_teams(lines)
    codes: list[str] = []
    found_scoring = False
    for ln in lines:
        s = ln.strip()
        m = re.search(r'Scoring by 5 Minute intervals\s+([A-Z]{3})', s)
        if m:
            c = m.group(1)
            if c not in codes:
                codes.append(c)
            found_scoring = True
            continue
        if found_scoring and len(codes) < 2:
            m2 = re.match(r'^([A-Z]{3})\s+\d+\s+\d+', s)
            if m2 and m2.group(1) not in codes:
                codes.append(m2.group(1))
    result: dict[str, str] = {}
    if len(codes) >= 2:
        if home: result[home] = codes[0]
        if away: result[away] = codes[1]
    return result


def extract_from_plusminus_pdf(pdf_path: Path) -> list[dict[str, Any]]:
    """Parse Player PlusMinus Summary PDF → per-player on/off court metrics."""
    rows: list[dict[str, Any]] = []
    with pdfplumber.open(pdf_path) as pdf:
        all_text_parts = []
        all_tables: list[list] = []
        for page in pdf.pages:
            txt = page.extract_text() or ''
            all_text_parts.append(txt)
            tables = page.extract_tables() or []
            all_tables.extend(tables)

    all_text = '\n'.join(all_text_parts)
    all_lines = [safe_str(x) for x in all_text.splitlines()]
    match_id = parse_match_id(all_text, pdf_path)
    match_date = parse_match_date(all_lines, pdf_path.name)
    name_to_code = extract_team_code_map(all_lines)

    for table in all_tables:
        if not table or len(table) < 4:
            continue
        # Detect PM table: row[1] must start with 'No', 'Name'
        header_row = table[1] if len(table) > 1 else []
        if not header_row or len(header_row) < 2:
            continue
        if safe_str(header_row[0]).lower() != 'no' or safe_str(header_row[1]).lower() != 'name':
            continue
        # Row 0 has team name
        team_name = safe_str(table[0][0]) if table[0] else ''
        team_code = name_to_code.get(team_name, team_name[:3].upper() if team_name else 'UNK')
        # Data rows start at index 3 (after team, headers, On/Off sub-headers)
        for r in table[3:]:
            if not r or len(r) < 18:
                continue
            no = safe_int(r[0])
            name = safe_str(r[1])
            if no is None or not name:
                continue
            rows.append({
                'Match_ID': match_id, 'Match_Date': match_date,
                'Team_Code': team_code, 'No.': no, 'Name': name,
                'pm_on_minutes': minutes_to_float(safe_str(r[2])),
                'pm_off_minutes': minutes_to_float(safe_str(r[3])),
                'pm_on_diff': safe_int(r[6]),
                'pm_off_diff': safe_int(r[7]),
                'pm_on_points_per_min': safe_float(r[8]),
                'pm_off_points_per_min': safe_float(r[9]),
                'pm_on_assists': safe_int(r[10]),
                'pm_off_assists': safe_int(r[11]),
                'pm_on_rebounds': safe_int(r[12]),
                'pm_off_rebounds': safe_int(r[13]),
                'pm_on_steals': safe_int(r[14]),
                'pm_off_steals': safe_int(r[15]),
                'pm_on_turnovers': safe_int(r[16]),
                'pm_off_turnovers': safe_int(r[17]),
            })
    logger.info('PlusMinus extracted %d player rows from %s', len(rows), pdf_path.name)
    return rows


def extract_from_lineup_pdf(pdf_path: Path) -> list[dict[str, Any]]:
    """Parse Line Up Analysis PDF → per-player lineup segment aggregates."""
    with pdfplumber.open(pdf_path) as pdf:
        all_text_parts = []
        all_tables: list[list] = []
        for page in pdf.pages:
            txt = page.extract_text() or ''
            all_text_parts.append(txt)
            tables = page.extract_tables() or []
            all_tables.extend(tables)

    all_text = '\n'.join(all_text_parts)
    all_lines = [safe_str(x) for x in all_text.splitlines()]
    match_id = parse_match_id(all_text, pdf_path)
    match_date = parse_match_date(all_lines, pdf_path.name)
    name_to_code = extract_team_code_map(all_lines)

    # Collect lineup segments keyed by (team_code, jersey_no)
    player_lineups: dict[tuple[str, int], list[dict]] = {}

    for table in all_tables:
        if not table or len(table) < 3:
            continue
        # Detect lineup table: row[1] starts with 'Lineup'
        header_row = table[1] if len(table) > 1 else []
        if not header_row or safe_str(header_row[0]).lower() != 'lineup':
            continue
        team_name = safe_str(table[0][0]) if table[0] else ''
        team_code = name_to_code.get(team_name, team_name[:3].upper() if team_name else 'UNK')
        # Data rows start at index 2 (no sub-header row in lineup tables)
        for r in table[2:]:
            if not r or len(r) < 9:
                continue
            lineup_str = safe_str(r[0])
            if not lineup_str:
                continue
            # Extract jersey numbers: format "​3- Goncalves G/ ​5- Dundao C/ ..."
            jersey_nums = [int(x) for x in re.findall(r'(\d+)\s*-', lineup_str)]
            if not jersey_nums:
                continue
            time_float = minutes_to_float(safe_str(r[1])) or 0.0
            score_diff = safe_float(r[3])
            pts_per_min = safe_float(r[4])
            seg = {
                'time': time_float,
                'score_diff': score_diff if score_diff is not None else 0.0,
                'pts_per_min': pts_per_min if pts_per_min is not None and not math.isnan(pts_per_min) else 0.0,
            }
            for jno in jersey_nums:
                key = (team_code, jno)
                if key not in player_lineups:
                    player_lineups[key] = []
                player_lineups[key].append(seg)

    # Aggregate per player
    rows: list[dict[str, Any]] = []
    for (tc, no), segs in player_lineups.items():
        total_time = sum(s['time'] for s in segs)
        diffs = [s['score_diff'] for s in segs]
        times = [s['time'] for s in segs]
        weighted_diff = (sum(t * d for t, d in zip(times, diffs)) / total_time) if total_time > 0 else 0.0
        rows.append({
            'Match_ID': match_id, 'Match_Date': match_date,
            'Team_Code': tc, 'No.': no,
            'lineup_segments': len(segs),
            'lineup_avg_diff': float(np.mean(diffs)) if diffs else 0.0,
            'lineup_best_diff': float(max(diffs)) if diffs else 0.0,
            'lineup_worst_diff': float(min(diffs)) if diffs else 0.0,
            'lineup_weighted_avg_diff': float(weighted_diff),
            'lineup_avg_pts_per_min': float(np.mean([s['pts_per_min'] for s in segs])),
            'lineup_total_minutes': float(total_time),
        })
    logger.info('Lineup extracted %d player rows from %s', len(rows), pdf_path.name)
    return rows


def extract_from_pbp_pdf(pdf_path: Path) -> list[dict[str, Any]]:
    """Parse Play by Play PDF → per-player event counts per game."""
    with pdfplumber.open(pdf_path) as pdf:
        all_text_parts = [page.extract_text() or '' for page in pdf.pages]
    all_text = '\n'.join(all_text_parts)
    all_lines = [safe_str(x) for x in all_text.splitlines()]
    match_id = parse_match_id(all_text, pdf_path)
    match_date = parse_match_date(all_lines, pdf_path.name)

    # 1. Build player→team map from quarter starters
    player_team: dict[tuple[int, str], str] = {}   # (jersey_no, 'LASTNAME I') → team_code
    p_starter_line = re.compile(r'^([A-Z]{3})\s+(.+)$')
    p_player_in_starter = re.compile(r'(\d+)\s+([A-Z][a-z]+\s+[A-Z])\b')
    for i, ln in enumerate(all_lines):
        if 'Quarter Starters:' in ln:
            for j in range(i + 1, min(i + 3, len(all_lines))):
                m = p_starter_line.match(all_lines[j].strip())
                if m:
                    tc = m.group(1)
                    for no_str, pname in p_player_in_starter.findall(m.group(2)):
                        player_team[(int(no_str), pname.upper())] = tc

    # 2. Scan event lines for player actions
    p_event = re.compile(r'(\d+)\s+([A-Z]{2,}\s+[A-Z])\s+(.*)')
    counts: dict[tuple[int, str], dict[str, int]] = defaultdict(lambda: defaultdict(int))
    pending_sub_team: str | None = None
    pending_shot: tuple[tuple[int, str], str] | None = None   # ((no, team), shot_type)

    for ln in all_lines:
        stripped = ln.strip()
        if not stripped:
            continue
        sl = stripped.lower()

        # Handle pending shot result from previous player event (continuation line)
        if pending_shot and not p_event.search(stripped):
            if 'made' in sl:
                ck, st = pending_shot
                counts[ck][f'pbp_{st}_made'] += 1
                pending_shot = None
                continue
            elif 'missed' in sl or 'blocked' in sl:
                ck, st = pending_shot
                counts[ck][f'pbp_{st}_missed'] += 1
                pending_shot = None
                continue

        m = p_event.search(stripped)
        if not m:
            continue

        no = int(m.group(1))
        name = m.group(2)       # e.g. "CASTRO C"
        event_text = m.group(3).lower()
        key = (no, name)

        # Lookup team
        team = player_team.get(key)

        # Handle substitutions for team tracking
        if 'substitution out' in event_text:
            if team:
                pending_sub_team = team
        elif 'substitution in' in event_text:
            if not team and pending_sub_team:
                player_team[key] = pending_sub_team
                team = pending_sub_team
            pending_sub_team = None

        if not team:
            continue
        ck = (no, team)

        # --- Count events ---
        if 'substitution' in event_text:
            counts[ck]['pbp_substitutions'] += 1
        if 'turnover' in event_text:
            counts[ck]['pbp_turnover_events'] += 1
        if 'foul' in event_text and 'foul received' not in event_text:
            counts[ck]['pbp_foul_events'] += 1
        if 'offensive rebound' in event_text:
            counts[ck]['pbp_off_reb_events'] += 1
        if 'defensive rebound' in event_text:
            counts[ck]['pbp_def_reb_events'] += 1
        if 'steal' in event_text:
            counts[ck]['pbp_steal_events'] += 1
        if 'assist' in event_text:
            counts[ck]['pbp_assist_events'] += 1
        if 'fast break' in event_text:
            counts[ck]['pbp_fast_break_events'] += 1

        # --- Shot tracking ---
        # Resolve stale pending shot if a different player takes a new action
        if pending_shot and pending_shot[0] != ck:
            pk, st = pending_shot
            counts[pk][f'pbp_{st}_missed'] += 1
            pending_shot = None

        if '2pt fg' in event_text:
            if 'made' in event_text:
                counts[ck]['pbp_2pt_made'] += 1; pending_shot = None
            elif 'missed' in event_text or 'blocked' in event_text:
                counts[ck]['pbp_2pt_missed'] += 1; pending_shot = None
            else:
                pending_shot = (ck, '2pt')
        elif '3pt fg' in event_text:
            if 'made' in event_text:
                counts[ck]['pbp_3pt_made'] += 1; pending_shot = None
            elif 'missed' in event_text:
                counts[ck]['pbp_3pt_missed'] += 1; pending_shot = None
            else:
                pending_shot = (ck, '3pt')
        elif 'free throw' in event_text:
            if 'made' in event_text:
                counts[ck]['pbp_ft_made'] += 1; pending_shot = None
            elif 'missed' in event_text:
                counts[ck]['pbp_ft_missed'] += 1; pending_shot = None
            else:
                pending_shot = (ck, 'ft')

    # Resolve any final pending shot
    if pending_shot:
        pk, st = pending_shot
        counts[pk][f'pbp_{st}_missed'] += 1

    # Build output rows
    pbp_cols = ['pbp_substitutions', 'pbp_turnover_events', 'pbp_foul_events',
                'pbp_off_reb_events', 'pbp_def_reb_events', 'pbp_steal_events',
                'pbp_assist_events', 'pbp_fast_break_events',
                'pbp_2pt_made', 'pbp_2pt_missed', 'pbp_3pt_made', 'pbp_3pt_missed',
                'pbp_ft_made', 'pbp_ft_missed']
    rows: list[dict[str, Any]] = []
    for (pno, ptc), evts in counts.items():
        row: dict[str, Any] = {
            'Match_ID': match_id, 'Match_Date': match_date,
            'Team_Code': ptc, 'No.': pno,
        }
        for col in pbp_cols:
            row[col] = evts.get(col, 0)
        rows.append(row)
    logger.info('PBP extracted %d player rows from %s', len(rows), pdf_path.name)
    return rows


def extract_all_pdfs(data_dir: Path) -> tuple[pd.DataFrame, pd.DataFrame, pd.DataFrame,
                                               pd.DataFrame, pd.DataFrame, pd.DataFrame]:
    """Extract data from ALL recognized PDF types in data_dir.
    Returns: (player_df, team_df, log_df, pm_df, lineup_df, pbp_df)
    """
    players=[]; teams=[]; logs=[]
    pm_rows: list[dict] = []; lineup_rows: list[dict] = []; pbp_rows: list[dict] = []
    for pdf_path in discover_pdf_files(data_dir):
        fname = pdf_path.name.lower()
        # --- Box Score PDFs (existing) ---
        if 'box score' in fname:
            try:
                res=extract_from_box_score_pdf(pdf_path)
                players.extend(res.player_rows); teams.extend(res.team_profiles); logs.extend(res.logs)
            except Exception as exc:
                logger.exception('Failed extracting box score %s', pdf_path)
                logs.append({'file':str(pdf_path),'page':'n/a','table_index':'n/a','status':'failed',
                             'candidate_tables_detected':0,'message':f'Exception: {exc}'})
        # --- Plus/Minus PDFs (NEW) ---
        elif 'plusminus' in fname:
            try:
                pm_rows.extend(extract_from_plusminus_pdf(pdf_path))
                logs.append({'file':str(pdf_path),'page':'all','table_index':'n/a',
                             'status':'extracted_successfully','candidate_tables_detected':1,
                             'message':'PlusMinus PDF parsed successfully'})
            except Exception as exc:
                logger.exception('Failed extracting plusminus %s', pdf_path)
                logs.append({'file':str(pdf_path),'page':'n/a','table_index':'n/a','status':'failed',
                             'candidate_tables_detected':0,'message':f'PlusMinus Exception: {exc}'})
        # --- Lineup Analysis PDFs (NEW) ---
        elif 'line up analysis' in fname:
            try:
                lineup_rows.extend(extract_from_lineup_pdf(pdf_path))
                logs.append({'file':str(pdf_path),'page':'all','table_index':'n/a',
                             'status':'extracted_successfully','candidate_tables_detected':1,
                             'message':'Lineup Analysis PDF parsed successfully'})
            except Exception as exc:
                logger.exception('Failed extracting lineup %s', pdf_path)
                logs.append({'file':str(pdf_path),'page':'n/a','table_index':'n/a','status':'failed',
                             'candidate_tables_detected':0,'message':f'Lineup Exception: {exc}'})
        # --- Play-by-Play PDFs (NEW) ---
        elif 'play by play' in fname:
            try:
                pbp_rows.extend(extract_from_pbp_pdf(pdf_path))
                logs.append({'file':str(pdf_path),'page':'all','table_index':'n/a',
                             'status':'extracted_successfully','candidate_tables_detected':1,
                             'message':'Play-by-Play PDF parsed successfully'})
            except Exception as exc:
                logger.exception('Failed extracting PBP %s', pdf_path)
                logs.append({'file':str(pdf_path),'page':'n/a','table_index':'n/a','status':'failed',
                             'candidate_tables_detected':0,'message':f'PBP Exception: {exc}'})
        else:
            # Other PDF types (Shot Chart, Rotations, Shot Areas) — skip for now
            logs.append({'file':str(pdf_path),'page':'n/a','table_index':'n/a','status':'skipped',
                         'candidate_tables_detected':0,'message':'Skipped unrecognized PDF type'})
    return (pd.DataFrame(players), pd.DataFrame(teams), pd.DataFrame(logs),
            pd.DataFrame(pm_rows), pd.DataFrame(lineup_rows), pd.DataFrame(pbp_rows))
