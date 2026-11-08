"""Index named test outcomes from a TRX report for focused CI qualification."""
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


class TrxEvidence:
    def __init__(self, root):
        self._results = root.findall(".//t:UnitTestResult", NS)
        if not self._results:
            raise ValueError("no UnitTestResult elements")
        for r in self._results:
            if not r.get("testId"):
                raise ValueError("missing test result identity")
            if r.get("outcome") not in ("Passed", "NotExecuted"):
                raise ValueError(f"unexpected outcome {r.get('outcome')!r}")
        mapping = {}
        methods = {}
        for d in root.findall(".//t:UnitTest", NS):
            method = d.find("t:TestMethod", NS)
            if method is None:
                continue
            tid = d.get("id")
            if not tid or not method.get("className"):
                raise ValueError("missing test definition identity or class")
            if tid in mapping and mapping[tid] != method.get("className"):
                raise ValueError(f"conflicting className for unit test {tid!r}")
            if tid in methods and methods[tid] != method.get("name"):
                raise ValueError(f"conflicting methodName for unit test {tid!r}")
            mapping[tid] = method.get("className")
            methods[tid] = method.get("name")
        self._mapping = mapping
        for r in self._results:
            if r.get("testId") not in mapping:
                raise ValueError(f"result {r.get('testId')!r} has no usable unit test definition")
        self._cases = {
            tid: f"{mapping[tid]}.{methods[tid]}"
            for tid in mapping
            if methods.get(tid)
        }
        validate_summary(root, len(self._results), self.passed_count,
                         sum(result.get("outcome") == "NotExecuted" for result in self._results))

    def outcomes_for(self, class_name):
        return tuple(
            r.get("outcome")
            for r in self._results
            if self._mapping.get(r.get("testId")) == class_name
        )

    def outcomes_for_case(self, case_name):
        """Every result row whose full case name matches, in document order."""
        targets = {tid for tid, full in self._cases.items() if full == case_name}
        return tuple(
            r.get("outcome")
            for r in self._results
            if r.get("testId") in targets
        )

    @property
    def passed_count(self):
        return sum(1 for r in self._results if r.get("outcome") == "Passed")


def _ascii_int(text):
    """True only for a nonempty string of ASCII digits."""
    return bool(text) and all(c in "0123456789" for c in text)


def validate_summary(root, result_count, passed_count, skipped_count):
    """Admit only a completed run whose summary agrees with its executed rows."""
    summaries = [e for e in root if e.tag == f"{{{NS['t']}}}ResultSummary"]
    if len(summaries) != 1:
        raise ValueError(f"expected exactly one ResultSummary, found {len(summaries)}")
    section = summaries[0]
    if section.get("outcome") != "Completed":
        raise ValueError(f"run outcome {section.get('outcome')!r} is not Completed")
    counters = [e for e in section if e.tag == f"{{{NS['t']}}}Counters"]
    if len(counters) != 1:
        raise ValueError(f"expected exactly one Counters child, found {len(counters)}")
    counts = counters[0].attrib
    required = ("total", "executed", "passed", "failed", "error", "timeout",
                "aborted", "inconclusive", "passedButRunAborted", "notRunnable",
                "notExecuted", "disconnected", "warning", "completed",
                "inProgress", "pending")
    for name in required:
        if not _ascii_int(counts.get(name)):
            raise ValueError(f"counter {name!r} is missing or not an ASCII integer")
    total, executed, passed, not_executed = (
        int(counts["total"]), int(counts["executed"]),
        int(counts["passed"]), int(counts["notExecuted"]),
    )
    if total != result_count:
        raise ValueError(f"total {total} does not match {result_count} results")
    if passed != passed_count:
        raise ValueError(f"passed {passed} does not match {passed_count} results")
    # VSTest may leave notExecuted at zero for platform skips. The actual rows,
    # total and executed counters still have to account for every test.
    if not_executed not in (0, skipped_count) or result_count != passed_count + skipped_count:
        raise ValueError(f"notExecuted {not_executed} is inconsistent with {skipped_count} skips")
    if executed != passed_count:
        raise ValueError(f"executed {executed} does not match {passed_count} executed results")
    for name in ("failed", "error", "timeout", "aborted", "inconclusive",
                 "passedButRunAborted", "notRunnable", "disconnected",
                 "warning", "inProgress", "pending"):
        if int(counts[name]) != 0:
            raise ValueError(f"counter {name!r} must be zero, found {counts[name]}")
    return None
