from __future__ import annotations

import math
from typing import Any

import numpy as np
import pandas as pd

from config import (
    COLD_START_DEFAULT,
    COLD_START_LEAGUE,
    COLD_START_PLAYER,
    COLD_START_SIMILARITY,
    COLD_START_TEAM,
    DEFAULT_FILL,
    HIST_LINEUP_FEATURES,
    HIST_PBP_FEATURES,
    HIST_PM_FEATURES,
    HIST_STATS,
    OPP_FEATURES,
    SIMILARITY_FEATURES,
    SIMILARITY_PROFILE_DIMS,
)
from utils import build_player_key, safe_str

def clean_player_data(df: pd.DataFrame) -> tuple[pd.DataFrame, dict[str, Any]]:
    summary={}
    if df.empty: return df, summary
    raw_rows=len(df); d=df.copy(); d['Name']=d['Name'].map(safe_str)
    d=d[~d['Name'].isin(['Team/Coach','Totals'])]
    dnp_rows=int(d['DNP'].fillna(0).astype(int).sum()) if 'DNP' in d.columns else 0
    d=d[d['DNP'].fillna(0).astype(int)==0]
    nums=['No.','MIN_num','PTS','REB','AST','STL','BLK','TO','EF','OR','DR','PF','FD','+/-',
          'FG_PCT','2PT_PCT','3PT_PCT','FT_PCT','FGM','FGA','2PTM','2PTA','3PTM','3PTA','FTM','FTA']
    for c in nums:
        if c in d.columns: d[c]=pd.to_numeric(d[c], errors='coerce')
    d['Match_Date']=pd.to_datetime(d['Match_Date'], errors='coerce')
    if 'Team_Code' not in d.columns:
        d['Team_Code'] = ''
    d['Player_Key'] = d.apply(lambda r: build_player_key(r.get('Team_Code'), r.get('Name'), r.get('No.')), axis=1)
    d=d.dropna(subset=['Match_ID','Team','Name','No.','EF'])
    d=d.drop_duplicates(subset=['Match_ID','Team_Code','Player_Key'], keep='first')
    summary['raw_rows']=raw_rows
    summary['dnp_rows_removed']=dnp_rows
    summary['usable_rows_after_cleaning']=len(d)
    summary['missing_match_date_rows']=int(d['Match_Date'].isna().sum())
    return d, summary


def cosine_similarity_sparse(a: np.ndarray, b: np.ndarray) -> float | None:
    valid=(~np.isnan(a)) & (~np.isnan(b))
    if valid.sum()==0: return None
    av,bv=a[valid],b[valid]
    denom=np.linalg.norm(av)*np.linalg.norm(bv)
    if denom==0: return None
    return float(np.dot(av,bv)/denom)


def compute_similarity_features(
    current_profile: np.ndarray,
    candidate_profiles: list[tuple[np.ndarray, float, float, float]],
) -> dict[str, float]:

    result: dict[str, float] = {f: 0.0 for f in SIMILARITY_FEATURES}

    if len(candidate_profiles) == 0:
        return result
    cand_arrays = np.array([cp[0] for cp in candidate_profiles])
    pool_mean = cand_arrays.mean(axis=0)
    pool_std = cand_arrays.std(axis=0)
    pool_std[pool_std == 0] = 1.0  # avoid division by zero for constant features

    current_std = (current_profile - pool_mean) / pool_std

    # Compute cosine similarity against each candidate
    similarities: list[tuple[float, float, float, float]] = []
    for cand_profile, cand_ef, cand_pts, cand_min in candidate_profiles:
        cand_std = (cand_profile - pool_mean) / pool_std
        sim = cosine_similarity_sparse(current_std, cand_std)
        if sim is not None:
            similarities.append((sim, cand_ef, cand_pts, cand_min))

    if not similarities:
        return result

    # Sort descending by similarity score
    similarities.sort(key=lambda x: x[0], reverse=True)

    # --- Top-1 neighbor ---
    result['Top1_Similarity'] = similarities[0][0]

    # --- Top-3 neighbors ---
    top3 = similarities[:3]
    result['Top3_Similarity_Avg'] = float(np.mean([s[0] for s in top3]))
    result['Top3_Neighbor_EF_Avg'] = float(np.mean([s[1] for s in top3]))
    result['Top3_Neighbor_PTS_Avg'] = float(np.mean([s[2] for s in top3]))
    result['Top3_Neighbor_MIN_Avg'] = float(np.mean([s[3] for s in top3]))

    # --- Top-5 neighbors ---
    top5 = similarities[:5]
    result['Top5_Similarity_Avg'] = float(np.mean([s[0] for s in top5]))
    result['Top5_Neighbor_EF_Avg'] = float(np.mean([s[1] for s in top5]))

    return result


def compute_cold_start_priors(
    team_code: str,
    match_order: int,
    d: pd.DataFrame,
) -> tuple[dict[str, float], int]:
    prior_all = d[d['match_order'] < match_order]
    priors: dict[str, float] = {}

    # Level 2: Team historical priors — teammates from prior matches
    team_prior = prior_all[prior_all['Team_Code'] == team_code]
    if not team_prior.empty:
        for stat in HIST_STATS:
            val = team_prior[stat].mean()
            priors[stat] = float(val) if pd.notna(val) else 0.0
        ef_val = team_prior['EF'].mean()
        priors['EF'] = float(ef_val) if pd.notna(ef_val) else 0.0
        return priors, COLD_START_TEAM

    # Level 3: League-wide priors — all players from prior matches
    if not prior_all.empty:
        for stat in HIST_STATS:
            val = prior_all[stat].mean()
            priors[stat] = float(val) if pd.notna(val) else 0.0
        ef_val = prior_all['EF'].mean()
        priors['EF'] = float(ef_val) if pd.notna(ef_val) else 0.0
        return priors, COLD_START_LEAGUE

    # Level 4: Absolute default — no prior data available at all
    for stat in HIST_STATS:
        priors[stat] = DEFAULT_FILL
    priors['EF'] = DEFAULT_FILL
    return priors, COLD_START_DEFAULT


def engineer_features(player_df: pd.DataFrame, team_df: pd.DataFrame,
                      pm_df: pd.DataFrame | None = None,
                      lineup_df: pd.DataFrame | None = None,
                      pbp_df: pd.DataFrame | None = None) -> tuple[pd.DataFrame, list[str]]:
    if player_df.empty: return player_df, ['No player data available for feature engineering.']
    notes=[]
    d=player_df.copy(); d['Match_Date']=pd.to_datetime(d['Match_Date'], errors='coerce')
    mo=(d[['Match_ID','Match_Date']].drop_duplicates().sort_values(['Match_Date','Match_ID'], kind='mergesort').reset_index(drop=True))
    mo['match_order']=np.arange(len(mo))
    d=d.merge(mo,on=['Match_ID','Match_Date'],how='left')

    if team_df.empty:
        team_df=pd.DataFrame(columns=['Match_ID','Match_Date','Team_Code','team_fast_break_points','team_def_rebounds','team_turnovers',
                                      'team_points_from_turnovers','team_second_chance_points','team_bench_points'])
    else:
        team_df=team_df.copy(); team_df['Match_Date']=pd.to_datetime(team_df['Match_Date'], errors='coerce')
        team_df=team_df.merge(mo,on=['Match_ID','Match_Date'],how='left')

    # --- Build jersey-to-name lookup from box-score data (FIX: Phase 1) ---
    # Supplementary extractors (PM, Lineup, PBP) may lack a 'Name' field or
    # have inconsistent name formats.  The box-score Player_Key is the canonical
    # identity.  We map (Match_ID, Team_Code, No.) -> Name from player_df so that
    # supplementary rows can reconstruct identical Player_Keys.
    _jersey_lookup = d[['Match_ID', 'Team_Code', 'No.', 'Name']].drop_duplicates(
        subset=['Match_ID', 'Team_Code', 'No.'], keep='first'
    )
    _jersey_lookup = _jersey_lookup.rename(columns={'Name': '_bs_name'})
    _supp_unmatched = 0  # counter for diagnostic logging

    def _enrich_supplementary(supp: pd.DataFrame) -> pd.DataFrame:
        """Inject canonical box-score Name into supplementary DF, then build Player_Key."""
        nonlocal _supp_unmatched
        supp = supp.merge(
            _jersey_lookup, on=['Match_ID', 'Team_Code', 'No.'], how='left'
        )
        # Prefer box-score name; fall back to supplementary Name if present
        if 'Name' in supp.columns:
            supp['Name'] = supp['_bs_name'].fillna(supp['Name'])
        else:
            supp['Name'] = supp['_bs_name']
        _n_missing = int(supp['Name'].isna().sum())
        _supp_unmatched += _n_missing
        supp['Name'] = supp['Name'].fillna('unknown')
        supp = supp.drop(columns=['_bs_name'], errors='ignore')
        supp['Player_Key'] = supp.apply(
            lambda r: build_player_key(r.get('Team_Code'), r.get('Name'), r.get('No.')), axis=1
        )
        return supp

    # --- Merge match_order into supplementary DataFrames ---
    mo_lookup = mo[['Match_ID', 'match_order']].copy()
    if pm_df is not None and not pm_df.empty:
        _pm = pm_df.copy()
        _pm['Match_Date'] = pd.to_datetime(_pm['Match_Date'], errors='coerce')
        _pm = _pm.merge(mo_lookup, on='Match_ID', how='left')
        _pm = _pm.dropna(subset=['match_order'])
        _pm = _enrich_supplementary(_pm)
        for c in ['pm_on_diff','pm_off_diff','pm_on_points_per_min','pm_off_points_per_min',
                   'pm_on_assists','pm_off_assists','pm_on_rebounds','pm_off_rebounds',
                   'pm_on_steals','pm_off_steals','pm_on_turnovers','pm_off_turnovers']:
            if c in _pm.columns: _pm[c] = pd.to_numeric(_pm[c], errors='coerce')
    else:
        _pm = pd.DataFrame()

    if lineup_df is not None and not lineup_df.empty:
        _lineup = lineup_df.copy()
        _lineup['Match_Date'] = pd.to_datetime(_lineup['Match_Date'], errors='coerce')
        _lineup = _lineup.merge(mo_lookup, on='Match_ID', how='left')
        _lineup = _lineup.dropna(subset=['match_order'])
        _lineup = _enrich_supplementary(_lineup)
    else:
        _lineup = pd.DataFrame()

    if pbp_df is not None and not pbp_df.empty:
        _pbp = pbp_df.copy()
        _pbp['Match_Date'] = pd.to_datetime(_pbp['Match_Date'], errors='coerce')
        _pbp = _pbp.merge(mo_lookup, on='Match_ID', how='left')
        _pbp = _pbp.dropna(subset=['match_order'])
        _pbp = _enrich_supplementary(_pbp)
        for c in ['pbp_substitutions','pbp_turnover_events','pbp_foul_events',
                   'pbp_off_reb_events','pbp_def_reb_events','pbp_steal_events',
                   'pbp_assist_events','pbp_fast_break_events',
                   'pbp_2pt_made','pbp_2pt_missed','pbp_3pt_made','pbp_3pt_missed',
                   'pbp_ft_made','pbp_ft_missed']:
            if c in _pbp.columns: _pbp[c] = pd.to_numeric(_pbp[c], errors='coerce')
    else:
        _pbp = pd.DataFrame()

    d=d.sort_values(['match_order','Match_ID','Team','No.'], kind='mergesort').reset_index(drop=True)
    feats=[]
    overall_defaults=0; recent_from_overall=0; cold_count=0; cold_unresolved=0
    # --- NEW: Cold-start level counters for diagnostics ---
    cs_player_count=0; cs_similarity_count=0; cs_team_count=0; cs_league_count=0; cs_default_count=0

    for idx, (_, row) in enumerate(d.iterrows()):
        prior=d[d['match_order']<row['match_order']]
        ph=prior[prior['Player_Key']==row['Player_Key']].sort_values('match_order')
        rh=ph.tail(3)
        feat={'Returning_From_Absence':0,'Past_Appearances':int(len(ph)),'Player_First_Appearance':1 if len(ph)==0 else 0}

        if row['match_order']>=3:
            last_orders=[row['match_order']-1,row['match_order']-2,row['match_order']-3]
            recent_matches=mo[mo['match_order'].isin(last_orders)]['Match_ID']
            appeared=prior[(prior['Player_Key']==row['Player_Key']) & (prior['Match_ID'].isin(recent_matches))].shape[0] > 0
            feat['Returning_From_Absence']=1 if not appeared else 0

        # =================================================================
        # MODIFIED: Layered cold-start fallback for Overall/Recent averages
        # Instead of flat 0.0 default, use priority cascade:
        #   1. Player own history (best — COLD_START_PLAYER)
        #   2. Team historical priors (COLD_START_TEAM)
        #   3. League-wide priors (COLD_START_LEAGUE)
        #   4. Default 0.0 (last resort — COLD_START_DEFAULT)
        # Similarity-based upgrade (COLD_START_SIMILARITY) applied later
        # after the profile vector is constructed.
        # =================================================================
        cold_start_level = COLD_START_PLAYER
        cold_start_priors = None
        if ph.empty:
            # Player has no prior game history — activate cold-start cascade
            cold_start_priors, cold_start_level = compute_cold_start_priors(
                team_code=row['Team_Code'],
                match_order=row['match_order'],
                d=d,
            )

        ov_vals=[]; rv_vals=[]
        for stat in HIST_STATS:
            ov=ph[stat].mean() if not ph.empty else np.nan
            rv=rh[stat].mean() if not rh.empty else np.nan
            if pd.isna(ov):
                # Use layered cold-start priors instead of flat DEFAULT_FILL
                ov = cold_start_priors.get(stat, DEFAULT_FILL) if cold_start_priors else DEFAULT_FILL
                overall_defaults+=1
            if pd.isna(rv): rv=ov; recent_from_overall+=1
            feat[f'Overall_Avg_{stat}']=float(ov); feat[f'Recent_Avg_{stat}']=float(rv)
            ov_vals.append(float(ov)); rv_vals.append(float(rv))

        feat['Overall_Average']=float(np.mean(ov_vals)) if ov_vals else DEFAULT_FILL
        feat['Recent_Form_Average']=float(np.mean(rv_vals)) if rv_vals else feat['Overall_Average']

        # --- Trend features (last prior game vs career average, strictly prior) ---
        if len(ph) >= 2:
            feat['EF_Trend'] = float(ph['EF'].iloc[-1] - ph['EF'].mean())
            feat['PTS_Trend'] = float(ph['PTS'].iloc[-1] - ph['PTS'].mean())
            feat['REB_Trend'] = float(ph['REB'].iloc[-1] - ph['REB'].mean())
            feat['AST_Trend'] = float(ph['AST'].iloc[-1] - ph['AST'].mean())
        else:
            feat['EF_Trend'] = 0.0; feat['PTS_Trend'] = 0.0
            feat['REB_Trend'] = 0.0; feat['AST_Trend'] = 0.0

        # --- Volatility features (std dev across all prior games) ---
        feat['EF_StdDev'] = float(ph['EF'].std()) if len(ph) >= 2 else 0.0
        feat['PTS_StdDev'] = float(ph['PTS'].std()) if len(ph) >= 2 else 0.0
        feat['MIN_StdDev'] = float(ph['MIN_num'].std()) if len(ph) >= 2 else 0.0

        # --- Last-game features (most recent prior game only) ---
        if not ph.empty:
            _last = ph.iloc[-1]
            feat['Last_EF'] = float(_last['EF']) if pd.notna(_last['EF']) else 0.0
            feat['Last_PTS'] = float(_last['PTS']) if pd.notna(_last['PTS']) else 0.0
            feat['Last_REB'] = float(_last['REB']) if pd.notna(_last['REB']) else 0.0
            feat['Last_AST'] = float(_last['AST']) if pd.notna(_last['AST']) else 0.0
            feat['Last_MIN_num'] = float(_last['MIN_num']) if pd.notna(_last['MIN_num']) else 0.0
            feat['Last_Plus_Minus'] = float(_last['+/-']) if pd.notna(_last['+/-']) else 0.0
        else:
            feat['Last_EF'] = 0.0; feat['Last_PTS'] = 0.0; feat['Last_REB'] = 0.0
            feat['Last_AST'] = 0.0; feat['Last_MIN_num'] = 0.0; feat['Last_Plus_Minus'] = 0.0

        # --- EWM (exponentially weighted mean) for recency bias, prior only ---
        if len(ph) >= 2:
            feat['EF_Ewm_3'] = float(ph['EF'].ewm(span=3, min_periods=1).mean().iloc[-1])
        else:
            feat['EF_Ewm_3'] = feat['Last_EF']

        # --- Per-minute rate features from career history (prior only) ---
        _total_min = float(ph['MIN_num'].sum()) if not ph.empty else 0.0
        if _total_min > 0:
            feat['Hist_PTS_Per_Min'] = float(ph['PTS'].sum() / _total_min)
            feat['Hist_REB_Per_Min'] = float(ph['REB'].sum() / _total_min)
            feat['Hist_EF_Per_Min'] = float(ph['EF'].sum() / _total_min)
        else:
            feat['Hist_PTS_Per_Min'] = 0.0; feat['Hist_REB_Per_Min'] = 0.0; feat['Hist_EF_Per_Min'] = 0.0

        # --- Activity: games played in last 5 match slots (prior only) ---
        if row['match_order'] >= 1:
            _lo = list(range(max(0, int(row['match_order']) - 5), int(row['match_order'])))
            _l5_matches = mo[mo['match_order'].isin(_lo)]['Match_ID']
            feat['Games_Played_Last_5_Slots'] = int(
                prior[(prior['Player_Key'] == row['Player_Key']) & (prior['Match_ID'].isin(_l5_matches))].shape[0])
        else:
            feat['Games_Played_Last_5_Slots'] = 0

        # =====================================================================
        # NEW: Extreme-sensitivity features (reduce regression-to-the-mean)
        # =====================================================================
        if len(ph) >= 2:
            _ef_vals = ph['EF'].dropna()
            feat['EF_Max_Prior'] = float(_ef_vals.max())
            feat['EF_Min_Prior'] = float(_ef_vals.min())
            feat['EF_Range_Prior'] = float(_ef_vals.max() - _ef_vals.min())
            _ef_mean = _ef_vals.mean()
            feat['EF_Pct_Above_Mean'] = float((_ef_vals > _ef_mean).sum() / len(_ef_vals)) if len(_ef_vals) > 0 else 0.5
            feat['EF_Ewm_2'] = float(_ef_vals.ewm(span=2, min_periods=1).mean().iloc[-1])
        elif len(ph) == 1:
            _v = float(ph['EF'].iloc[0])
            feat['EF_Max_Prior'] = _v; feat['EF_Min_Prior'] = _v; feat['EF_Range_Prior'] = 0.0
            feat['EF_Pct_Above_Mean'] = 0.5; feat['EF_Ewm_2'] = _v
        else:
            feat['EF_Max_Prior'] = 0.0; feat['EF_Min_Prior'] = 0.0; feat['EF_Range_Prior'] = 0.0
            feat['EF_Pct_Above_Mean'] = 0.5; feat['EF_Ewm_2'] = 0.0

        if len(ph) >= 2:
            feat['Last_2_EF_Avg'] = float(ph['EF'].tail(2).mean())
        elif len(ph) == 1:
            feat['Last_2_EF_Avg'] = float(ph['EF'].iloc[0])
        else:
            feat['Last_2_EF_Avg'] = 0.0

        # =================================================================
        # NEW: Cold-start EF feature override (Phase 1)
        # For first-appearance players, replace 0.0 EF-based features with
        # team/league priors to give the model a meaningful baseline.
        # Without this, the model sees 0.0 for Last_EF, EF_Ewm, etc. which
        # is misleading (0.0 EF is a real bad game, not "no data").
        # =================================================================
        if cold_start_level > COLD_START_PLAYER and cold_start_priors is not None:
            _ef_prior = cold_start_priors.get('EF', DEFAULT_FILL)
            if _ef_prior != DEFAULT_FILL:
                feat['Last_EF'] = _ef_prior
                feat['EF_Ewm_3'] = _ef_prior
                feat['EF_Ewm_2'] = _ef_prior
                feat['Last_2_EF_Avg'] = _ef_prior
                feat['EF_Max_Prior'] = _ef_prior
                feat['EF_Min_Prior'] = _ef_prior
        current_profile = np.array([feat[dim] for dim in SIMILARITY_PROFILE_DIMS])
        candidate_profiles: list[tuple[np.ndarray, float, float, float]] = []
        for j in range(idx):
            if d.iloc[j]['match_order'] < row['match_order']:
                cand_profile = np.array([feats[j][dim] for dim in SIMILARITY_PROFILE_DIMS])
                cand_ef = float(d.iloc[j]['EF']) if pd.notna(d.iloc[j]['EF']) else 0.0
                cand_pts = float(d.iloc[j]['PTS']) if pd.notna(d.iloc[j]['PTS']) else 0.0
                cand_min = float(d.iloc[j]['MIN_num']) if pd.notna(d.iloc[j]['MIN_num']) else 0.0
                candidate_profiles.append((cand_profile, cand_ef, cand_pts, cand_min))

        sim_feats = compute_similarity_features(current_profile, candidate_profiles)

        # For cold-start players: if similarity neighbors were found,
        # upgrade cold-start level to SIMILARITY — indicates the model has
        # useful neighbor-based signal beyond just team/league averages.
        if cold_start_level >= COLD_START_TEAM and sim_feats['Top1_Similarity'] > 0.0:
            cold_start_level = COLD_START_SIMILARITY

        feat['Cold_Start_Level'] = cold_start_level
        feat.update(sim_feats)

        # --- Track cold-start level distribution for diagnostics ---
        if cold_start_level == COLD_START_PLAYER: cs_player_count += 1
        elif cold_start_level == COLD_START_SIMILARITY: cs_similarity_count += 1
        elif cold_start_level == COLD_START_TEAM: cs_team_count += 1
        elif cold_start_level == COLD_START_LEAGUE: cs_league_count += 1
        else: cs_default_count += 1

        # =====================================================================
        # EXISTING: Opponent profile features (prior only, no leakage)
        # =====================================================================
        feat['Opponent_Profile_Proxy_Team']=None
        feat['Opponent_Profile_ColdStart']=0
        feat['Opponent_Profile_Unresolved']=0
        opp_vals={k:np.nan for k in OPP_FEATURES}

        hist_team=team_df[team_df['match_order']<row['match_order']]
        direct=hist_team[hist_team['Team_Code']==row['Opponent_Code']]
        if not direct.empty:
            opp_vals={
              'opp_profile_fast_break_points':direct['team_fast_break_points'].mean(),
              'opp_profile_def_rebounds':direct['team_def_rebounds'].mean(),
              'opp_profile_turnovers':direct['team_turnovers'].mean(),
              'opp_profile_points_from_turnovers':direct['team_points_from_turnovers'].mean(),
              'opp_profile_second_chance_points':direct['team_second_chance_points'].mean(),
              'opp_profile_bench_points':direct['team_bench_points'].mean()
            }
        else:
            feat['Opponent_Profile_ColdStart']=1; cold_count+=1
            # FIX: Use league-average from all historical teams instead of
            # current-game data (which would be data leakage).
            if not hist_team.empty:
                _tp_cols=['team_fast_break_points','team_def_rebounds','team_turnovers',
                          'team_points_from_turnovers','team_second_chance_points','team_bench_points']
                _league_avg=hist_team[_tp_cols].mean(numeric_only=True)
                opp_vals={
                  'opp_profile_fast_break_points':_league_avg['team_fast_break_points'],
                  'opp_profile_def_rebounds':_league_avg['team_def_rebounds'],
                  'opp_profile_turnovers':_league_avg['team_turnovers'],
                  'opp_profile_points_from_turnovers':_league_avg['team_points_from_turnovers'],
                  'opp_profile_second_chance_points':_league_avg['team_second_chance_points'],
                  'opp_profile_bench_points':_league_avg['team_bench_points']
                }
                feat['Opponent_Profile_Proxy_Team']='LEAGUE_AVG'
            else:
                feat['Opponent_Profile_Unresolved']=1; cold_unresolved+=1

        for k,v in opp_vals.items(): feat[k]=DEFAULT_FILL if pd.isna(v) else float(v)

        # =====================================================================
        # NEW: Plus/Minus historical features (prior games only)
        # Uses Team_Code for accurate player matching across teams.
        # =====================================================================
        # Initialize all supplementary features to default
        for f_name in HIST_PM_FEATURES + HIST_LINEUP_FEATURES + HIST_PBP_FEATURES:
            feat[f_name] = 0.0

        if not _pm.empty:
            pm_prior = _pm[(_pm['match_order'] < row['match_order']) &
                           (_pm['Player_Key'] == row['Player_Key'])]
            if not pm_prior.empty:
                _on_d = pm_prior['pm_on_diff'].dropna()
                _off_d = pm_prior['pm_off_diff'].dropna()
                feat['hist_pm_avg_on_diff'] = float(_on_d.mean()) if len(_on_d) > 0 else 0.0
                feat['hist_pm_avg_off_diff'] = float(_off_d.mean()) if len(_off_d) > 0 else 0.0
                if len(_on_d) > 0 and len(_off_d) > 0:
                    feat['hist_pm_avg_net_impact'] = float((_on_d.mean()) - (_off_d.mean()))
                else:
                    feat['hist_pm_avg_net_impact'] = 0.0
                _ppm = pm_prior['pm_on_points_per_min'].dropna()
                feat['hist_pm_avg_on_pts_per_min'] = float(_ppm.mean()) if len(_ppm) > 0 else 0.0
                if len(_on_d) >= 2:
                    feat['hist_pm_trend_on_diff'] = float(_on_d.iloc[-1] - _on_d.mean())
                    feat['hist_pm_stddev_on_diff'] = float(_on_d.std())
                else:
                    feat['hist_pm_trend_on_diff'] = 0.0
                    feat['hist_pm_stddev_on_diff'] = 0.0

        # =====================================================================
        # NEW: Lineup Analysis historical features (prior games only)
        # =====================================================================
        if not _lineup.empty:
            lu_prior = _lineup[(_lineup['match_order'] < row['match_order']) &
                               (_lineup['Player_Key'] == row['Player_Key'])]
            if not lu_prior.empty:
                feat['hist_lineup_avg_segments'] = float(lu_prior['lineup_segments'].mean())
                feat['hist_lineup_avg_weighted_diff'] = float(lu_prior['lineup_weighted_avg_diff'].mean())
                feat['hist_lineup_avg_best_diff'] = float(lu_prior['lineup_best_diff'].mean())
                feat['hist_lineup_avg_worst_diff'] = float(lu_prior['lineup_worst_diff'].mean())
                feat['hist_lineup_avg_total_minutes'] = float(lu_prior['lineup_total_minutes'].mean())
                feat['hist_lineup_stability'] = float(lu_prior['lineup_segments'].std()) if len(lu_prior) >= 2 else 0.0

        # =====================================================================
        # NEW: Play-by-Play historical features (prior games only)
        # =====================================================================
        if not _pbp.empty:
            pbp_prior = _pbp[(_pbp['match_order'] < row['match_order']) &
                             (_pbp['Player_Key'] == row['Player_Key'])]
            if not pbp_prior.empty:
                feat['hist_pbp_avg_substitutions'] = float(pbp_prior['pbp_substitutions'].mean())
                feat['hist_pbp_avg_turnovers'] = float(pbp_prior['pbp_turnover_events'].mean())
                feat['hist_pbp_avg_fouls'] = float(pbp_prior['pbp_foul_events'].mean())
                feat['hist_pbp_avg_off_reb'] = float(pbp_prior['pbp_off_reb_events'].mean())
                feat['hist_pbp_avg_def_reb'] = float(pbp_prior['pbp_def_reb_events'].mean())
                feat['hist_pbp_avg_steals'] = float(pbp_prior['pbp_steal_events'].mean())
                feat['hist_pbp_avg_assists'] = float(pbp_prior['pbp_assist_events'].mean())
                feat['hist_pbp_avg_fast_break'] = float(pbp_prior['pbp_fast_break_events'].mean())
                # Trend: last game turnovers vs career average
                if len(pbp_prior) >= 2:
                    feat['hist_pbp_trend_turnovers'] = float(
                        pbp_prior['pbp_turnover_events'].iloc[-1] - pbp_prior['pbp_turnover_events'].mean())
                # Shot selection rates from historical PBP
                _t2 = float(pbp_prior['pbp_2pt_made'].sum() + pbp_prior['pbp_2pt_missed'].sum())
                _t3 = float(pbp_prior['pbp_3pt_made'].sum() + pbp_prior['pbp_3pt_missed'].sum())
                _tf = float(pbp_prior['pbp_ft_made'].sum() + pbp_prior['pbp_ft_missed'].sum())
                _total_shots = _t2 + _t3 + _tf
                if _total_shots > 0:
                    feat['hist_pbp_2pt_rate'] = _t2 / _total_shots
                    feat['hist_pbp_3pt_rate'] = _t3 / _total_shots
                    feat['hist_pbp_ft_rate'] = _tf / _total_shots

        feats.append(feat)

    out=pd.concat([d.reset_index(drop=True), pd.DataFrame(feats).reset_index(drop=True)], axis=1)
    notes.append(f'Cold-start fill strategy: Layered cascade (Player\u2192Similarity\u2192Team\u2192League\u2192Default). '
                 f'Distribution: player={cs_player_count}, similarity={cs_similarity_count}, '
                 f'team={cs_team_count}, league={cs_league_count}, default={cs_default_count}.')
    notes.append(f'Cosine similarity features: {len(SIMILARITY_FEATURES)} features computed using '
                 f'z-score standardized {len(SIMILARITY_PROFILE_DIMS)}-dim profile vectors from prior data only.')
    notes.append(f'Recent_Form fallback: {recent_from_overall} feature values filled from Overall averages when recent history unavailable.')
    notes.append(f'Overall defaults applied: {overall_defaults} feature values.')
    notes.append(f'Cold-start opponents: {cold_count} rows; unresolved cold-starts due unavailable profiles: {cold_unresolved} rows.')
    notes.append("Identity linkage uses composite Player_Key = Team_Code + normalized Name + No., reducing ambiguity versus jersey-number-only matching.")
    notes.append(f'Supplementary matching: jersey-to-name lookup from box-score data; {_supp_unmatched} supplementary rows could not be matched.')
    notes.append(f'PlusMinus historical features computed from {len(_pm)} PM rows across {int(_pm["Match_ID"].nunique()) if not _pm.empty else 0} games.')
    notes.append(f'Lineup historical features computed from {len(_lineup)} lineup rows across {int(_lineup["Match_ID"].nunique()) if not _lineup.empty else 0} games.')
    notes.append(f'PBP historical features computed from {len(_pbp)} PBP rows across {int(_pbp["Match_ID"].nunique()) if not _pbp.empty else 0} games.')
    return out, notes


def split_train_test_by_match(df: pd.DataFrame, train_ratio: float=0.8) -> tuple[pd.DataFrame,pd.DataFrame]:
    m=df[['Match_ID','Match_Date']].drop_duplicates().sort_values(['Match_Date','Match_ID'], kind='mergesort').reset_index(drop=True)
    n=len(m)
    if n==0: return df.iloc[0:0],df.iloc[0:0]
    if n==1: return df.copy(),df.iloc[0:0]
    n_train=max(1,min(n-1,int(math.floor(n*train_ratio))))
    train_ids=set(m.iloc[:n_train]['Match_ID'])
    return df[df['Match_ID'].isin(train_ids)].copy(), df[~df['Match_ID'].isin(train_ids)].copy()
