from __future__ import annotations

from dataclasses import dataclass
from itertools import combinations

from tools.vocabulary.normalization import normalize_word


@dataclass(frozen=True, slots=True)
class ConfusableCandidate:
    source_word: str
    target_word: str
    kind: str
    score: float
    evidence: str


@dataclass(frozen=True, slots=True)
class CandidateGeneration:
    candidates: tuple[ConfusableCandidate, ...]
    metrics: dict[str, int]


def _shared_edge(left: str, right: str) -> int:
    prefix = 0
    for a, b in zip(left, right):
        if a != b:
            break
        prefix += 1
    suffix = 0
    for a, b in zip(reversed(left), reversed(right)):
        if a != b:
            break
        suffix += 1
    return max(prefix, suffix)


def _edit_distance_at_most_two(left: str, right: str) -> int | None:
    if abs(len(left) - len(right)) > 2:
        return None
    previous = list(range(len(right) + 1))
    for index, a in enumerate(left, 1):
        current = [index]
        row_min = index
        for other_index, b in enumerate(right, 1):
            value = min(current[-1] + 1, previous[other_index] + 1, previous[other_index - 1] + (a != b))
            current.append(value)
            row_min = min(row_min, value)
        if row_min > 2:
            return None
        previous = current
    return previous[-1] if previous[-1] <= 2 else None


def generate_candidates_with_metrics(words: list[str]) -> CandidateGeneration:
    """Generate conservative spelling candidates; never publish them automatically."""
    normalized = sorted({normalize_word(word) for word in words if normalize_word(word)})
    buckets: dict[tuple[str, int], list[str]] = {}
    for word in normalized:
        for token in {word[:2], word[-5:] if len(word) >= 5 else word}:
            buckets.setdefault((token, len(word) // 3), []).append(word)
    pairs: set[tuple[str, str]] = set()
    bucket_oversize = 0
    for members in buckets.values():
        if len(members) > 150:
            bucket_oversize += len(set(members)) * (len(set(members)) - 1) // 2
            continue
        pairs.update(combinations(sorted(set(members)), 2))
    candidates = []
    distance_filtered = 0
    edge_filtered = 0
    for left, right in sorted(pairs):
        edge = _shared_edge(left, right)
        distance = _edit_distance_at_most_two(left, right)
        if edge < 2:
            edge_filtered += 1
            continue
        if distance is None:
            distance_filtered += 1
            continue
        score = round(min(0.89, 0.55 + (2 - distance) * 0.1 + edge / max(len(left), len(right)) / 4), 4)
        candidates.extend(
            (
                ConfusableCandidate(left, right, "spelling_similar", score, f"edit_distance:{distance};shared_edge:{edge}"),
                ConfusableCandidate(right, left, "spelling_similar", score, f"edit_distance:{distance};shared_edge:{edge}"),
            )
        )
    undirected: dict[tuple[str, str], tuple[float, str]] = {}
    for candidate in candidates:
        key = tuple(sorted((candidate.source_word, candidate.target_word)))
        undirected[key] = max(undirected.get(key, (0.0, "")), (candidate.score, candidate.evidence))
    degree: dict[str, int] = {word: 0 for word in normalized}
    bounded = []
    degree_filtered = 0
    for (left, right), (score, evidence) in sorted(undirected.items(), key=lambda item: (-item[1][0], item[0])):
        if degree[left] >= 4 or degree[right] >= 4:
            degree_filtered += 1
            continue
        degree[left] += 1
        degree[right] += 1
        bounded.extend(
            (
                ConfusableCandidate(left, right, "spelling_similar", score, evidence),
                ConfusableCandidate(right, left, "spelling_similar", score, evidence),
            )
        )
    result = tuple(sorted(bounded, key=lambda item: (item.source_word, item.target_word)))
    return CandidateGeneration(result, {
        "bucket_pairs_considered": len(pairs),
        "bucket_oversize_filtered_pairs": bucket_oversize,
        "shared_edge_filtered_pairs": edge_filtered,
        "edit_distance_filtered_pairs": distance_filtered,
        "degree_cap_filtered_pairs": degree_filtered,
        "retained_directed_candidates": len(result),
    })


def generate_candidates(words: list[str]) -> tuple[ConfusableCandidate, ...]:
    return generate_candidates_with_metrics(words).candidates
