#!/usr/bin/env python3
"""Regenerate WordFlow's frozen FSRS-6 vectors from pinned py-fsrs."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

PINNED_COMMIT = "854a51496993d088175f6d7da0bbfaa414034c61"
PINNED_RELEASE = "v6.3.0"


def require_pinned_checkout(root: Path) -> None:
    actual = subprocess.run(
        ["git", "-C", str(root), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    if actual != PINNED_COMMIT:
        raise SystemExit(f"expected py-fsrs {PINNED_COMMIT}, got {actual}")


def canonical_json(payload: dict[str, object]) -> bytes:
    return (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode("utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--py-fsrs-root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    root = args.py_fsrs_root.resolve()
    require_pinned_checkout(root)
    sys.path.insert(0, str(root))

    from fsrs import Rating, Scheduler  # pylint: disable=import-outside-toplevel

    scheduler = Scheduler(learning_steps=(), relearning_steps=(), enable_fuzzing=False)
    base = datetime(2026, 1, 15, 12, tzinfo=timezone.utc)

    def retrievability(stability: float, elapsed_days: float) -> float:
        return (1 + scheduler._FACTOR * elapsed_days / stability) ** scheduler._DECAY

    def update(previous, rating: Rating, reviewed_at: datetime, retention: float):
        scheduler.desired_retention = retention
        if previous is None:
            difficulty = scheduler._initial_difficulty(rating=rating, clamp=True)
            stability = scheduler._initial_stability(rating=rating)
            before = 0.0
        else:
            old_difficulty, old_stability, last_review = previous
            elapsed_days = (reviewed_at - last_review).total_seconds() / 86_400
            before = retrievability(old_stability, elapsed_days)
            difficulty = scheduler._next_difficulty(
                difficulty=old_difficulty, rating=rating
            )
            if elapsed_days < 1:
                stability = scheduler._short_term_stability(
                    stability=old_stability, rating=rating
                )
            else:
                stability = scheduler._next_stability(
                    difficulty=old_difficulty,
                    stability=old_stability,
                    retrievability=before,
                    rating=rating,
                )
        interval_days = scheduler._next_interval(stability=stability)
        return (difficulty, stability, reviewed_at), before, interval_days

    def vector(previous, rating: Rating, reviewed_at: datetime, retention: float):
        state, before, interval_days = update(
            previous, rating, reviewed_at, retention
        )
        return {
            "difficulty": state[0],
            "due_at": (reviewed_at + timedelta(days=interval_days)).isoformat(),
            "interval_days": interval_days,
            "rating": int(rating),
            "retention": retention,
            "retrievability_before_review": before,
            "reviewed_at": reviewed_at.isoformat(),
            "stability_days": state[1],
        }

    good_state = update(None, Rating.Good, base, 0.9)[0]
    delayed_at = base + timedelta(days=10, hours=12)
    same_day_at = base + timedelta(minutes=10)

    public_same_day_elapsed = (same_day_at - base).days
    public_delayed_elapsed = (delayed_at - base).days
    public_delayed_retrievability = retrievability(
        good_state[1], public_delayed_elapsed
    )
    public_delayed_stability = scheduler._next_stability(
        difficulty=good_state[0],
        stability=good_state[1],
        retrievability=public_delayed_retrievability,
        rating=Rating.Good,
    )

    payload = {
        "adaptation": "WordFlow UTC TotalDays supplied to pinned private helpers",
        "defaults": list(scheduler.parameters),
        "provenance": {
            "commit": PINNED_COMMIT,
            "release": PINNED_RELEASE,
            "repository": "https://github.com/open-spaced-repetition/py-fsrs",
        },
        "public_wrapper_integer_day_comparison": {
            "delayed_10_5d_elapsed_days": public_delayed_elapsed,
            "delayed_10_5d_retrievability": public_delayed_retrievability,
            "delayed_10_5d_stability_days": public_delayed_stability,
            "same_day_10m_elapsed_days": public_same_day_elapsed,
            "same_day_10m_retrievability": retrievability(
                good_state[1], public_same_day_elapsed
            ),
        },
        "vectors": {
            "delayed_good_10_5d": vector(
                good_state, Rating.Good, delayed_at, 0.9
            ),
            "exact_24h_good": vector(
                good_state, Rating.Good, base + timedelta(days=1), 0.9
            ),
            "initial_again": vector(None, Rating.Again, base, 0.9),
            "initial_good": vector(None, Rating.Good, base, 0.9),
            "initial_hard": vector(None, Rating.Hard, base, 0.9),
            "lapse_again_30d": vector(
                good_state, Rating.Again, base + timedelta(days=30), 0.9
            ),
            "retention_0_85": vector(None, Rating.Good, base, 0.85),
            "retention_0_90": vector(None, Rating.Good, base, 0.90),
            "retention_0_95": vector(None, Rating.Good, base, 0.95),
            "same_day_hard_10m": vector(
                good_state, Rating.Hard, same_day_at, 0.9
            ),
        },
    }

    encoded = canonical_json(payload)
    if args.output:
        args.output.write_bytes(encoded)
    else:
        sys.stdout.buffer.write(encoded)
    print(f"sha256={hashlib.sha256(encoded).hexdigest().upper()}", file=sys.stderr)


if __name__ == "__main__":
    main()
