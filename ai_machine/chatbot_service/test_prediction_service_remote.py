from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import pandas as pd

from services.prediction_service import PredictionService
from services.project_store import ProjectStore


class FakeResponse:
    def __init__(self, payload: dict, status_code: int = 200) -> None:
        self.payload = payload
        self.status_code = status_code

    def raise_for_status(self) -> None:
        if self.status_code >= 400:
            raise RuntimeError(f"HTTP {self.status_code}")

    def json(self) -> dict:
        return self.payload


class FakeSession:
    def __init__(self) -> None:
        self.post_call: dict | None = None
        self.get_call: dict | None = None

    def post(self, url: str, **kwargs) -> FakeResponse:
        self.post_call = {"url": url, **kwargs}
        return FakeResponse({"team": "team-1", "status": "trained"})

    def get(self, url: str, **kwargs) -> FakeResponse:
        self.get_call = {"url": url, **kwargs}
        return FakeResponse(
            {
                "team": "team-1",
                "predictions": [{"Name": "Player One", "predicted_EF": 12.5}],
            }
        )


class RemotePredictionServiceTests(unittest.TestCase):
    def test_remote_retrain_and_prediction_read(self) -> None:
        fake_session = FakeSession()
        payload = {
            "team_id": "team-1",
            "event_id": "event-1",
            "match_stats_id": "match-1",
            "documents": [],
        }
        with tempfile.TemporaryDirectory() as temporary, patch(
            "services.prediction_service._SESSION", fake_session
        ):
            service = PredictionService(
                store=ProjectStore(Path(temporary) / "projects"),
                base_url="http://prediction.internal:8101/",
                service_token="secret",
            )

            result = service.retrain("team-1", payload=payload)
            predictions = service.load_predictions("team-1")

        self.assertEqual("trained", result["status"])
        self.assertEqual(
            "http://prediction.internal:8101/teams/team-1/retrain",
            fake_session.post_call["url"],
        )
        self.assertEqual("Bearer secret", fake_session.post_call["headers"]["Authorization"])
        self.assertIsInstance(predictions, pd.DataFrame)
        self.assertEqual("Player One", predictions.iloc[0]["Name"])


if __name__ == "__main__":
    unittest.main()
