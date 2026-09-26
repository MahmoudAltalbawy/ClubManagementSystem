"""
Live, non-PDF team data pulled from the .NET app (Phase 7 hybrid, extended in Phase 9).

Match PDFs can't tell us who is currently injured, available, scheduled, attending,
or how fit a player is, nor what the coaching plan says. For those questions the
microservice asks the app directly. The app exposes service-token guarded internal
endpoints (GET internal/teams/{team_id}/...) returning snake_case JSON we trust verbatim.

This client is OFF unless APP_API_BASE_URL is configured, so deployments that don't
share the app API keep working unchanged (callers get None and fall back to the
prediction CSV / RAG). All failures are swallowed and reported as "unavailable" rather
than raising, so a slow or down app never breaks a chat answer.

Read-only: every method here is a GET. The chatbot reads and advises; it never writes.
"""
from __future__ import annotations

import os
from typing import Any

import requests

from .cache import _MISS, cache_ttl_seconds, get_team_version, make_read_cache

# Phase 2c: one module-level Session for HTTP keep-alive + a shared connection pool.
# The chatbot makes several sequential GETs to the .NET app per /ask; a bare
# requests.get() opens a fresh TCP+TLS connection each time, and on a networked
# deployment that handshake dominates. A pooled Session reuses connections, so repeated
# hops to the same host skip the handshake. requests.Session over urllib3's pooled
# connections is safe to share across threads (each request checks out its own).
_SESSION = requests.Session()


def _interactive_timeout() -> tuple[float, float]:
    """(connect, read) timeout for interactive /ask lane calls.

    A tuple so a slow-to-connect host fails fast on connect instead of burning the whole
    budget. Read defaults to APP_API_TIMEOUT_INTERACTIVE (3s) — short, because a lane
    call blocks the coach's answer; connect is capped at 3s.
    """
    read = float(os.getenv("APP_API_TIMEOUT_INTERACTIVE", "3"))
    return (min(read, 3.0), read)


def _ingest_timeout() -> tuple[float, float]:
    """(connect, read) timeout for background/ingest calls — the original 8s read
    budget, where completing the fetch matters more than latency."""
    read = float(os.getenv("APP_API_TIMEOUT", "8"))
    return (min(read, 5.0), read)


# Phase 2f: one cache shared by all AppDataClient instances, so a multi-turn chat
# re-using the same endpoints within the window skips the network hop. Keyed including
# the team's version stamp (see cache.py) so an ingest in any worker invalidates
# entries on next read. Phase 5: make_read_cache() returns a shared RedisTTLCache when
# CACHE_BACKEND=redis (coherent across instances), else the per-process TTLCache.
_READ_CACHE = make_read_cache(cache_ttl_seconds())


class AppDataClient:
    def __init__(self, base_url: str | None = None, service_token: str | None = None, timeout: int | None = None) -> None:
        self.base_url = (base_url if base_url is not None else os.getenv("APP_API_BASE_URL", "")).strip().rstrip("/")
        self.service_token = service_token if service_token is not None else os.getenv("MICROSERVICE_SERVICE_TOKEN", "")
        # Default to the interactive (connect, read) tuple. An explicit scalar `timeout`
        # arg still wins (back-compat) and applies to both connect and read. Callers that
        # are doing background work pass interactive=False to _get for the longer budget.
        if timeout is not None:
            self.timeout: tuple[float, float] = (float(timeout), float(timeout))
        else:
            self.timeout = _interactive_timeout()

    def is_configured(self) -> bool:
        return bool(self.base_url)

    def _headers(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {self.service_token}"} if self.service_token else {}

    # ------------------------------------------------------------------
    # Generic GET helper — returns the parsed JSON dict, or None on any failure.
    # ------------------------------------------------------------------
    def _get(
        self,
        team_id: str,
        path: str,
        params: dict[str, Any] | None = None,
        *,
        interactive: bool = True,
    ) -> dict[str, Any] | None:
        if not self.is_configured() or not team_id:
            return None
        url = f"{self.base_url}/internal/teams/{team_id}/{path}"
        # interactive=True uses the instance timeout (short, for /ask lanes); a background
        # caller passes interactive=False for the longer ingest budget. An explicit scalar
        # timeout passed to __init__ already overrode self.timeout, so honour it either way.
        timeout = self.timeout if interactive else _ingest_timeout()
        try:
            resp = _SESSION.get(url, headers=self._headers(), params=params, timeout=timeout)
            resp.raise_for_status()
            payload = resp.json()
        except Exception:
            return None
        return payload if isinstance(payload, dict) else None

    def _cached_get(
        self,
        team_id: str,
        path: str,
        params: dict[str, Any] | None = None,
    ) -> dict[str, Any] | None:
        """TTL-cached variant of _get for hot, frequently-repeated read endpoints.

        Caches the raw payload (including a ``None`` "unavailable" result, so a down app
        doesn't get re-hit every turn within the window). The key carries the team's
        version stamp, so an ingest bump invalidates across workers. Returns exactly what
        _get would, so callers are unchanged apart from which helper they call.
        """
        if not self.is_configured() or not team_id:
            return None
        version = get_team_version(team_id)
        key = f"{self.base_url}|{team_id}|{path}|{sorted((params or {}).items())}"
        cached = _READ_CACHE.get(key, version)
        if cached is not _MISS:
            return cached
        payload = self._get(team_id, path, params)
        _READ_CACHE.set(key, version, payload)
        return payload

    @staticmethod
    def _list(payload: dict[str, Any] | None, key: str) -> list[dict[str, Any]] | None:
        if not isinstance(payload, dict):
            return None
        items = payload.get(key)
        if not isinstance(items, list):
            return None
        return [item for item in items if isinstance(item, dict)]

    # ------------------------------------------------------------------
    # Typed reads — each returns a list of dicts, or None when the app
    # integration is off / the call fails / the response is malformed.
    # ------------------------------------------------------------------

    def get_roster(self, team_id: str) -> list[dict[str, Any]] | None:
        """Members with name, role, position, jersey_number, is_injured, injury_type, injury_count."""
        return self._list(self._get(team_id, "roster"), "members")

    def get_injuries(self, team_id: str) -> list[dict[str, Any]] | None:
        """Active (uncleared) injuries: name, injury_type, diagnosis, record_date, expected_return_date."""
        return self._list(self._get(team_id, "injuries"), "injuries")

    def get_availability(self, team_id: str) -> list[dict[str, Any]] | None:
        """
        Per-player availability for prediction-time filtering: user_id, name, position,
        jersey_number, available (bool), reason. Unavailable players carry an injury reason.
        """
        return self._list(self._cached_get(team_id, "availability"), "players")

    def get_unavailable_names(self, team_id: str) -> set[str] | None:
        """
        Convenience for the prediction lane: lowercase names of players who are NOT
        available right now, or None if availability can't be determined (caller then
        degrades to model-only). An empty set means "everyone available".
        """
        players = self.get_availability(team_id)
        if players is None:
            return None
        out: set[str] = set()
        for p in players:
            if p.get("available") is False:
                name = str(p.get("name") or "").strip().lower()
                if name:
                    out.add(name)
        return out

    def get_schedule(self, team_id: str) -> list[dict[str, Any]] | None:
        """Upcoming events: event_id, title, event_type, start_at, end_at, location."""
        return self._list(self._cached_get(team_id, "schedule"), "events")

    def get_attendance(self, team_id: str, days: int | None = None) -> list[dict[str, Any]] | None:
        """Per-player attendance over a rolling window: name, present, total, rate."""
        params = {"days": days} if days else None
        return self._list(self._get(team_id, "attendance", params=params), "players")

    def get_fitness(self, team_id: str) -> list[dict[str, Any]] | None:
        """Latest fitness record per player: name, test_date, bmi, body_fat_pct, speed_test_result, endurance_score."""
        return self._list(self._get(team_id, "fitness"), "players")

    def get_player_stats(self, team_id: str) -> list[dict[str, Any]] | None:
        """Coach-recorded aggregates (DB-native, not PDF box scores): name, matches, goals, assists, avg_rating, cards."""
        return self._list(self._get(team_id, "player-stats"), "players")

    def get_plans(self, team_id: str) -> list[dict[str, Any]] | None:
        """Coaching plans (read-only): plan_id, title, description, content, visibility, created_at, updated_at."""
        return self._list(self._get(team_id, "plans"), "plans")

    # ------------------------------------------------------------------
    # Basketball box scores + analysis (PDF-imported into the DB). These are
    # the real basketball numbers the chatbot needs for "who shoots the most
    # threes", "top scorer", "best lineup", etc. — distinct from the
    # soccer-shaped get_player_stats above.
    # ------------------------------------------------------------------

    def get_match_player_stats(self, team_id: str) -> list[dict[str, Any]] | None:
        """Per-player basketball box scores (per-game + cumulative rows; team-totals excluded).
        Fields: name, opponent_name, matchup, game_no, date, granularity, status, player_no,
        is_starter, is_captain, games_played, starts, minutes, two_pt_ma, three_pt_ma,
        ft_ma, offensive_rebounds, defensive_rebounds, total_rebounds, assists, turnovers,
        steals, blocks, personal_fouls, fouls_drawn, efficiency, points."""
        return self._list(self._cached_get(team_id, "match-player-stats"), "players")

    def get_unified_box_scores(self, team_id: str) -> list[dict[str, Any]] | None:
        """Phase 1: deduplicated per-player box scores from the .NET unified view.

        Same row shape as get_match_player_stats, but the PDF-imported vs
        coach-entered overlap has already been resolved in SQL so each (player, game)
        appears once. This is the authoritative path; when the endpoint isn't deployed
        the .NET app returns 404/empty and this returns None, so the caller falls back
        to the get_match_player_stats / CSV ladder. Read-only, fail-soft."""
        return self._list(self._cached_get(team_id, "unified-box-scores"), "players")

    def get_team(self, team_id: str) -> dict[str, Any] | None:
        """Team identity for resolving the box-score team code: {code, name, ...}.

        Returns None when the app integration is off or the endpoint is absent, so
        callers fall back to inferring the code from match data."""
        payload = self._get(team_id, "team")
        return payload if isinstance(payload, dict) else None

    def get_match_team_stats(self, team_id: str) -> list[dict[str, Any]] | None:
        """Per-game + cumulative basketball team box scores: opponent_name, competition_name,
        venue, result, team_score, opponent_score, two_pt_ma, three_pt_ma, ft_ma, rebounds,
        assists, turnovers, steals, blocks, points, granularity, game_no, matchup, created_at."""
        return self._list(self._get(team_id, "match-team-stats"), "games")

    def get_match_reports(self, team_id: str) -> list[dict[str, Any]] | None:
        """Match analysis reports with written summaries + nested lineup on/off splits.
        Each report: opponent_name, match_date, competition, result, team_score,
        opponent_score, summary, lineups[] (lineup_players, time_on_court, points_for,
        points_against, score_diff, points_per_minute, rebounds, steals, turnovers, assists)."""
        return self._list(self._cached_get(team_id, "match-reports"), "reports")

    def get_lineup_analysis(self, team_id: str) -> list[dict[str, Any]] | None:
        """Flattened lineup on/off splits across all match reports (each row tagged with its
        report's opponent_name / match_date), sorted by time on court descending."""
        reports = self.get_match_reports(team_id)
        if reports is None:
            return None
        rows: list[dict[str, Any]] = []
        for r in reports:
            lineups = r.get("lineups")
            if not isinstance(lineups, list):
                continue
            for l in lineups:
                if not isinstance(l, dict):
                    continue
                row = dict(l)
                row.setdefault("opponent_name", r.get("opponent_name"))
                row.setdefault("match_date", r.get("match_date"))
                rows.append(row)
        rows.sort(key=lambda x: x.get("time_seconds") or 0, reverse=True)
        return rows

    def get_coaching_lineups(self, team_id: str) -> list[dict[str, Any]] | None:
        """Tactical lineups: title, formation, game_model, tactical_notes, visibility,
        created_by_name, players[] (name, position, unit, sort_order, instructions)."""
        return self._list(self._get(team_id, "coaching-lineups"), "lineups")

    def get_coach_notes(self, team_id: str) -> list[dict[str, Any]] | None:
        """Coach notes attached to games: event_id, author_name, author_role, body, created_at."""
        return self._list(self._get(team_id, "coach-notes"), "notes")

    def get_seasons(self, team_id: str) -> list[dict[str, Any]] | None:
        """Seasons: season_id, label, start_date, end_date, is_current."""
        return self._list(self._get(team_id, "seasons"), "seasons")
