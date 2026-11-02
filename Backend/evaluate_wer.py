"""Word-error-rate evaluation for the speech-recognition stage (FR-02).

The backend writes one JSON object per final Deepgram transcript to
Backend/Logs/transcripts.jsonl while metrics are enabled (the default in DEV_MODE).
This script compares those transcripts, in order, with a reference list and reports
the word error rate for each utterance and for the corpus.

Usage:
    python Backend/evaluate_wer.py --references phrases.txt
    python Backend/evaluate_wer.py --references phrases.txt --transcripts Backend/Logs/transcripts.jsonl
    python Backend/evaluate_wer.py --references phrases.txt --json results.json

The reference file holds one spoken phrase per line, in the order the phrases were
spoken. Blank lines and lines starting with '#' are ignored.
"""

import argparse
import json
import os
import re
import string
import sys

DEFAULT_TRANSCRIPTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Logs", "transcripts.jsonl")

PUNCTUATION = str.maketrans("", "", string.punctuation)


def normalise(text):
    text = text.lower().translate(PUNCTUATION)
    return [word for word in re.split(r"\s+", text) if word]


def align_counts(reference, hypothesis):
    """Recounts operations from the alignment so the reported split matches the distance."""
    if not reference:
        return 0, 0, len(hypothesis)
    if not hypothesis:
        return 0, len(reference), 0

    rows = len(reference)
    cols = len(hypothesis)
    cost = [[0] * (cols + 1) for _ in range(rows + 1)]
    for i in range(rows + 1):
        cost[i][0] = i
    for j in range(cols + 1):
        cost[0][j] = j
    for i in range(1, rows + 1):
        for j in range(1, cols + 1):
            cost[i][j] = min(
                cost[i - 1][j - 1] + (reference[i - 1] != hypothesis[j - 1]),
                cost[i - 1][j] + 1,
                cost[i][j - 1] + 1,
            )

    substitutions = deletions = insertions = 0
    i, j = rows, cols
    while i > 0 or j > 0:
        if i > 0 and j > 0 and cost[i][j] == cost[i - 1][j - 1] + (reference[i - 1] != hypothesis[j - 1]):
            if reference[i - 1] != hypothesis[j - 1]:
                substitutions += 1
            i, j = i - 1, j - 1
        elif i > 0 and cost[i][j] == cost[i - 1][j] + 1:
            deletions += 1
            i -= 1
        else:
            insertions += 1
            j -= 1
    return substitutions, deletions, insertions


def read_references(path):
    references = []
    with open(path, "r", encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#"):
                references.append(line)
    return references


def read_transcripts(path):
    transcripts = []
    with open(path, "r", encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                data = json.loads(line)
            except json.JSONDecodeError:
                transcripts.append(line)
                continue
            if isinstance(data, dict):
                text = data.get("english") or data.get("transcript") or ""
            else:
                text = str(data)
            if text:
                transcripts.append(text)
    return transcripts


def main():
    # Phrases may contain characters the Windows console cannot represent in its default code page
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description="Compute word error rate for the speech-recognition stage.")
    parser.add_argument("--references", required=True, help="File with one reference phrase per line.")
    parser.add_argument("--transcripts", default=DEFAULT_TRANSCRIPTS, help="JSONL transcript log written by the backend.")
    parser.add_argument("--json", dest="json_path", help="Optional path for a machine-readable summary.")
    args = parser.parse_args()

    references = read_references(args.references)
    if not references:
        sys.exit("The reference file contains no phrases.")
    if not os.path.exists(args.transcripts):
        sys.exit(f"No transcript log at {args.transcripts}. Run the backend in DEV_MODE and speak the phrases first.")

    transcripts = read_transcripts(args.transcripts)
    if not transcripts:
        sys.exit(f"The transcript log at {args.transcripts} is empty.")

    pairs = min(len(references), len(transcripts))
    if len(references) != len(transcripts):
        print(f"Warning: {len(transcripts)} transcript(s) for {len(references)} reference phrase(s); comparing the first {pairs}.")

    total_words = total_errors = total_substitutions = total_deletions = total_insertions = 0
    rows = []
    for index in range(pairs):
        reference = references[index]
        hypothesis = transcripts[index]
        reference_words = normalise(reference)
        hypothesis_words = normalise(hypothesis)
        substitutions, deletions, insertions = align_counts(reference_words, hypothesis_words)
        errors = substitutions + deletions + insertions
        wer = errors / len(reference_words) if reference_words else 0.0

        total_words += len(reference_words)
        total_errors += errors
        total_substitutions += substitutions
        total_deletions += deletions
        total_insertions += insertions
        rows.append({
            "index": index + 1,
            "reference": reference,
            "hypothesis": hypothesis,
            "reference_words": len(reference_words),
            "substitutions": substitutions,
            "deletions": deletions,
            "insertions": insertions,
            "wer": round(wer, 4),
        })
        print(f"{index + 1:3d}. WER {wer * 100:6.2f}%  S={substitutions} D={deletions} I={insertions}")
        print(f"     ref: {reference}")
        print(f"     hyp: {hypothesis}")

    corpus_wer = (total_errors / total_words) if total_words else 0.0
    print()
    print(f"Utterances: {pairs}")
    print(f"Reference words: {total_words}")
    print(f"Substitutions: {total_substitutions}  Deletions: {total_deletions}  Insertions: {total_insertions}")
    print(f"Corpus WER: {corpus_wer * 100:.2f}%  (errors {total_errors} / words {total_words})")
    print("FR-02 target: below 10.00% -> " + ("PASS" if corpus_wer < 0.10 else "FAIL"))

    if args.json_path:
        summary = {
            "utterances": pairs,
            "reference_words": total_words,
            "substitutions": total_substitutions,
            "deletions": total_deletions,
            "insertions": total_insertions,
            "wer": round(corpus_wer, 4),
            "target": 0.10,
            "passed": corpus_wer < 0.10,
            "rows": rows,
        }
        with open(args.json_path, "w", encoding="utf-8") as f:
            json.dump(summary, f, indent=2)
        print(f"Summary written to {args.json_path}")


if __name__ == "__main__":
    main()
