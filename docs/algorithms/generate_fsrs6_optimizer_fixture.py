#!/usr/bin/env python3
"""Generate the optimizer log-loss fixture from pinned py-fsrs formulas."""

from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

PINNED_COMMIT = "854a51496993d088175f6d7da0bbfaa414034c61"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--py-fsrs-root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = args.py_fsrs_root.resolve()
    actual = subprocess.run(
        ["git", "-C", str(root), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    if actual != PINNED_COMMIT:
        raise SystemExit(f"expected py-fsrs {PINNED_COMMIT}, got {actual}")
    sys.path.insert(0, str(root))

    from fsrs import Rating, Scheduler  # pylint: disable=import-outside-toplevel

    scheduler = Scheduler(learning_steps=(), relearning_steps=(), enable_fuzzing=False)
    base = datetime(2025, 1, 1, tzinfo=timezone.utc)

    def retrievability(stability: float, elapsed_days: float) -> float:
        return (1 + scheduler._FACTOR * elapsed_days / stability) ** scheduler._DECAY

    difficulty = scheduler._initial_difficulty(rating=Rating.Good, clamp=True)
    stability = scheduler._initial_stability(rating=Rating.Good)
    predictions = []
    losses = []
    previous = base
    for reviewed_at, rating in (
        (base + timedelta(days=10, hours=12), Rating.Good),
        (base + timedelta(days=40, hours=12), Rating.Again),
    ):
        elapsed_days = (reviewed_at - previous).total_seconds() / 86_400
        probability = retrievability(stability, elapsed_days)
        predictions.append(probability)
        losses.append(-math.log(probability if rating != Rating.Again else 1 - probability))
        next_difficulty = scheduler._next_difficulty(difficulty=difficulty, rating=rating)
        stability = scheduler._next_stability(
            difficulty=difficulty,
            stability=stability,
            retrievability=probability,
            rating=rating,
        )
        difficulty = next_difficulty
        previous = reviewed_at

    payload = {
        "adaptation": "continuous UTC TotalDays",
        "mean_binary_recall_log_loss": sum(losses) / len(losses),
        "predictions": predictions,
        "provenance": {
            "commit": PINNED_COMMIT,
            "repository": "https://github.com/open-spaced-repetition/py-fsrs",
        },
        "ratings": [3, 3, 1],
        "reviewed_at": [
            base.isoformat(),
            (base + timedelta(days=10, hours=12)).isoformat(),
            (base + timedelta(days=40, hours=12)).isoformat(),
        ],
    }
    encoded = (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode()
    if args.output:
        args.output.write_bytes(encoded)
    else:
        sys.stdout.buffer.write(encoded)


if __name__ == "__main__":
    main()
