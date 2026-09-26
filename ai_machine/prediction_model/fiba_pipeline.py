from __future__ import annotations

import argparse
import json
from pathlib import Path

from config import ExtractionResult
from extractors import extract_all_pdfs
from features import clean_player_data, engineer_features, split_train_test_by_match
from model import export_csv, train_model
from pipeline import retrain_model, run_pipeline
import pipeline as _pipeline_state

__all__ = [
    "ExtractionResult",
    "extract_all_pdfs",
    "clean_player_data",
    "engineer_features",
    "split_train_test_by_match",
    "train_model",
    "export_csv",
    "retrain_model",
    "run_pipeline",
]


def __getattr__(name: str):
    if name.startswith("MASTER_"):
        return getattr(_pipeline_state, name)
    raise AttributeError(name)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="FIBA PDFs extraction + tuned RandomForest EF pipeline."
    )
    parser.add_argument("--data-dir", type=Path, default=Path(r"C:\Users\Acer\Desktop\data"))
    parser.add_argument("--output-csv", type=Path, default=Path.cwd() / "training_data.csv")
    parser.add_argument("--output-log-csv", type=Path, default=Path.cwd() / "processing_log.csv")
    parser.add_argument("--output-pred-csv", type=Path, default=Path.cwd() / "test_predictions.csv")
    args = parser.parse_args()

    summary = run_pipeline(args.data_dir, args.output_csv, args.output_log_csv, args.output_pred_csv)
    print(json.dumps(summary, indent=2, default=str))


if __name__ == "__main__":
    main()
